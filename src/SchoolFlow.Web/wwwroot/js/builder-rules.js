// Workflow builder: the plain-language "rule maker" for logic boxes (show when, required when, validation
// rules, Decision steps) and the guided maker for Calculated fields.
//
// These editors only WRITE formula text in a few fixed shapes and READ those same shapes back; anything
// else stays in the formula box under "Advanced". Formulas are still checked and run on the server only.
(function () {
  'use strict';
  const SF = window.SF;

  // ---- formula text helpers ------------------------------------------------------------

  /** A value as formula text: numbers bare, dates as DATE(y, m, d), everything else quoted. */
  function literal(value, kind) {
    if (value && typeof value === 'object' && value.today) return 'TODAY()';
    if (value && typeof value === 'object' && value.unit)
      return 'DATEADD(TODAY(), ' + (Number.isFinite(Number(value.offset)) ? Number(value.offset) : 0) + ', "' + value.unit + '")';
    if (kind === 'date') {
      const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || '');
      return m ? 'DATE(' + Number(m[1]) + ', ' + Number(m[2]) + ', ' + Number(m[3]) + ')' : '""';
    }
    if ((kind === 'number' || kind === 'numchoice') && value !== '' && value !== null && Number.isFinite(Number(value))) return String(Number(value));
    return '"' + String(value === null || value === undefined ? '' : value).replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
  }

  /** Reads back what literal() writes. Returns { value } or null. */
  function readLiteral(text) {
    let m;
    if ((m = /^"((?:[^"\\]|\\.)*)"$/.exec(text)) || (m = /^'((?:[^'\\]|\\.)*)'$/.exec(text))) return { value: m[1].replace(/\\(.)/g, '$1') };
    if (/^-?\d+(\.\d+)?$/.test(text)) return { value: text };
    if (/^TODAY\(\s*\)$/i.test(text)) return { value: { today: true } };
    if ((m = /^DATEADD\(\s*TODAY\(\s*\)\s*,\s*(-?\d+)\s*,\s*["'](weekdays|days)["']\s*\)$/i.exec(text)))
      return { value: { offset: m[1], unit: m[2].toLowerCase() } };
    if ((m = /^DATE\(\s*(\d{4})\s*,\s*(\d{1,2})\s*,\s*(\d{1,2})\s*\)$/i.exec(text)))
      return { value: m[1] + '-' + m[2].padStart(2, '0') + '-' + m[3].padStart(2, '0') };
    if (/^(true|false)$/i.test(text)) return { value: text.toLowerCase() === 'true' };
    return null;
  }

  /** Splits a formula at top-level separators (outside quotes and brackets). */
  function splitTop(text, separators) {
    const parts = [];
    const found = new Set();
    let depth = 0, quote = null, start = 0;
    for (let i = 0; i < text.length; i++) {
      const c = text[i];
      if (quote) { if (c === '\\') i++; else if (c === quote) quote = null; continue; }
      if (c === '"' || c === '\'') { quote = c; continue; }
      if (c === '(' || c === '[') depth++;
      else if (c === ')' || c === ']') depth--;
      else if (depth === 0) {
        const sep = separators.find((s) => text.startsWith(s, i));
        if (sep) { parts.push(text.slice(start, i).trim()); found.add(sep); i += sep.length - 1; start = i + 1; }
      }
    }
    parts.push(text.slice(start).trim());
    return { parts, found };
  }

  // ---- tests offered for each kind of field ------------------------------------------------

  const TESTS = {
    text: [['eq', 'is'], ['ne', 'is not'], ['in', 'is one of'], ['nin', 'is none of'], ['contains', 'contains'], ['blank', 'is empty'], ['filled', 'is filled in']],
    number: [['eq', 'is'], ['ne', 'is not'], ['gt', 'is more than'], ['ge', 'is at least'], ['lt', 'is less than'], ['le', 'is at most'], ['blank', 'is empty'], ['filled', 'is filled in']],
    date: [['eq', 'is on'], ['lt', 'is before'], ['gt', 'is after'], ['le', 'is on or before'], ['ge', 'is on or after'], ['blank', 'is empty'], ['filled', 'is filled in']],
    choice: [['eq', 'is'], ['ne', 'is not'], ['in', 'is one of'], ['nin', 'is none of'], ['blank', 'is empty'], ['filled', 'is filled in']],
    numchoice: [['eq', 'is'], ['ne', 'is not'], ['in', 'is one of'], ['nin', 'is none of'], ['ge', 'is at least'], ['gt', 'is more than'], ['le', 'is at most'], ['lt', 'is less than']],
    multi: [['contains', 'includes'], ['ncontains', 'does not include'], ['blank', 'has nothing ticked'], ['filled', 'has something ticked']],
    bool: [['yes', 'is ticked (yes)'], ['no', 'is not ticked (no)']],
    presence: [['filled', 'is filled in'], ['blank', 'is empty']],
  };
  TESTS.any = TESTS.number.concat([['contains', 'contains']]);
  const OPS = { eq: '==', ne: '!=', gt: '>', ge: '>=', lt: '<', le: '<=' };
  const OP_ALIASES = { '=': 'eq', '<>': 'ne' };
  const NO_VALUE = ['blank', 'filled', 'yes', 'no'];

  function conditionText(c, subject) {
    const code = c.code;
    const kind = subject ? subject.kind : 'any';
    switch (c.test) {
      case 'blank': return 'ISBLANK(' + code + ')';
      case 'filled': return 'NOT(ISBLANK(' + code + '))';
      case 'yes': return code + ' == true';
      case 'no': return code + ' != true';
      case 'contains': return 'CONTAINS(' + code + ', ' + literal(c.value, 'text') + ')';
      case 'in': case 'nin': {
        const list = (Array.isArray(c.value) ? c.value : []).map((v) => literal(v, kind));
        const text = 'IN(' + code + (list.length ? ', ' + list.join(', ') : ', ""') + ')';
        return c.test === 'in' ? text : 'NOT(' + text + ')';
      }
      case 'ncontains': return 'NOT(CONTAINS(' + code + ', ' + literal(c.value, 'text') + '))';
      default: return code + ' ' + OPS[c.test] + ' ' + literal(c.value, kind === 'any' && c.value !== '' && Number.isFinite(Number(c.value)) ? 'number' : kind);
    }
  }

  const CODE = '([A-Za-z_]\\w*(?:\\.[A-Za-z_]\\w*)*|COUNT\\(\\s*[A-Za-z_]\\w*\\s*\\))';
  const RE_BLANK = new RegExp('^ISBLANK\\(\\s*' + CODE + '\\s*\\)$', 'i');
  const RE_FILLED = new RegExp('^NOT\\(\\s*ISBLANK\\(\\s*' + CODE + '\\s*\\)\\s*\\)$', 'i');
  const RE_CONTAINS = new RegExp('^CONTAINS\\(\\s*' + CODE + '\\s*,\\s*(.+)\\)$', 'i');
  const RE_NCONTAINS = new RegExp('^NOT\\(\\s*CONTAINS\\(\\s*' + CODE + '\\s*,\\s*(.+)\\)\\s*\\)$', 'i');
  const RE_IN = new RegExp('^IN\\(\\s*' + CODE + '\\s*,(.+)\\)$', 'i');
  const RE_NIN = new RegExp('^NOT\\(\\s*IN\\(\\s*' + CODE + '\\s*,(.+)\\)\\s*\\)$', 'i');
  const RE_BARE = new RegExp('^' + CODE + '$');
  const RE_NOTBARE = new RegExp('^(?:NOT\\(\\s*' + CODE + '\\s*\\)|!\\s*' + CODE + ')$', 'i');
  const RE_COMPARE = new RegExp('^' + CODE + '\\s*(==|!=|<>|>=|<=|>|<|=)\\s*(.+)$', 'i');

  /** One condition read back from formula text, or null when it is not a shape the rule maker writes. */
  function readCondition(text, find) {
    let m, test, code, valueText = null;
    if ((m = RE_BLANK.exec(text))) { test = 'blank'; code = m[1]; }
    else if ((m = RE_FILLED.exec(text))) { test = 'filled'; code = m[1]; }
    else if ((m = RE_NIN.exec(text)) || (m = RE_IN.exec(text))) {
      test = /^NOT/i.test(text) ? 'nin' : 'in'; code = m[1];
      const { parts, found } = splitTop(m[2], [',']);
      const values = parts.map(readLiteral);
      if (found.size > 1 || values.some((v) => !v || typeof v.value !== 'string')) return null;
      const subject = find(code);
      if (!subject || !TESTS[subject.kind].some((t) => t[0] === test)) return null;
      return { code: subject.code, test, value: values.map((v) => v.value).filter((v) => v !== '') };
    }
    else if ((m = RE_BARE.exec(text))) { test = 'yes'; code = m[1]; }
    else if ((m = RE_NOTBARE.exec(text))) { test = 'no'; code = m[1] || m[2]; }
    else if ((m = RE_NCONTAINS.exec(text))) { test = 'ncontains'; code = m[1]; valueText = m[2].trim(); }
    else if ((m = RE_CONTAINS.exec(text))) { test = 'contains'; code = m[1]; valueText = m[2].trim(); }
    else if ((m = RE_COMPARE.exec(text))) { code = m[1]; valueText = m[3].trim(); test = OP_ALIASES[m[2]] || Object.keys(OPS).find((k) => OPS[k] === m[2]); }
    else return null;
    const subject = find(code);
    if (!subject) return null;
    let value = '';
    if (valueText !== null) {
      const lit = readLiteral(valueText);
      if (!lit) return null;
      value = lit.value;
      if (typeof value === 'boolean') {
        if (test === 'eq') test = value ? 'yes' : 'no';
        else if (test === 'ne') test = value ? 'no' : 'yes';
        else return null;
        value = '';
      }
    }
    if (!TESTS[subject.kind].some((t) => t[0] === test)) return null;
    return { code: subject.code, test, value };
  }

  /** The whole formula read back as { join, items }, or null. A blank formula is an empty rule. */
  function readRule(text, find) {
    text = (text || '').trim();
    if (!text) return { join: '&&', items: [] };
    const { parts, found } = splitTop(text, ['&&', '||']);
    if (found.size > 1) return null;
    const items = [];
    for (const part of parts) {
      const c = readCondition(part, find);
      if (!c) return null;
      items.push(c);
    }
    return { join: found.has('||') ? '||' : '&&', items };
  }

  // ---- the editors --------------------------------------------------------------------------

  /**
   * deps: { h, formulaInput, markDirty, subjects(opts) -> [{ code, label, kind, options?: [[value, label]] }] }
   */
  SF.builderRules = function (deps) {
    const { h, formulaInput, markDirty } = deps;

    function selectBox(value, options, onChange, attrs) {
      const el = h('select', attrs || null, options.map(([v, label]) => h('option', { value: v }, label)));
      el.value = value;
      if (el.value !== value && value) { el.appendChild(h('option', { value }, value)); el.value = value; }
      el.addEventListener('change', () => onChange(el.value));
      return el;
    }

    /**
     * Wraps a formula box: the plain editor on top, the formula itself folded away under "Advanced".
     * read(text) -> model or null; write(model) -> text; drawSimple(box, model, changed, restructure).
     */
    function twoWay(value, onChange, opts, read, write, drawSimple) {
      const simple = h('div', { class: 'b-maker' });
      let syncing = false;
      let model = read(value);
      const fxBox = formulaInput(value, (v) => {
        onChange(v);
        if (syncing) return;
        model = read(v);
        draw();
      }, opts);
      const area = fxBox.querySelector('textarea');
      const advanced = h('details', { class: 'b-advanced' }, h('summary', null, 'Advanced: edit as a formula'), fxBox);

      function changed() {
        syncing = true;
        area.value = write(model);
        area.dispatchEvent(new Event('input'));
        syncing = false;
      }
      function draw() {
        simple.textContent = '';
        if (!model) {
          simple.append(h('p', { class: 'b-help' }, 'This is written as a formula the simple editor can’t show. Change it under “Advanced”, or ',
            h('button', { type: 'button', class: 'b-fx-btn', onclick: () => { model = read(''); changed(); draw(); } }, 'start over'), '.'));
          advanced.open = true;
          return;
        }
        drawSimple(simple, model, changed, () => { changed(); draw(); });
      }
      draw();
      return h('div', { class: 'b-logic' }, simple, advanced);
    }

    /**
     * A yes/no rule: "When [all/any] of: [field] [test] [value]".
     * opts.empty: what a blank rule means (e.g. "Always shown"); opts.defaultCode: first field offered.
     */
    function condition(value, onChange, opts) {
      opts = opts || {};
      const subjects = () => deps.subjects(opts).filter((s) => TESTS[s.kind]);
      const find = (code) => subjects().find((s) => s.code.toLowerCase() === code.replace(/\s+/g, '').toLowerCase());
      const write = (model) => model.items.map((c) => conditionText(c, find(c.code))).join(' ' + model.join + ' ');

      return twoWay(value, onChange, opts, (t) => readRule(t, find), write, (box, model, changed, restructure) => {
        const list = subjects();
        if (!list.length) {
          box.append(h('p', { class: 'b-help' }, 'Add some fields to the form first, then pick them here.'));
          return;
        }
        if (!model.items.length && opts.empty) box.append(h('p', { class: 'b-maker-empty' }, opts.empty));
        if (model.items.length > 1)
          box.append(h('div', { class: 'b-maker-join' }, 'When ',
            selectBox(model.join, [['&&', 'all'], ['||', 'any']], (v) => { model.join = v; changed(); }, { 'aria-label': 'All or any' }),
            ' of these are true:'));
        else if (model.items.length) box.append(h('div', { class: 'b-maker-join' }, 'When'));

        model.items.forEach((c, i) => {
          const subject = find(c.code) || list[0];
          const tests = TESTS[subject.kind];
          if (!tests.some((t) => t[0] === c.test)) c.test = tests[0][0];
          const pick = selectBox(subject.code, list.map((s) => [s.code, s.label]), (v) => {
            c.code = v; c.value = ''; c.test = TESTS[find(v).kind][0][0]; restructure();
          }, { 'aria-label': 'Field' });
          const test = selectBox(c.test, tests, (v) => {
            const wasList = c.test === 'in' || c.test === 'nin', isList = v === 'in' || v === 'nin';
            if (wasList !== isList) c.value = isList ? (c.value !== '' && c.value !== undefined && !(c.value && c.value.today) ? [String(c.value)] : []) : '';
            c.test = v; restructure();
          }, { 'aria-label': 'Test' });
          const valueBox = NO_VALUE.includes(c.test) ? null : valueControl(subject, c, changed);
          box.append(h('div', { class: 'b-cond' }, pick, test, valueBox,
            h('button', { type: 'button', class: 'icon-btn danger', 'aria-label': 'Remove this condition',
              onclick: () => { model.items.splice(i, 1); restructure(); } }, '×')));
        });
        box.append(h('button', { type: 'button', class: 'btn small', onclick: () => {
          const first = (opts.defaultCode && find(opts.defaultCode)) || list[0];
          model.items.push({ code: first.code, test: TESTS[first.kind][0][0], value: '' });
          restructure();
        } }, model.items.length ? '+ And another condition' : '+ Add a condition'));
      });
    }

    function valueControl(subject, c, changed) {
      if (c.test === 'in' || c.test === 'nin') {
        if (!Array.isArray(c.value)) c.value = [];
        if (subject.options && subject.options.length) {
          const known = subject.options.map(([v]) => String(v));
          const extra = c.value.filter((v) => !known.includes(String(v))).map((v) => [v, v]);
          return h('div', { class: 'b-cond-many' }, subject.options.concat(extra).map(([v, label]) => {
            const box = h('input', { type: 'checkbox', checked: c.value.map(String).includes(String(v)) });
            box.addEventListener('change', () => {
              c.value = c.value.filter((x) => String(x) !== String(v));
              if (box.checked) c.value.push(String(v));
              changed();
            });
            return h('label', null, box, ' ', label);
          }));
        }
        const input = h('input', { type: 'text', value: c.value.join(', '), 'aria-label': 'Values', placeholder: 'e.g. Red, Blue (comma between)' });
        input.addEventListener('input', () => { c.value = input.value.split(',').map((v) => v.trim()).filter(Boolean); changed(); });
        return input;
      }
      if (subject.options && (c.test === 'eq' || c.test === 'ne' || c.test === 'contains' || c.test === 'ncontains' || subject.kind === 'numchoice')) {
        if (c.value === '' && subject.options.length) c.value = subject.options[0][0];
        return selectBox(String(c.value), subject.options.map(([v, l]) => [String(v), l]), (v) => { c.value = v; changed(); }, { 'aria-label': 'Value' });
      }
      if (subject.kind === 'date') {
        const v0 = c.value && typeof c.value === 'object' ? c.value : null;
        const mode = !v0 ? 'date' : v0.today ? 'today' : v0.unit === 'weekdays' ? 'weekdays' : 'days';
        const date = h('input', { type: 'date', value: v0 ? '' : (c.value || ''), 'aria-label': 'Date', hidden: mode !== 'date' });
        const count = h('input', { type: 'number', step: 1, value: v0 && v0.unit ? v0.offset : '5', 'aria-label': 'How many days', hidden: !(v0 && v0.unit) });
        const which = selectBox(mode, [['date', 'a date'], ['today', 'today'], ['weekdays', 'school days from today'], ['days', 'days from today']], (v) => {
          date.hidden = v !== 'date';
          count.hidden = v !== 'weekdays' && v !== 'days';
          c.value = v === 'date' ? date.value : v === 'today' ? { today: true } : { offset: count.value || '0', unit: v };
          changed();
        }, { 'aria-label': 'Compare with' });
        date.addEventListener('input', () => { c.value = date.value; changed(); });
        count.addEventListener('input', () => { if (c.value && c.value.unit) { c.value.offset = count.value || '0'; changed(); } });
        return h('span', { class: 'b-cond-date' }, count, which, date);
      }
      const input = h('input', { type: subject.kind === 'number' ? 'number' : 'text', value: c.value || '', 'aria-label': 'Value',
        placeholder: subject.kind === 'number' ? '0' : 'text' });
      input.addEventListener('input', () => { c.value = input.value; changed(); });
      return input;
    }

    // ---- Calculated fields ----

    const CALC_MODES = [
      ['sum', 'Add up'], ['copy', 'Same as another value'], ['sub', 'Subtract'], ['mul', 'Multiply'], ['div', 'Divide'],
      ['days', 'Days between two dates'], ['schooldays', 'School days between two dates'],
    ];

    function calc(value, onChange, opts) {
      opts = opts || {};
      const all = () => deps.subjects(Object.assign({}, opts, { forCalc: true }));
      const numbers = () => all().filter((s) => s.kind === 'number' || s.kind === 'column');
      const dates = () => all().filter((s) => s.kind === 'date').concat([{ code: 'TODAY()', label: 'Today', kind: 'date' }]);
      const operand = (t) => {
        t = t.trim();
        if (/^-?\d+(\.\d+)?$/.test(t)) return { number: t };
        if (/^TODAY\(\s*\)$/i.test(t)) return { code: 'TODAY()' };
        const s = all().find((x) => x.code.toLowerCase() === t.replace(/\s+/g, '').toLowerCase());
        return s ? { code: s.code } : null;
      };
      const opText = (o) => (o.code ? o.code : (o.number === '' || o.number === undefined ? '0' : String(Number(o.number))));

      function read(text) {
        text = (text || '').trim();
        if (!text) return { mode: 'sum', items: [] };
        let m;
        if ((m = /^ROUND\((.*),\s*2\s*\)$/i.exec(text))) {
          const inner = read(m[1]);
          return inner && inner.items.length && !inner.round && inner.mode !== 'days' && inner.mode !== 'schooldays' ? Object.assign(inner, { round: true }) : null;
        }
        const single = operand(text);
        if (single && single.code && single.code !== 'TODAY()') return { mode: 'copy', items: [single] };
        const args = (inner, n) => {
          const { parts, found } = splitTop(inner, [',']);
          if (found.size > 1 || (n && parts.length !== n)) return null;
          const items = parts.map(operand);
          return items.every(Boolean) ? items : null;
        };
        if ((m = /^SUM\((.*)\)$/i.exec(text))) { const items = args(m[1]); return items && { mode: 'sum', items }; }
        if ((m = /^DATEDIFF\((.*)\)$/i.exec(text))) { const items = args(m[1], 2); return items && items.every((o) => o.code) && { mode: 'days', items }; }
        if ((m = /^WEEKDAYS\((.*)\)$/i.exec(text))) { const items = args(m[1], 2); return items && items.every((o) => o.code) && { mode: 'schooldays', items }; }
        for (const [mode, sep] of [['sum', '+'], ['sub', '-'], ['mul', '*'], ['div', '/']]) {
          const { parts, found } = splitTop(text, ['+', '-', '*', '/', '%', '^', '==', '!=', '<', '>', '&&', '||', '?']);
          if (parts.length < 2 || found.size !== 1 || !found.has(sep)) continue;
          if (mode !== 'mul' && mode !== 'sum' && parts.length !== 2) return null;
          const items = parts.map(operand);
          return items.every(Boolean) && items.every((o) => o.number === undefined || !o.number.startsWith('-')) ? { mode, items } : null;
        }
        return null;
      }

      function write(model) {
        const text = writeBare(model);
        return text && model.round ? 'ROUND(' + text + ', 2)' : text;
      }
      function writeBare(model) {
        const ops = model.items;
        if (!ops.length) return '';
        switch (model.mode) {
          case 'copy': return opText(ops[0]);
          case 'sum': return 'SUM(' + ops.map(opText).join(', ') + ')';
          case 'sub': return ops.map(opText).join(' - ');
          case 'mul': return ops.map(opText).join(' * ');
          case 'div': return ops.map(opText).join(' / ');
          case 'days': return 'DATEDIFF(' + ops.map(opText).join(', ') + ')';
          case 'schooldays': return 'WEEKDAYS(' + ops.map(opText).join(', ') + ')';
        }
        return '';
      }

      return twoWay(value, onChange, opts, read, write, (box, model, changed, restructure) => {
        const isDates = model.mode === 'days' || model.mode === 'schooldays';
        const isCopy = model.mode === 'copy';
        const fixed = model.mode === 'sub' || model.mode === 'div' || isDates || isCopy;
        const pool = isDates ? dates() : isCopy ? all().filter((x) => x.kind !== 'column') : numbers();
        box.append(h('div', { class: 'b-maker-join' }, 'Result: ',
          selectBox(model.mode, CALC_MODES, (v) => {
            const wasDates = isDates;
            model.mode = v;
            const nowDates = v === 'days' || v === 'schooldays';
            if (wasDates !== nowDates) model.items = [];
            if ((v === 'sub' || v === 'div' || nowDates) && model.items.length > 2) model.items.length = 2;
            if (v === 'copy' && model.items.length > 1) model.items.length = 1;
            if (nowDates) delete model.round;
            restructure();
          }, { 'aria-label': 'Kind of calculation' })));
        if (!pool.length) {
          box.append(h('p', { class: 'b-help' }, isDates ? 'Add two Date fields to the form first.' : 'Add a Number or Currency field to the form first.'));
          return;
        }
        if (isCopy && !model.items.length) model.items.push({ code: pool[0].code });
        if (fixed && !isCopy) while (model.items.length < 2) model.items.push(isDates ? { code: pool[Math.min(model.items.length, pool.length - 1)].code } : { code: pool[0].code });
        const names = isDates ? ['From', 'To'] : isCopy ? ['Same as'] : model.mode === 'sub' ? ['Start with', 'Take away'] : model.mode === 'div' ? ['Divide', 'By'] : null;
        const sign = { sum: '+', mul: '×' }[model.mode];

        model.items.forEach((o, i) => {
          const choices = pool.map((s) => [s.code, s.label]);
          if (!isDates) choices.push(['#', 'A fixed number…']);
          const pick = selectBox(o.code || '#', choices, (v) => {
            if (v === '#') { delete o.code; o.number = ''; } else { o.code = v; delete o.number; }
            restructure();
          }, { 'aria-label': 'Value ' + (i + 1) });
          let num = null;
          if (!o.code) {
            num = h('input', { type: 'number', step: 'any', value: o.number || '', placeholder: 'e.g. 0.67', 'aria-label': 'Number' });
            num.addEventListener('input', () => { o.number = num.value; changed(); });
          }
          box.append(h('div', { class: 'b-cond b-calc' },
            h('span', { class: 'b-cond-lead' }, names ? names[i] : (i === 0 ? '' : sign)), pick, num,
            fixed ? null : h('button', { type: 'button', class: 'icon-btn danger', 'aria-label': 'Remove',
              onclick: () => { model.items.splice(i, 1); restructure(); } }, '×')));
        });
        if (!fixed)
          box.append(h('button', { type: 'button', class: 'btn small', onclick: () => {
            model.items.push({ code: pool[0].code }); restructure();
          } }, model.items.length ? '+ ' + (model.mode === 'sum' ? 'Add another' : 'Multiply by another') : '+ Pick a value'));
        if (!isDates)
          box.append(h('label', { class: 'b-toggle' }, (() => {
            const tick = h('input', { type: 'checkbox', checked: !!model.round });
            tick.addEventListener('change', () => { if (tick.checked) model.round = true; else delete model.round; changed(); });
            return tick;
          })(), h('span', null, 'Round to cents (2 decimal places)')));
        if (model.mode === 'days')
          box.append(h('p', { class: 'b-help' }, 'Counts calendar days from the first date to the second (e.g. Mon → Wed = 2).'));
        if (model.mode === 'schooldays')
          box.append(h('p', { class: 'b-help' }, 'Counts Monday–Friday school days, both dates included, skipping days the school calendar marks closed.'));
      });
    }

    return { condition, calc };
  };

  // The readers and writers, for quick checks while debugging.
  SF.builderRules.internals = { readRule, conditionText, literal, readLiteral, splitTop };
})();
