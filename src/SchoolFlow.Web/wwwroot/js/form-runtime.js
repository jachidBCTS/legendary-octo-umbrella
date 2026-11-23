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
   *                    onSuccess(runId), preview, initialValues (answers to prefill, e.g. from a previous request),
 *                    previousDisplay ({ key: text } — how answers read before, for "Was: …" when one is changed),
   *                    saveDraft(values) → Promise<{savedAt}> (adds a "Save for later" button; after the first
   *                    save, later changes are saved automatically) }
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
    const draftStatus = h('span', { class: 'sf-draft-status muted small', role: 'status' });
    let draftSaved = !!options.draftAlreadySaved; // autosave only starts once the person has chosen to keep a draft
    let draftSaving = false;
    let draftDirty = false;
    const draftButton = options.saveDraft
      ? h('button', { type: 'button', class: 'btn lg', onclick: () => saveDraft(false) }, 'Save for later')
      : null;

    async function saveDraft(automatic) {
      if (!options.saveDraft || submitting || destroyed) return;
      if (draftSaving) { draftDirty = true; return; }
      draftSaving = true;
      draftDirty = false;
      if (!automatic) { draftButton.disabled = true; draftStatus.textContent = 'Saving…'; }
      try {
        const result = await options.saveDraft(values);
        draftSaved = true;
        draftStatus.textContent = (automatic ? 'Draft saved automatically at ' : 'Draft saved at ') + result.savedAt + '. You can close this page and finish later from the home page.';
        draftStatus.classList.remove('bad');
      } catch (e) {
        draftStatus.textContent = 'Could not save the draft: ' + e.message;
        draftStatus.classList.add('bad');
      } finally {
        draftSaving = false;
        if (draftButton) draftButton.disabled = false;
        // Something changed while the save was in flight: save once more so the draft is never stale.
        if (draftDirty && !destroyed) autosave();
      }
    }
    const autosave = debounce(() => { if (draftSaved) saveDraft(true); }, 2500);

    // ---- pages: a "page" field starts a new page; a form without one is a single page ----
    const pageOf = {};
    const pageTitles = [];
    fields.forEach((field, i) => {
      if (field.type === 'page' && i > 0) pageTitles.push(field.label || '');
      else if (i === 0) pageTitles.push(field.type === 'page' ? field.label || '' : 'Start');
      pageOf[field.key] = pageTitles.length - 1;
    });
    const pageCount = Math.max(1, pageTitles.length);
    let page = 0;
    const steps = h('ol', { class: 'sf-steps', hidden: pageCount < 2, 'aria-label': 'Pages of this form' });
    const backButton = h('button', { type: 'button', class: 'btn lg', hidden: true, onclick: () => showPage(page - 1, true) }, 'Back');
    const nextButton = h('button', { type: 'button', class: 'btn primary lg', hidden: true, onclick: () => nextPage() }, 'Next');

    function showPage(n, scroll) {
      page = Math.max(0, Math.min(pageCount - 1, n));
      for (const field of fields) controls[field.key].wrap.classList.toggle('sf-off-page', pageOf[field.key] !== page);
      if (pageCount < 2) return;
      backButton.hidden = page === 0;
      nextButton.hidden = page === pageCount - 1;
      submitButton.hidden = page !== pageCount - 1;
      steps.textContent = '';
      pageTitles.forEach((title, i) => steps.appendChild(h('li', { class: i === page ? 'on' : i < page ? 'done' : '', 'aria-current': i === page ? 'step' : null },
        h('span', null, String(i + 1)), title || 'Page ' + (i + 1))));
      if (scroll) form.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    function firstErrorOn(onPage) {
      for (const field of fields) {
        if (onPage !== null && pageOf[field.key] !== onPage) continue;
        const wrap = controls[field.key].wrap;
        if (wrap.hidden) continue;
        if (wrap.classList.contains('has-error') || wrap.querySelector('.sf-cell-error:not([hidden])')) return field;
      }
      return null;
    }

    /** Checks the current page with the server before moving on, so problems are fixed where they are. */
    async function nextPage() {
      fields.forEach((field) => { if (pageOf[field.key] === page) touched.add(field.key); });
      scheduleEvaluate.cancel();
      nextButton.disabled = true;
      try { await evaluate(false); } finally { nextButton.disabled = false; }
      const bad = firstErrorOn(page);
      if (bad) {
        setBanner('Please fix the highlighted fields before going on.');
        controls[bad.key].wrap.scrollIntoView({ behavior: 'smooth', block: 'center' });
        return;
      }
      setBanner(null);
      showPage(page + 1, true);
    }

    // Intro lines that start with a bullet become a list of notices; anything else stays a paragraph.
    const introLines = (definition.intro || '').split('\n').map((l) => l.trim()).filter(Boolean);
    const isBullet = (l) => /^[•\-*]\s+/.test(l);
    const intro = !introLines.length ? null
      : introLines.every(isBullet) ? h('ul', { class: 'sf-intro sf-notices' }, introLines.map((l) => h('li', null, l.replace(/^[•\-*]\s+/, ''))))
        : h('p', { class: 'sf-intro' }, definition.intro);

    const form = h('form', { class: 'sf-form' + (definition.style === 'paper' ? ' sf-paper' : ''), novalidate: true, onsubmit: onSubmit },
      intro,
      steps, banner, grid,
      h('div', { class: 'sf-submit-row' }, backButton, nextButton, submitButton, draftButton, busy, draftStatus,
        options.preview ? h('span', { class: 'muted' }, 'Preview — nothing is submitted.') : null));
    root.appendChild(form);

    if (fields.length === 0) {
      grid.appendChild(h('p', { class: 'muted' }, 'This form has no fields yet.'));
    }
    // A worksheet draws other fields of the form inside its rows, so those are built compact and
    // placed by the worksheet instead of in the grid.
    const byKey = {};
    fields.forEach((f) => { byKey[f.key.toLowerCase()] = f; });
    const rowKeys = (row) => {
      const keys = [];
      (row.description || '').replace(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (_, k) => { keys.push(k); return ''; });
      if (row.cost) keys.push(row.cost);
      return keys.map((k) => byKey[k.trim().toLowerCase()]).filter(Boolean);
    };
    const held = new Set();
    fields.forEach((f) => { if (f.type === 'worksheet') (f.rows || []).forEach((row) => rowKeys(row).forEach((x) => held.add(x.key))); });
    for (const field of fields) if (field.type !== 'worksheet') controls[field.key] = buildField(field, held.has(field.key));
    for (const field of fields) {
      if (field.type === 'worksheet') controls[field.key] = buildWorksheet(field);
      if (!held.has(field.key)) grid.appendChild(controls[field.key].wrap);
    }
    showPage(0, false);

    const scheduleEvaluate = debounce(() => evaluate(false), 220);

    function changed(key, immediate) {
      touched.add(key);
      if (draftSaved) autosave();
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

    // Revising: how each answer read before, so a changed one can say "Was: …".
    const previous = options.previousDisplay || null;
    const baseline = {};
    function showWas(field, control, value, init) {
      if (!previous || !(field.key in previous) || !control.error.parentNode) return;
      const now = JSON.stringify(value === undefined ? null : value);
      if (init) baseline[field.key] = now;
      const differs = field.key in baseline && now !== baseline[field.key];
      if (differs && !control.was) {
        control.was = h('div', { class: 'sf-was' });
        control.error.parentNode.insertBefore(control.was, control.error);
      }
      if (control.was) {
        control.was.hidden = !differs;
        control.was.textContent = differs ? 'Was: ' + previous[field.key] : '';
      }
    }

    function apply(state, init) {
      for (const field of fields) {
        const control = controls[field.key];
        const fieldState = (state.fields || {})[field.key] || { visible: true, required: false, errors: [] };
        const value = (state.values || {})[field.key];
        control.wrap.hidden = !fieldState.visible;
        if (control.required) control.required.hidden = !fieldState.required;
        if ((init || control.readOnly) && control.set && value !== null && value !== undefined) control.set(value);
        if (control.update) control.update(value, fieldState);
        showWas(field, control, value, init);

        const always = field.type === 'calculated';
        const show = always || showAllErrors || touched.has(field.key);
        // Formula problems are the builder's mistake, not the user's: always show them.
        const errors = (fieldState.errors || []).filter((e) => show || /formula/i.test(e));
        control.error.textContent = errors.join(' ');
        control.error.hidden = errors.length === 0;
        control.wrap.classList.toggle('has-error', errors.length > 0);
        // Say the same things to a screen reader: required, wrong, and what the problem is.
        const input = control.wrap.matches('input, select, textarea') ? control.wrap : control.wrap.querySelector('input, select, textarea');
        if (input) {
          if (fieldState.required) input.setAttribute('aria-required', 'true'); else input.removeAttribute('aria-required');
          if (errors.length) { input.setAttribute('aria-invalid', 'true'); if (control.error.id) input.setAttribute('aria-describedby', control.error.id); }
          else { input.removeAttribute('aria-invalid'); input.removeAttribute('aria-describedby'); }
        }
      }
    }

    async function onSubmit(event) {
      event.preventDefault();
      if (submitting) return;
      if (page < pageCount - 1) { nextPage(); return; } // Enter on an earlier page moves on rather than submitting
      submitting = true;
      showAllErrors = true;
      scheduleEvaluate.cancel();
      autosave.cancel();
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
        const bad = firstErrorOn(null);
        if (bad) {
          if (pageOf[bad.key] !== page) showPage(pageOf[bad.key], false);
          controls[bad.key].wrap.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
      } catch (e) {
        setBanner(e.message);
      } finally {
        submitting = false;
        submitButton.disabled = false;
      }
    }

    // ---- field controls ------------------------------------------------------------

    function buildWorksheet(field) {
      const placed = new Set();
      const place = (f) => {
        if (!f || placed.has(f.key) || !controls[f.key]) return null;
        placed.add(f.key);
        return controls[f.key];
      };
      const rows = (field.rows || []).map((row) => {
        const description = h('div', { class: 'ws-desc' });
        const errors = [];
        let last = 0;
        const text = row.description || '';
        text.replace(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (match, key, at) => {
          if (at > last) description.appendChild(h('span', null, text.slice(last, at).trim()));
          const control = place(byKey[key.toLowerCase()]);
          if (control) { description.appendChild(control.wrap); errors.push(control.error); }
          last = at + match.length;
          return match;
        });
        if (last < text.length && text.slice(last).trim()) description.appendChild(h('span', null, text.slice(last).trim()));
        const cost = h('div', { class: 'ws-cost' });
        const costControl = row.cost ? place(byKey[row.cost.trim().toLowerCase()]) : null;
        if (costControl) { cost.appendChild(costControl.wrap); errors.push(costControl.error); }
        errors.forEach((e) => description.appendChild(e));
        const isSum = !row.number && !row.item && !/\{/.test(text);
        return h('div', { class: 'ws-row' + (isSum ? ' is-sum' : ''), role: 'row' },
          h('div', { class: 'ws-no', role: 'cell' }, row.number || ''),
          h('div', { class: 'ws-item', role: 'cell' }, row.item || ''),
          description, cost);
      });
      const wrap = h('div', { class: 'sf-field sf-worksheet' },
        // A page break just above with the same title already names it.
        field.label && !fields.some((f) => f.type === 'page' && f.label === field.label) ? h('h3', null, field.label) : null,
        field.help ? h('p', { class: 'sf-help' }, field.help) : null,
        h('div', { class: 'ws', role: 'table' },
          h('div', { class: 'ws-row ws-head', role: 'row' }, h('div', null, ''), h('div', null, 'Item'), h('div', null, 'Description'), h('div', { class: 'ws-cost' }, 'Cost Estimate')),
          rows));
      return { wrap, error: h('div', { hidden: true }) };
    }

    function buildField(field, compact) {
      const id = 'sf-' + (++uid);
      const error = h('div', { class: 'sf-error', hidden: true, id: id + '-err' });
      if (field.type === 'break') {
        const wrap = h('div', { class: 'sf-field sf-break' + (field.format === 'line' ? ' sf-break-line' : ''), 'aria-hidden': 'true' }, error);
        return { wrap, error };
      }
      if (field.type === 'section' || field.type === 'page') {
        const wrap = h('div', { class: 'sf-field sf-section' + (field.type === 'page' ? ' sf-page-title' : '') },
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
        case 'time':
          body = input(h('input', { id, type: 'time' }), field.key, control, true);
          break;
        case 'phone':
          body = input(h('input', { id, type: 'tel', autocomplete: 'tel', inputmode: 'tel', maxlength: 40, placeholder: field.placeholder || null }), field.key, control);
          break;
        case 'signature':
        {
          // "Sign with my name" fills in the signed-in person's name as First Last (owner, Nov 23, 2026).
          const box = input(h('input', { id, type: 'text', class: 'sf-signature', autocomplete: 'name', maxlength: 200, placeholder: 'Type your full name to sign' }), field.key, control);
          const myName = (document.body.dataset.myName || '').trim();
          body = myName
            ? h('div', { class: 'sf-sign-row' }, box, h('button', { type: 'button', class: 'btn small', onclick: () => {
                if (box.disabled || box.readOnly) return;
                box.value = myName;
                box.dispatchEvent(new Event('input', { bubbles: true }));
                box.dispatchEvent(new Event('change', { bubbles: true }));
              } }, 'Sign with my name'))
            : box;
          break;
        }
        case 'email':
          body = input(h('input', { id, type: 'email', autocomplete: 'off', spellcheck: 'false', 'data-people': '', placeholder: field.placeholder || null }), field.key, control);
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
        case 'student':
          body = buildStudentLookup(field, id, control);
          break;
        case 'department':
          body = buildListed(field, id, control, '/api/departments', 'No departments have been set up yet', 'departments');
          break;
        case 'location':
          body = buildListed(field, id, control, '/api/locations', 'No locations have been set up yet', 'locations');
          break;
        case 'staff':
          body = buildStaffLookup(field, id, control);
          break;
        case 'approval_chain':
          labelFor = null;
          body = buildChain(field, control);
          break;
        case 'attachments':
          body = buildAttachments(field, id, control);
          break;
        case 'calculated': {
          labelFor = null;
          const out = h('output', { class: 'sf-calc' }, '—');
          control.update = (value) => { out.textContent = formatValue(value, field.format, field.decimals); };
          body = out;
          break;
        }
        case 'perdiem': {
          // GSA rates for the destination ZIP code and dates, looked up by the server. Read-only.
          labelFor = null;
          const box = h('div', { class: 'sf-perdiem', 'aria-live': 'polite' });
          const money = (v) => formatValue(v, 'currency', 2);
          control.update = (value) => {
            box.textContent = '';
            box.classList.toggle('is-fallback', !!value && value.source === 'fallback');
            if (!value) {
              box.appendChild(h('span', { class: 'muted' }, 'Enter the destination ZIP code and travel dates to look up the GSA rates.'));
              return;
            }
            const place = [value.city, value.county ? value.county + ' County' : '', value.state].filter(Boolean).join(', ');
            box.append(
              h('strong', null, value.source === 'fallback' ? 'Standard rate used' : (place || 'ZIP ' + value.zip) + (value.standard ? ' (standard rate area)' : '')),
              h('span', null, 'Meals & incidentals: ' + money(value.meals) + ' a day, ' + money(value.first_last_day) + ' on the first and last day'),
              h('span', null, 'Hotel: up to ' + money(value.lodging) + ' a night'),
              h('span', { class: 'muted small' }, value.source === 'fallback' ? value.note
                : value.source === 'simulated' || value.rates_year ? value.note : 'GSA per diem rates, fiscal year ' + value.fiscal_year));
          };
          body = box;
          break;
        }
        case 'table':
          labelFor = null;
          body = buildTable(field, control);
          break;
        default:
          body = input(h('input', { id, type: 'text', placeholder: field.placeholder || null, maxlength: 10000 }), field.key, control);
      }

      if (compact) {
        // Inside a worksheet row: no label of its own, so the control carries the field's name.
        const el = body.matches('input, select, textarea, output') ? body : body.querySelector('input, select, textarea, output');
        if (el) el.setAttribute('aria-label', field.label || field.key);
        control.wrap = h('span', { class: 'ws-input ws-' + field.type }, body);
        if (isReadOnly(field)) lockField(control);
        return control;
      }

      const wide = ['table', 'textarea', 'approval_chain', 'attachments'].includes(field.type);
      const stacked = wide || ['radio', 'multiselect'].includes(field.type);
      control.wrap = h('div', { class: 'sf-field' + (field.width === 'half' && !wide ? ' half' : '') + (field.type === 'calculated' ? ' is-calc' : '') + (stacked ? ' sf-stack' : '') },
        h('label', { class: 'sf-label', for: labelFor }, field.label || field.key, ' ', required),
        body,
        field.help ? h('p', { class: 'sf-help' }, field.help) : null,
        error);
      if (isReadOnly(field)) lockField(control);
      return control;
    }

    /** Read only: the server fills the answer in (its default, or what the request was submitted with). */
    function isReadOnly(field) {
      return field.readOnly === true && !['calculated', 'perdiem', 'section', 'page', 'worksheet', 'break'].includes(field.type);
    }
    function lockField(control) {
      control.readOnly = true;
      control.wrap.classList.add('sf-readonly');
      control.wrap.querySelectorAll('input, select, textarea, button').forEach((el) => {
        // Text stays selectable and readable; choices and buttons cannot be used.
        if (el.matches('input[type=text], input[type=email], input[type=tel], input[type=date], input[type=time], textarea, input:not([type])')) {
          el.readOnly = true;
          el.setAttribute('aria-readonly', 'true');
          el.tabIndex = -1;
        } else el.disabled = true;
      });
    }

    function input(el, key, control, immediate) {
      el.addEventListener('input', () => { values[key] = el.value; if (!immediate) scheduleEvaluate(); if (draftSaved) autosave(); });
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

    /** A dropdown over a list the admins maintain (departments, locations). Value: { id, name }. */
    function buildListed(field, id, control, url, emptyText, noun) {
      const select = h('select', { id, disabled: true }, h('option', { value: '' }, 'Loading…'));
      const hint = h('p', { class: 'sf-help no-tip', hidden: true });
      let items = [];
      let wanted = null;
      let loaded = false;
      const choose = (wantedId) => {
        const item = items.find((i) => String(i.id) === String(wantedId));
        select.value = item ? String(item.id) : '';
        const before = values[field.key];
        values[field.key] = item ? { id: item.id, name: item.name } : null;
        hint.hidden = !item || !item.finalApprovers;
        if (item && item.finalApprovers) {
          hint.textContent = item.finalApprovers.length
            ? 'Final approval: ' + item.finalApprovers.join(item.rule === 'all' ? ' and ' : ' or ')
            : 'This department has no final approver set yet — ask an admin before submitting.';
        }
        if (!item && before) scheduleEvaluate();
      };
      select.addEventListener('change', () => { choose(select.value); changed(field.key, true); });
      control.set = (v) => {
        if (!v || typeof v !== 'object' || v.id === undefined || v.id === null) return;
        values[field.key] = { id: v.id, name: v.name };
        if (loaded) choose(v.id); else wanted = v.id;
      };
      // A location field can be limited to one district: a fixed one, or the one chosen in another field.
      let all = [];
      let aliases = {};
      let shownFor = null;
      const code = (text) => { const t = String(text || '').trim(); return aliases[t] || aliases[t.toUpperCase()] || t; };
      const wantedDistrict = () => districtWanted(field, (t) => code(t), all);
      const fill = () => {
        const district = wantedDistrict();
        if (district === shownFor) return;
        shownFor = district;
        items = district ? all.filter((i) => code(i.district).toLowerCase() === district.toLowerCase()) : all;
        const current = values[field.key] && values[field.key].id;
        select.textContent = '';
        select.appendChild(h('option', { value: '' }, items.length ? 'Choose…' : (district ? 'No locations in ' + district + ' yet' : emptyText)));
        items.forEach((i) => select.appendChild(h('option', { value: String(i.id) }, i.name)));
        if (current !== undefined && current !== null) {
          if (items.some((i) => String(i.id) === String(current))) select.value = String(current);
          else if (loaded) { values[field.key] = null; changed(field.key, true); }
        }
      };
      if (field.type === 'location' && (field.district || field.districtField)) control.update = () => { if (loaded) fill(); };
      api('GET', url).then((data) => {
        all = data.items || [];
        aliases = data.districts || {};
        items = all;
        select.textContent = '';
        if (field.type === 'location' && (field.district || field.districtField)) fill();
        else {
          select.appendChild(h('option', { value: '' }, items.length ? 'Choose…' : emptyText));
          items.forEach((i) => select.appendChild(h('option', { value: String(i.id) }, i.name)));
        }
        select.disabled = !!control.readOnly;
        loaded = true;
        if (wanted !== null) { choose(wanted); wanted = null; }
      }).catch((e) => {
        select.textContent = '';
        select.appendChild(h('option', { value: '' }, 'Could not load ' + noun));
        control.error.textContent = e.message;
        control.error.hidden = false;
      });
      return h('div', null, select, hint);
    }

    function buildAttachments(field, id, control) {
      const limit = Math.min(Math.max(Number.isInteger(field.maxFiles) ? field.maxFiles : 5, 1), 20);
      let files = []; // [{ id, name, size }]
      let uploading = 0;
      const list = h('ul', { class: 'sf-files' });
      const status = h('div', { class: 'muted small', role: 'status' });
      const picker = h('input', { id, type: 'file', multiple: limit > 1, class: 'sf-file-input',
        accept: '.pdf,.png,.jpg,.jpeg,.gif,.webp,.heic,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.rtf' });
      const choose = h('label', { class: 'btn small', for: id }, 'Choose files');
      const size = (bytes) => (bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(bytes / 1024)) + ' KB');

      function store(notify) {
        values[field.key] = files.map((f) => ({ id: f.id, name: f.name, size: f.size }));
        if (notify) changed(field.key, true);
      }

      function render() {
        list.textContent = '';
        for (const file of files) {
          list.appendChild(h('li', null,
            h('span', { class: 'sf-file-name' }, file.name), h('span', { class: 'muted small' }, size(file.size)),
            h('button', { type: 'button', class: 'icon-btn danger', title: 'Remove file', 'aria-label': 'Remove ' + file.name, onclick: () => remove(file) }, '×')));
        }
        const full = files.length >= limit;
        picker.disabled = full || options.preview;
        choose.classList.toggle('disabled', picker.disabled);
        status.textContent = options.preview ? 'Uploads are switched off in the preview.'
          : uploading ? 'Uploading…'
            : full ? 'That is the most files this question takes.'
              : 'Up to ' + limit + ' file' + (limit === 1 ? '' : 's') + '. PDF, images, Word, Excel, PowerPoint or text.';
      }

      async function remove(file) {
        files = files.filter((f) => f !== file);
        store(true);
        render();
        // Tidy the server copy too; if that fails the nightly clean-up will catch it.
        try { await api('DELETE', '/api/attachments/' + encodeURIComponent(file.id)); } catch (e) { /* not worth interrupting the person */ }
      }

      async function upload(file) {
        const data = new FormData();
        data.append('workflowId', String(options.workflowId || 0));
        if (options.runId) data.append('runId', String(options.runId));
        if (options.taskId) data.append('taskId', String(options.taskId));
        data.append('field', field.key);
        data.append('file', file);
        let response;
        try {
          response = await fetch('/api/attachments', { method: 'POST', credentials: 'same-origin', body: data, redirect: 'manual',
            headers: { Accept: 'application/json', RequestVerificationToken: document.querySelector('meta[name="csrf-token"]')?.content || '' } });
        } catch (e) {
          throw new Error('“' + file.name + '” could not be uploaded. Check your connection and try again.');
        }
        let body = null;
        try { body = await response.json(); } catch (e) { body = null; }
        if (response.type === 'opaqueredirect' || response.status === 401) throw new Error('Your session has expired. Save a draft if you can, then reload the page.');
        if (response.status === 413) throw new Error('“' + file.name + '” is too large to upload.');
        if (!response.ok) throw new Error((body && body.error) || '“' + file.name + '” could not be uploaded.');
        return body;
      }

      picker.addEventListener('change', async () => {
        const chosen = Array.from(picker.files || []);
        picker.value = ''; // so the same file can be chosen again after removing it
        const errors = [];
        const room = limit - files.length;
        if (chosen.length > room) errors.push('Only ' + limit + ' file' + (limit === 1 ? '' : 's') + ' can be added here; the extra ones were left out.');
        uploading++;
        render();
        for (const file of chosen.slice(0, Math.max(0, room))) {
          try {
            files.push(await upload(file));
            store(false);
            render();
          } catch (e) {
            errors.push(e.message);
          }
        }
        uploading--;
        store(true);
        render();
        control.uploadError = errors.join(' ');
        uploadNote.textContent = control.uploadError;
        uploadNote.hidden = errors.length === 0;
      });

      const uploadNote = h('div', { class: 'sf-error', hidden: true });
      control.set = (v) => {
        if (!Array.isArray(v)) return;
        files = v.filter((f) => f && f.id && f.name).map((f) => ({ id: String(f.id), name: String(f.name), size: Number(f.size) || 0 }));
        store(false);
        render();
      };
      render();
      return h('div', { class: 'sf-attach' }, h('div', { class: 'sf-attach-top' }, picker, choose, status), list, uploadNote);
    }

    function buildChain(field, control) {
      const holder = h('div');
      const templates = h('select', { 'aria-label': 'Start from a saved chain' }, h('option', { value: '' }, 'Start from a saved chain…'));
      let chains = [];
      let chainName = '';
      const store = (steps) => { values[field.key] = steps.length ? { name: chainName, steps } : null; };
      const editor = window.SF.chainEditor(holder, { steps: [], onChange: (steps) => { store(steps); changed(field.key, false); } });

      const fillTemplates = () => api('GET', '/api/chains').then((data) => {
        chains = (data.shared || []).concat(data.mine || []);
        templates.textContent = '';
        templates.appendChild(h('option', { value: '' }, chains.length ? 'Start from a saved chain…' : 'No saved chains yet'));
        const group = (label, list) => {
          if (!list.length) return;
          const g = h('optgroup', { label });
          list.forEach((c) => g.appendChild(h('option', { value: String(c.id) }, c.name)));
          templates.appendChild(g);
        };
        group('Shared templates', data.shared || []);
        group('My chains', data.mine || []);
      }).catch(() => { /* templates are a convenience; the editor still works without them */ });
      fillTemplates();

      templates.addEventListener('change', () => {
        const chosen = chains.find((c) => String(c.id) === templates.value);
        templates.value = '';
        if (!chosen) return;
        if (editor.get().length && !window.confirm('Replace the steps below with “' + chosen.name + '”?')) return;
        chainName = chosen.name;
        editor.set(chosen.steps);
        store(editor.get());
        changed(field.key, true);
      });

      const saveButton = h('button', { type: 'button', class: 'btn small', onclick: async () => {
        const steps = editor.get();
        if (!steps.length) { window.SF.toast('Add at least one step first.', 'bad'); return; }
        const name = window.prompt('Name for this chain, so you can reuse it:', chainName || '');
        if (!name || !name.trim()) return;
        try {
          await api('POST', '/api/chains', { name: name.trim(), description: '', shared: false, steps });
          chainName = name.trim();
          store(steps);
          window.SF.toast('Saved to your chains.');
          fillTemplates();
        } catch (e) {
          window.SF.toast(e.message, 'bad');
        }
      } }, 'Save as my chain');

      control.set = (v) => {
        if (!v || typeof v !== 'object' || !Array.isArray(v.steps)) return;
        chainName = v.name || '';
        editor.set(v.steps);
        store(editor.get());
      };
      return h('div', { class: 'sf-chain' },
        h('div', { class: 'sf-chain-top' }, templates, saveButton), holder,
        h('p', { class: 'sf-help' }, 'Approvers decide in this order. If the request has a department, its final approver is added automatically at the end.'));
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

    /** Search the student roster by name or ID. Only people an admin has given student access get results. */
    /** The district a location or staff field is limited to right now ('' = any). */
    function districtWanted(field, code, locations) {
      if (field.districtField) {
        const chosen = values[field.districtField];
        if (chosen && typeof chosen === 'object') {
          if (chosen.district) return code(chosen.district);
          const place = (locations || []).find((l) => String(l.id) === String(chosen.id));
          return place && place.district ? code(place.district) : '';
        }
        return chosen ? code(chosen) : '';
      }
      return field.district ? code(field.district) : '';
    }

    /** A member of staff from the district's staff list. Value: { email, name, title, location, department, district }. */
    function buildStaffLookup(field, id, control) {
      const box = h('input', { id, type: 'text', autocomplete: 'off', placeholder: field.placeholder || 'Type a name or email…', role: 'combobox', 'aria-expanded': 'false', 'aria-autocomplete': 'list' });
      const list = h('div', { class: 'sf-suggest', hidden: true, role: 'listbox' });
      const chosen = h('p', { class: 'sf-help sf-student no-tip', hidden: true });
      const detail = (p) => [p.email, p.title, p.location].filter(Boolean).join(' · ');
      const show = (p) => { chosen.textContent = p ? detail(p) : ''; chosen.hidden = !p; };
      let latest = 0;
      const search = debounce(async () => {
        const query = box.value.trim();
        const mine = ++latest;
        if (query.length < 2) { list.hidden = true; return; }
        // The server knows the district names; a district question's answer is passed as it is.
        const chosenDistrict = field.districtField ? values[field.districtField] : field.district;
        const district = chosenDistrict && typeof chosenDistrict === 'object' ? chosenDistrict.district || '' : chosenDistrict || '';
        try {
          const data = await api('GET', '/api/staff?q=' + encodeURIComponent(query) + (district ? '&district=' + encodeURIComponent(district) : ''));
          if (mine !== latest) return;
          list.textContent = '';
          const items = data.items || [];
          if (data.connected === false) list.appendChild(h('div', { class: 'sf-suggest-empty' }, 'The staff list is not connected on this site.'));
          else if (items.length === 0) list.appendChild(h('div', { class: 'sf-suggest-empty' }, district ? 'No matches in ' + district : 'No matches'));
          for (const item of items) {
            list.appendChild(h('button', { type: 'button', class: 'sf-suggest-item', role: 'option', onclick: () => choose(item) },
              h('strong', null, item.name || item.email), h('small', null, detail(item))));
          }
          list.hidden = false;
          box.setAttribute('aria-expanded', 'true');
        } catch (e) {
          control.error.textContent = e.message;
          control.error.hidden = false;
        }
      }, 250);
      function choose(item) {
        values[field.key] = { email: item.email, name: item.name, title: item.title, location: item.location, department: item.department, district: item.district };
        box.value = item.name || item.email;
        show(item);
        close();
        changed(field.key, true);
      }
      function close() { list.hidden = true; box.setAttribute('aria-expanded', 'false'); }
      control.set = (v) => {
        if (!v || typeof v !== 'object' || !v.email) return;
        values[field.key] = v;
        box.value = v.name || v.email;
        show(v);
      };
      box.addEventListener('input', () => {
        if (control.readOnly) return;
        // Typing after a choice clears it: only a person picked from the staff list counts.
        if (values[field.key]) { values[field.key] = null; show(null); changed(field.key, false); }
        search();
      });
      box.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
      box.addEventListener('blur', () => setTimeout(() => {
        close();
        if (!values[field.key]) box.value = '';
        if (!touched.has(field.key)) changed(field.key, true);
      }, 180));
      return h('div', { class: 'sf-typeahead' }, box, list, chosen);
    }

    function buildStudentLookup(field, id, control) {
      const box = h('input', { id, type: 'text', autocomplete: 'off', placeholder: field.placeholder || 'Type a student\'s name or ID…', role: 'combobox', 'aria-expanded': 'false', 'aria-autocomplete': 'list' });
      const list = h('div', { class: 'sf-suggest', hidden: true, role: 'listbox' });
      const chosen = h('p', { class: 'sf-help sf-student no-tip', hidden: true });
      const detail = (s) => ['ID ' + s.id, s.school, s.grade ? 'Grade ' + s.grade : '', s.academy].filter(Boolean).join(' · ');
      const show = (s) => { chosen.textContent = s ? detail(s) : ''; chosen.hidden = !s; };
      let latest = 0;
      const search = debounce(async () => {
        const query = box.value.trim();
        const mine = ++latest;
        if (query.length < 2) { list.hidden = true; return; }
        try {
          const items = (await api('GET', '/api/students?q=' + encodeURIComponent(query))).items || [];
          if (mine !== latest) return;
          list.textContent = '';
          if (items.length === 0) list.appendChild(h('div', { class: 'sf-suggest-empty' }, 'No matches'));
          for (const item of items) {
            list.appendChild(h('button', { type: 'button', class: 'sf-suggest-item', role: 'option', onclick: () => choose(item) },
              h('strong', null, item.name), h('small', null, detail(item))));
          }
          list.hidden = false;
          box.setAttribute('aria-expanded', 'true');
        } catch (e) {
          // Most often: this person has not been given access to student information.
          control.error.textContent = e.message;
          control.error.hidden = false;
        }
      }, 250);
      function choose(item) {
        values[field.key] = { id: item.id, name: item.name, first: item.first, last: item.last, school: item.school, grade: item.grade, academy: item.academy };
        box.value = item.name;
        show(item);
        close();
        changed(field.key, true);
      }
      function close() { list.hidden = true; box.setAttribute('aria-expanded', 'false'); }
      control.set = (v) => {
        if (!v || typeof v !== 'object' || !v.id) return;
        values[field.key] = v;
        box.value = v.name || '';
        show(v);
      };
      box.addEventListener('input', () => {
        // Typing after a choice clears it: only a student picked from the roster counts.
        if (values[field.key]) { values[field.key] = null; show(null); changed(field.key, false); }
        search();
      });
      box.addEventListener('keydown', (e) => { if (e.key === 'Escape') close(); });
      box.addEventListener('blur', () => setTimeout(() => {
        close();
        if (!values[field.key]) box.value = '';
        if (!touched.has(field.key)) changed(field.key, true);
      }, 180));
      return h('div', { class: 'sf-typeahead' }, box, list, chosen);
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
      destroy() { destroyed = true; scheduleEvaluate.cancel(); autosave.cancel(); root.textContent = ''; },
    };
  }

  window.SF.mountForm = mountForm;
  window.SF.loadLookup = loadLookup;
})();
