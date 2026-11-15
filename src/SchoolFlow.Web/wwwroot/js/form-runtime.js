// Renders a form definition and keeps it live: every change is sent to the server, which
// evaluates the field logic (show/hide, calculations, required, validation) and sends back
// the new state. The same server code runs again on submit, so the browser can never
// disagree with what is finally stored.
(function () {
  'use strict';
  const { h, api, debounce } = window.SF;

  const lookupCache = new Map();
  function loadLookup(source, query) {
    const key = source + '\n' + (query || '');
    if (!lookupCache.has(key)) {
      const promise = api('GET', '/api/lookup?source=' + encodeURIComponent(source) + (query ? '&q=' + encodeURIComponent(query) : ''))
        .then((data) => data.items || []);
      // Do not cache failures, so a retry can succeed.
      promise.catch(() => lookupCache.delete(key));
      lookupCache.set(key, promise);
    }
    return lookupCache.get(key);
  }

  function formatValue(value, format, decimals, type) {
    if (value === null || value === undefined || value === '') return '—';
    if (typeof value === 'boolean') return value ? 'Yes' : 'No';
    if (typeof value === 'number') {
      const currency = format === 'currency' || type === 'currency';
      const places = Number.isInteger(decimals) ? decimals : (currency ? 2 : undefined);
      try {
        return new Intl.NumberFormat('en-US', Object.assign(
          currency ? { style: 'currency', currency: 'USD' } : {},
          places === undefined ? { maximumFractionDigits: 6 } : { minimumFractionDigits: places, maximumFractionDigits: places },
        )).format(value);
      } catch (e) {
        return String(value);
      }
    }
    if (Array.isArray(value)) return value.map((v) => formatValue(v)).join(', ');
    if (typeof value === 'object') return value.name || value.id || '';
    return String(value);
  }

  let uid = 0;

  /**
   * @param root      element to render into
   * @param options   { definition, evaluate(values, init) → Promise<state>, submit(values) → Promise<{ok, state, runId}>,
   *                    onSuccess(runId), preview, initialValues (answers to prefill, e.g. from a previous request) }
   */
  function mountForm(root, options) {
    const definition = options.definition || {};
    const fields = (definition.fields || []).filter((f) => f && f.key);
    const values = {};
    const touched = new Set();
    const controls = {};
    let showAllErrors = false;
    let sequence = 0;
    let applied = 0;
    let submitting = false;
    let destroyed = false;

    root.textContent = '';
    const banner = h('div', { class: 'sf-banner', hidden: true, role: 'alert' });
    const grid = h('div', { class: 'sf-grid' });
    const submitButton = h('button', { type: 'submit', class: 'btn primary lg' }, definition.submitLabel || 'Submit');
    const busy = h('span', { class: 'sf-busy muted', hidden: true }, 'Checking…');
    const form = h('form', { class: 'sf-form', novalidate: true, onsubmit: onSubmit },
      definition.intro ? h('p', { class: 'sf-intro' }, definition.intro) : null,
      banner, grid,
      h('div', { class: 'sf-submit-row' }, submitButton, busy,
        options.preview ? h('span', { class: 'muted' }, 'Preview — nothing is submitted.') : null));
    root.appendChild(form);

    if (fields.length === 0) {
      grid.appendChild(h('p', { class: 'muted' }, 'This form has no fields yet.'));
    }
    for (const field of fields) {
      const control = buildField(field);
      controls[field.key] = control;
      grid.appendChild(control.wrap);
    }

    const scheduleEvaluate = debounce(() => evaluate(false), 220);

    function changed(key, immediate) {
      touched.add(key);
      if (immediate) { scheduleEvaluate.cancel(); evaluate(false); } else scheduleEvaluate();
    }

    async function evaluate(init) {
      const mine = ++sequence;
      busy.hidden = false;
      try {
        // The first call carries any prefill; the server normalises it and applies defaults to the rest.
        const state = await options.evaluate(init ? Object.assign({}, options.initialValues || {}) : values, init);
        if (destroyed || mine < applied) return; // a newer answer has already been applied
        applied = mine;
        apply(state, init);
        if (!showAllErrors) setBanner(null);
      } catch (e) {
        if (!destroyed && mine === sequence) setBanner(e.message);
      } finally {
        if (mine === sequence) busy.hidden = true;
      }
    }

    function setBanner(message) {
      banner.hidden = !message;
      banner.textContent = message || '';
    }

    function apply(state, init) {
      for (const field of fields) {
        const control = controls[field.key];
        const fieldState = (state.fields || {})[field.key] || { visible: true, required: false, errors: [] };
        const value = (state.values || {})[field.key];
        control.wrap.hidden = !fieldState.visible;
        if (control.required) control.required.hidden = !fieldState.required;
        if (init && control.set && value !== null && value !== undefined) control.set(value);
        if (control.update) control.update(value, fieldState);

        const always = field.type === 'calculated';
        const show = always || showAllErrors || touched.has(field.key);
        // Formula problems are the builder's mistake, not the user's: always show them.
        const errors = (fieldState.errors || []).filter((e) => show || /formula/i.test(e));
        control.error.textContent = errors.join(' ');
        control.error.hidden = errors.length === 0;
        control.wrap.classList.toggle('has-error', errors.length > 0);
      }
    }

    async function onSubmit(event) {
      event.preventDefault();
      if (submitting) return;
      submitting = true;
      showAllErrors = true;
      scheduleEvaluate.cancel();
      submitButton.disabled = true;
      setBanner(null);
      try {
        const mine = ++sequence;
        const result = await options.submit(values);
        applied = mine;
        apply(result.state, false);
        if (result.ok) {
          if (options.onSuccess) options.onSuccess(result.runId);
          else setBanner(null);
          return;
        }
        setBanner('Please fix the highlighted fields and submit again.');
        const firstError = grid.querySelector('.has-error:not([hidden]), .sf-cell-error:not([hidden])');
        if (firstError) firstError.scrollIntoView({ behavior: 'smooth', block: 'center' });
      } catch (e) {
        setBanner(e.message);
      } finally {
        submitting = false;
        submitButton.disabled = false;
      }
    }

    // ---- field controls ------------------------------------------------------------

    function buildField(field) {
      const id = 'sf-' + (++uid);
      const error = h('div', { class: 'sf-error', hidden: true, id: id + '-err' });
      if (field.type === 'section') {
        const wrap = h('div', { class: 'sf-field sf-section' },
          h('h3', null, field.label || ''), field.help ? h('p', { class: 'sf-help' }, field.help) : null, error);
        return { wrap, error };
      }

      const required = h('span', { class: 'sf-req', hidden: true, title: 'Required' }, '*');
      const control = { error, required };
      let body;
      let labelFor = id;

      switch (field.type) {
        case 'textarea':
          body = input(h('textarea', { id, rows: 4, placeholder: field.placeholder || null, maxlength: 10000 }), field.key, control);
          break;
        case 'number':
        case 'currency': {
          const el = input(h('input', { id, type: 'text', inputmode: 'decimal', autocomplete: 'off', placeholder: field.placeholder || null }), field.key, control);
          body = field.type === 'currency' ? h('div', { class: 'sf-prefix' }, h('span', null, '$'), el) : el;
          break;
        }
        case 'date':
          body = input(h('input', { id, type: 'date' }), field.key, control, true);
          break;
        case 'email':
          body = input(h('input', { id, type: 'email', autocomplete: 'email', placeholder: field.placeholder || null }), field.key, control);
          break;
        case 'select': {
          const el = h('select', { id }, h('option', { value: '' }, 'Choose…'), (field.options || []).map((o) => h('option', { value: o }, o)));
          body = input(el, field.key, control, true);
          break;
        }
        case 'radio': {
          labelFor = null;
          const name = id;
          const radios = (field.options || []).map((o) => h('input', { type: 'radio', name, value: o,
            onchange: () => { values[field.key] = o; changed(field.key, true); } }));
          body = h('div', { class: 'sf-options', role: 'radiogroup' }, radios.map((r) => h('label', { class: 'sf-option' }, r, h('span', null, r.value))));
          control.set = (v) => { radios.forEach((r) => { r.checked = String(v).toLowerCase() === r.value.toLowerCase(); }); values[field.key] = v; };
          break;
        }
        case 'multiselect': {
          labelFor = null;
          const boxes = (field.options || []).map((o) => h('input', { type: 'checkbox', value: o, onchange: collect }));
          function collect() {
            values[field.key] = boxes.filter((b) => b.checked).map((b) => b.value);
            changed(field.key, true);
          }
          body = h('div', { class: 'sf-options' }, boxes.map((b) => h('label', { class: 'sf-option' }, b, h('span', null, b.value))));
          control.set = (v) => {
            const chosen = (Array.isArray(v) ? v : []).map((x) => String(x).toLowerCase());
            boxes.forEach((b) => { b.checked = chosen.includes(b.value.toLowerCase()); });
            values[field.key] = boxes.filter((b) => b.checked).map((b) => b.value);
          };
          break;
        }
        case 'checkbox': {
          const box = h('input', { id, type: 'checkbox', onchange: () => { values[field.key] = box.checked; changed(field.key, true); } });
          control.set = (v) => { box.checked = v === true; values[field.key] = box.checked; };
          const wrap = h('div', { class: 'sf-field' + (field.width === 'half' ? ' half' : '') },
            h('label', { class: 'sf-check', for: id }, box, h('span', null, field.label || '', ' ', required)),
            field.help ? h('p', { class: 'sf-help' }, field.help) : null, error);
          control.wrap = wrap;
          return control;
        }
        case 'lookup':
          body = field.source === 'requesters' ? buildPersonLookup(field, id, control) : buildLookupSelect(field, id, control);
          break;
        case 'calculated': {
          labelFor = null;
          const out = h('output', { class: 'sf-calc' }, '—');
          control.update = (value) => { out.textContent = formatValue(value, field.format, field.decimals); };
          body = out;
          break;
        }
        case 'table':
          labelFor = null;
          body = buildTable(field, control);
          break;
        default:
          body = input(h('input', { id, type: 'text', placeholder: field.placeholder || null, maxlength: 10000 }), field.key, control);
      }

      const wide = field.type === 'table' || field.type === 'textarea';
      control.wrap = h('div', { class: 'sf-field' + (field.width === 'half' && !wide ? ' half' : '') + (field.type === 'calculated' ? ' is-calc' : '') },
        h('label', { class: 'sf-label', for: labelFor }, field.label || field.key, ' ', required),
        body,
        field.help ? h('p', { class: 'sf-help' }, field.help) : null,
        error);
      return control;
    }

    function input(el, key, control, immediate) {
      el.addEventListener('input', () => { values[key] = el.value; if (!immediate) scheduleEvaluate(); });
      el.addEventListener('change', () => { values[key] = el.value; changed(key, true); });
      el.addEventListener('blur', () => { if (!touched.has(key)) changed(key, true); });
      control.set = (v) => {
        el.value = typeof v === 'object' ? '' : String(v);
        values[key] = el.value;
      };
      return el;
    }

    function buildLookupSelect(field, id, control) {
      const select = h('select', { id, disabled: true }, h('option', { value: '' }, 'Loading…'));
      let items = [];
      select.addEventListener('change', () => {
        const item = items.find((i) => String(i.id) === select.value);
        values[field.key] = item ? { id: item.id, name: item.name, email: item.email || undefined } : null;
        changed(field.key, true);
      });
      let wanted = null; // a prefilled choice waiting for the list to load
      let loaded = false;
      const choose = (id) => {
        const item = items.find((i) => String(i.id) === String(id));
        select.value = item ? String(item.id) : '';
        const before = values[field.key];
        values[field.key] = item ? { id: item.id, name: item.name, email: item.email || undefined } : null;
        // A prefilled choice that no longer exists in Freshservice is dropped; let the server know.
        if (!item && before) scheduleEvaluate();
      };
      control.set = (v) => {
        if (!v || typeof v !== 'object' || v.id === undefined || v.id === null) return;
        values[field.key] = { id: v.id, name: v.name, email: v.email || undefined };
        if (loaded) choose(v.id); else wanted = v.id;
      };
      loadLookup(field.source).then((result) => {
        items = result;
        select.textContent = '';
        select.appendChild(h('option', { value: '' }, items.length ? 'Choose…' : 'Nothing to choose from'));
        for (const item of items) {
          select.appendChild(h('option', { value: String(item.id) }, item.detail ? item.name + ' (' + item.detail + ')' : item.name));
        }
        select.disabled = false;
        loaded = true;
        if (wanted !== null) { choose(wanted); wanted = null; }
      }).catch((e) => {
        select.textContent = '';
        select.appendChild(h('option', { value: '' }, 'Could not load choices'));
        control.error.textContent = e.message;
        control.error.hidden = false;
      });
      return select;
    }

    function buildPersonLookup(field, id, control) {
      const box = h('input', { id, type: 'text', autocomplete: 'off', placeholder: field.placeholder || 'Start typing a name…', role: 'combobox', 'aria-expanded': 'false' });
      const list = h('div', { class: 'sf-suggest', hidden: true, role: 'listbox' });
      let latest = 0;
      const search = debounce(async () => {
        const query = box.value.trim();
        const mine = ++latest;
        if (query.length < 2) { list.hidden = true; return; }
        try {
          const items = await loadLookup(field.source, query);
          if (mine !== latest) return;
          list.textContent = '';
          if (items.length === 0) list.appendChild(h('div', { class: 'sf-suggest-empty' }, 'No matches'));
          for (const item of items.slice(0, 12)) {
            list.appendChild(h('button', { type: 'button', class: 'sf-suggest-item', role: 'option', onclick: () => choose(item) },
              h('strong', null, item.name), item.email ? h('small', null, item.email) : null));
          }
          list.hidden = false;
          box.setAttribute('aria-expanded', 'true');
        } catch (e) {
          control.error.textContent = e.message;
          control.error.hidden = false;
        }
      }, 250);
      function choose(item) {
        values[field.key] = { id: item.id, name: item.name, email: item.email || undefined };
        box.value = item.name;
        close();
        changed(field.key, true);
      }
      function close() { list.hidden = true; box.setAttribute('aria-expanded', 'false'); }
      control.set = (v) => {
        if (!v || typeof v !== 'object' || !v.name || v.id === undefined || v.id === null) return;
        values[field.key] = { id: v.id, name: v.name, email: v.email || undefined };
        box.value = v.name;
      };
      box.addEventListener('input', () => {
        // Typing after a choice clears it: only a picked person counts as a value.
        if (values[field.key]) { values[field.key] = null; changed(field.key, false); }
        search();
      });
      box.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
      box.addEventListener('blur', () => setTimeout(() => {
        close();
        if (!values[field.key]) box.value = '';
        if (!touched.has(field.key)) changed(field.key, true);
      }, 180));
      return h('div', { class: 'sf-typeahead' }, box, list);
    }

    function buildTable(field, control) {
      const columns = (field.columns || []).filter((c) => c && c.key);
      const rows = []; // { id, data, cells: {colKey: {el, error, out}}, tr }
      const tbody = h('tbody');
      const addButton = h('button', { type: 'button', class: 'btn small', onclick: () => { addRow(); sync(true); } }, '+ Add row');
      const table = h('table', { class: 'sf-table' },
        h('thead', null, h('tr', null, columns.map((c) => h('th', { class: 'col-' + c.type }, c.label || c.key, c.required ? h('span', { class: 'sf-req' }, ' *') : null)), h('th', { class: 'col-x' }))),
        tbody);

      // Rows the user has not typed anything into are not sent, so an untouched starter row never
      // counts as a row. Each row remembers its position in what was sent, to match the server's answer.
      function collect() {
        const sent = [];
        for (const row of rows) {
          const blank = Object.values(row.data).every((v) => v === '' || v === false || v === null || v === undefined);
          row.index = blank ? -1 : sent.push(row.data) - 1;
        }
        values[field.key] = sent;
      }

      function sync(immediate) {
        collect();
        addButton.disabled = Number.isInteger(field.maxRows) && rows.length >= field.maxRows;
        changed(field.key, immediate);
      }

      function addRow(initial) {
        const row = { id: ++uid, data: {}, cells: {} };
        const tds = columns.map((col) => {
          const cellError = h('div', { class: 'sf-cell-error', hidden: true });
          const cell = { error: cellError, touched: false };
          let el;
          const start = initial ? initial[col.key] : undefined;
          const set = (v) => { row.data[col.key] = v; };
          const touch = () => { cell.touched = true; };
          if (col.type === 'calculated') {
            el = h('output', { class: 'sf-calc small' }, '—');
            cell.out = el;
          } else if (col.type === 'select') {
            el = h('select', { 'aria-label': col.label }, h('option', { value: '' }, '—'), (col.options || []).map((o) => h('option', { value: o }, o)));
            if (start) { el.value = start; set(el.value); }
            el.addEventListener('change', () => { set(el.value); touch(); sync(true); });
          } else if (col.type === 'checkbox') {
            el = h('input', { type: 'checkbox', 'aria-label': col.label });
            if (start === true) { el.checked = true; set(true); }
            el.addEventListener('change', () => { set(el.checked); touch(); sync(true); });
          } else {
            const numeric = col.type === 'number' || col.type === 'currency';
            el = h('input', { type: col.type === 'date' ? 'date' : 'text', inputmode: numeric ? 'decimal' : null, 'aria-label': col.label, autocomplete: 'off' });
            if (start !== undefined && start !== null && typeof start !== 'object') { el.value = String(start); set(el.value); }
            el.addEventListener('input', () => { set(el.value); sync(false); });
            el.addEventListener('change', () => { set(el.value); touch(); sync(true); });
            el.addEventListener('blur', touch);
          }
          row.cells[col.key] = cell;
          return h('td', { class: 'col-' + col.type, dataset: { label: col.label || col.key } }, el, cellError);
        });
        const remove = h('button', { type: 'button', class: 'icon-btn', title: 'Remove row', 'aria-label': 'Remove row',
          onclick: () => { rows.splice(rows.indexOf(row), 1); row.tr.remove(); sync(true); } }, '×');
        row.tr = h('tr', null, tds, h('td', { class: 'col-x' }, remove));
        rows.push(row);
        tbody.appendChild(row.tr);
        return row;
      }

      control.set = (v) => {
        if (!Array.isArray(v) || v.length === 0) return;
        // Replace the empty starter row rather than leaving it above the prefilled rows.
        while (rows.length) rows.pop().tr.remove();
        v.forEach((r) => addRow(r && typeof r === 'object' ? r : {}));
        collect();
        addButton.disabled = Number.isInteger(field.maxRows) && rows.length >= field.maxRows;
      };
      control.update = (value, state) => {
        const serverRows = Array.isArray(value) ? value : [];
        const cellErrors = state.cellErrors || {};
        rows.forEach((row) => {
          const index = row.index;
          for (const col of columns) {
            const cell = row.cells[col.key];
            if (cell.out) cell.out.textContent = formatValue(index < 0 ? null : (serverRows[index] || {})[col.key], col.format, col.decimals);
            const message = index < 0 ? null : cellErrors[index + '.' + col.key];
            const show = message && (showAllErrors || cell.touched || col.type === 'calculated');
            cell.error.textContent = show ? message : '';
            cell.error.hidden = !show;
          }
        });
      };

      // Start with one empty row so the table invites input.
      addRow();
      collect();
      return h('div', { class: 'sf-table-wrap' }, table, addButton);
    }

    evaluate(true);

    return {
      destroy() { destroyed = true; scheduleEvaluate.cancel(); root.textContent = ''; },
    };
  }

  window.SF.mountForm = mountForm;
  window.SF.loadLookup = loadLookup;
})();
