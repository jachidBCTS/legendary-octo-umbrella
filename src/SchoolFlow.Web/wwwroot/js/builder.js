// Workflow builder: form designer, field logic, flow canvas, settings and live preview.
// All text is placed with textContent (through SF.h), never innerHTML.
(function () {
  'use strict';
  const { h, api, debounce, toast } = window.SF;
  const root = document.getElementById('builder');
  if (!root) return;

  const workflowId = Number(root.dataset.workflowId);
  const catalog = JSON.parse(document.getElementById('function-catalog').textContent);

  const FIELD_TYPES = [
    { type: 'text', label: 'Short text', hint: 'One line' },
    { type: 'textarea', label: 'Long text', hint: 'Paragraph' },
    { type: 'number', label: 'Number', hint: 'Quantity, count' },
    { type: 'currency', label: 'Currency', hint: 'Dollar amount' },
    { type: 'date', label: 'Date', hint: 'Calendar picker' },
    { type: 'select', label: 'Dropdown', hint: 'Pick one' },
    { type: 'radio', label: 'Choice buttons', hint: 'Pick one, all visible' },
    { type: 'multiselect', label: 'Checkboxes', hint: 'Pick several' },
    { type: 'checkbox', label: 'Yes / no', hint: 'Single tick box' },
    { type: 'email', label: 'Email', hint: 'e.g. an approver' },
    { type: 'time', label: 'Time', hint: 'e.g. departure time' },
    { type: 'phone', label: 'Phone number', hint: 'Checked for digits' },
    { type: 'lookup', label: 'Freshservice lookup', hint: 'Device, location…' },
    { type: 'student', label: 'Student', hint: 'Needs student access' },
    { type: 'department', label: 'Department', hint: 'Sets the final approver' },
    { type: 'approval_chain', label: 'Approval chain', hint: 'Requester picks approvers' },
    { type: 'location', label: 'Location', hint: 'Building or room' },
    { type: 'attachments', label: 'Attachments', hint: 'Upload files' },
    { type: 'calculated', label: 'Calculated', hint: 'Formula result' },
    { type: 'table', label: 'Table', hint: 'Repeating rows' },
    { type: 'signature', label: 'Signature', hint: 'Type your name to sign' },
    { type: 'section', label: 'Section heading', hint: 'Group fields' },
    { type: 'page', label: 'Page break', hint: 'Start a new page' },
    { type: 'worksheet', label: 'Worksheet', hint: 'Table with fields inside its rows' },
  ];
  const COLUMN_TYPES = [['text', 'Text'], ['number', 'Number'], ['currency', 'Currency'], ['date', 'Date'], ['select', 'Dropdown'], ['checkbox', 'Yes / no'], ['calculated', 'Calculated']];
  const LOOKUP_SOURCES = [['departments', 'Freshservice departments'], ['locations', 'Freshservice locations (live)'], ['my_assets', "The person's own devices"], ['requesters', 'People (search by name)'], ['groups', 'Agent groups']];
  const NODE_TYPES = [
    { type: 'condition', label: 'Decision', hint: 'Yes / No branch from a formula', tone: 'amber' },
    { type: 'approval', label: 'Approval', hint: 'Wait for someone to approve', tone: 'violet' },
    { type: 'approval_chain', label: 'Approval chain', hint: 'Several approvers in order, then the department', tone: 'violet' },
    { type: 'task', label: 'Task', hint: 'Someone fills in details or adds documents', tone: 'violet' },
    { type: 'notify', label: 'Send notification', hint: 'In the app and by email', tone: 'rose' },
    { type: 'fs_create_ticket', label: 'Create ticket', hint: 'Open a Freshservice ticket', tone: 'teal' },
    { type: 'fs_update_ticket', label: 'Update ticket', hint: 'Status, priority, group, fields', tone: 'teal' },
    { type: 'fs_add_note', label: 'Add note', hint: 'Private or public note', tone: 'teal' },
    { type: 'fs_service_request', label: 'Service request', hint: 'Order a catalog item', tone: 'teal' },
    { type: 'fs_lookup', label: 'Look up', hint: 'Fetch a person or ticket', tone: 'teal' },
    { type: 'wait_ticket', label: 'Wait for ticket', hint: 'Pause until Freshservice updates it', tone: 'blue' },
    { type: 'set_variable', label: 'Set value', hint: 'Calculate and remember a value', tone: 'slate' },
    { type: 'end', label: 'End', hint: 'Finish the request', tone: 'green' },
  ];
  const NODE_INFO = Object.fromEntries(NODE_TYPES.map((n) => [n.type, n]));
  // Steps and fields that reach into the Freshservice help desk: administrators only.
  const HELP_DESK_STEPS = ['fs_create_ticket', 'fs_update_ticket', 'fs_add_note', 'fs_service_request', 'fs_lookup', 'wait_ticket'];
  // Matches FieldTypes.InTask on the server.
  const TASK_FIELD_TYPES = [['text', 'Text'], ['textarea', 'Paragraph'], ['email', 'Email'], ['phone', 'Phone number'], ['number', 'Number'], ['currency', 'Currency'],
    ['date', 'Date'], ['time', 'Time'], ['select', 'Dropdown'], ['radio', 'Choice buttons'], ['multiselect', 'Checkboxes'], ['checkbox', 'Yes / no'],
    ['attachments', 'Attachments'], ['calculated', 'Calculated'], ['section', 'Section heading']];
  NODE_INFO.start = { type: 'start', label: 'Start', hint: '', tone: 'indigo' };
  const APPROVAL_PORTS = [['approved', 'Approved'], ['rejected', 'Rejected']];
  const PORTS = { condition: [['true', 'Yes'], ['false', 'No']], approval: APPROVAL_PORTS, approval_chain: APPROVAL_PORTS, end: [] };
  const portsOf = (type) => PORTS[type] || [['next', '']];
  const COLORS = ['indigo', 'blue', 'teal', 'green', 'amber', 'rose', 'violet', 'slate'];
  const NODE_W = 230;

  const state = {
    meta: { name: '', description: '', category: '', color: 'indigo' },
    def: null,
    tab: 'form',
    selField: -1,
    selNode: null,
    selEdge: -1,
    dirty: false,
    saving: false,
    issues: [],
    showIssues: false,
    publishedVersion: 0,
    isPublished: false,
    hasUnpublishedChanges: false,
    isAdmin: false,
    canEditForm: true,
    teams: [],
  };
  const autoKeys = new WeakSet(); // fields whose key still follows the label
  let preview = null;

  // ---- utilities ---------------------------------------------------------------------

  const slug = (text) => {
    let s = String(text || '').toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 40);
    if (!s) s = 'field';
    if (/^[0-9]/.test(s)) s = 'f_' + s;
    return s;
  };
  const RESERVED = ['submitter', 'ticket', 'approval', 'approvals', 'run', 'event', 'chain', 'attachments', 'rates', 'task', '_previous', '_restart', 'true', 'false', 'null', 'blank', 'and', 'or', 'not'];
  function uniqueKey(base, taken) {
    let key = RESERVED.includes(base) ? base + '_field' : base;
    let n = 2;
    while (taken.includes(key)) key = base + '_' + (n++);
    return key;
  }
  const fieldKeys = (except) => state.def.form.fields.filter((f) => f !== except).map((f) => f.key);
  const isFormTrigger = () => state.def.trigger.type !== 'ticket';

  // ---- undo ---------------------------------------------------------------------------
  // A copy of the whole definition is kept before each structural change (adding, removing, duplicating
  // or moving a step or field, connecting steps); Undo or Ctrl+Z puts the last one back. Up to 30.
  const undoStack = [];
  function remember() {
    undoStack.push(JSON.stringify(state.def));
    if (undoStack.length > 30) undoStack.shift();
  }
  function undo() {
    if (!undoStack.length) { toast('Nothing to undo.'); return; }
    state.def = JSON.parse(undoStack.pop());
    if (state.selNode && !nodeById(state.selNode)) state.selNode = null;
    state.selEdge = -1;
    if (state.selField >= state.def.form.fields.length) state.selField = -1;
    state.dirty = false; // markDirty re-renders the top bar (and the Undo button with it)
    markDirty();
    renderAll();
    toast('Undone.');
  }

  function markDirty() {
    if (!state.dirty) { state.dirty = true; renderTop(); }
  }
  function clean(value) { return value === '' || value === undefined ? null : value; }

  // ---- loading and saving ------------------------------------------------------------

  async function load() {
    try {
      const data = await api('GET', '/api/builder/' + workflowId);
      state.meta = { name: data.name, description: data.description || '', category: data.category || '', color: data.color || 'indigo',
        owners: data.owners || [], audienceGroups: data.audienceGroups || [] };
      state.groups = data.groups || [];
      state.isAdmin = !!data.isAdmin;
      state.canEditForm = data.canEditForm !== false;
      state.teams = data.teams || [];
      state.def = normalise(data.definition);
      state.publishedVersion = data.publishedVersion;
      state.isPublished = data.isPublished;
      state.hasUnpublishedChanges = data.hasUnpublishedChanges;
      if (!isFormTrigger() || !state.canEditForm) state.tab = 'flow';
      renderAll();
    } catch (e) {
      root.textContent = '';
      root.appendChild(h('div', { class: 'flash bad' }, e.message));
    }
  }

  function normalise(def) {
    def = def || {};
    def.trigger = def.trigger || { type: 'form' };
    def.form = def.form || {};
    def.form.fields = def.form.fields || [];
    def.flow = def.flow || {};
    def.flow.nodes = def.flow.nodes || [];
    def.flow.edges = def.flow.edges || [];
    for (const n of def.flow.nodes) { n.config = n.config || {}; n.customFields = n.customFields || {}; }
    return def;
  }

  const payload = () => Object.assign({}, state.meta, { definition: state.def });

  async function save(publish) {
    if (state.saving) return;
    state.saving = true;
    renderTop();
    try {
      const result = publish
        ? await api('POST', '/api/builder/' + workflowId + '/publish', payload())
        : await api('PUT', '/api/builder/' + workflowId, payload());
      state.dirty = false;
      state.issues = result.issues || [];
      const errors = state.issues.filter((i) => i.severity === 'error').length;
      if (publish) {
        if (result.ok) {
          state.isPublished = true;
          state.hasUnpublishedChanges = false;
          state.publishedVersion = result.publishedVersion;
          toast('Published version ' + result.publishedVersion + '. It is live now.');
        } else {
          state.hasUnpublishedChanges = state.isPublished;
          state.showIssues = true;
          toast('Draft saved, but not published: fix ' + errors + ' problem' + (errors === 1 ? '' : 's') + ' first.', 'bad');
        }
      } else {
        state.hasUnpublishedChanges = !!result.hasUnpublishedChanges;
        toast(errors ? 'Draft saved. ' + errors + ' problem' + (errors === 1 ? '' : 's') + ' to fix before publishing.' : 'Draft saved.');
        if (errors) state.showIssues = true;
      }
    } catch (e) {
      toast(e.message, 'bad');
    } finally {
      state.saving = false;
      renderAll();
    }
  }

  async function check() {
    try {
      const result = await api('POST', '/api/builder/validate', state.def);
      state.issues = result.issues || [];
      state.showIssues = true;
      if (state.issues.length === 0) toast('No problems found.');
      renderAll();
    } catch (e) {
      toast(e.message, 'bad');
    }
  }

  window.addEventListener('beforeunload', (e) => {
    if (state.dirty) { e.preventDefault(); e.returnValue = ''; }
  });
  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); save(false); return; }
    const typing = e.target.closest && e.target.closest('input, textarea, select, dialog, [contenteditable]');
    if (typing) return; // inside a text box, Ctrl+Z undoes typing as usual
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'z') { e.preventDefault(); undo(); return; }
    if (state.tab !== 'flow') return;
    if (e.key === 'Delete' || e.key === 'Backspace') {
      if (state.selEdge >= 0) { e.preventDefault(); removeEdge(state.selEdge); }
      else if (state.selNode) { e.preventDefault(); removeNode(state.selNode); }
    }
  });

  // ---- shared inputs -----------------------------------------------------------------

  let rowIds = 0;
  function row(label, control, help) {
    const caption = h('label', { class: 'b-label' }, label);
    // Tie the label to its control so screen readers announce it and clicking the label focuses it.
    const target = control && control.matches && control.matches('input, select, textarea') ? control
      : control && control.querySelector ? control.querySelector('input, select, textarea') : null;
    if (target) {
      if (!target.id) target.id = 'b-field-' + (++rowIds);
      caption.setAttribute('for', target.id);
    }
    return h('div', { class: 'b-row' }, caption, control, help ? h('p', { class: 'b-help' }, help) : null);
  }

  function textInput(value, onChange, attrs) {
    const el = h('input', Object.assign({ type: 'text', value: value || '' }, attrs || {}));
    el.addEventListener('input', () => { onChange(el.value); markDirty(); });
    return el;
  }

  /** Grows a textarea to fit its text (up to a limit) so long templates are never clipped. */
  function autosize(el) {
    const fit = () => {
      el.style.height = 'auto';
      el.style.height = Math.min(el.scrollHeight + 2, 340) + 'px';
    };
    el.addEventListener('input', fit);
    requestAnimationFrame(fit);
  }

  function textArea(value, onChange, rows) {
    const el = h('textarea', { rows: rows || 3 });
    el.value = value || '';
    el.addEventListener('input', () => { onChange(el.value); markDirty(); });
    autosize(el);
    return el;
  }

  function numberInput(value, onChange, attrs) {
    const el = h('input', Object.assign({ type: 'number', value: value === null || value === undefined ? '' : value }, attrs || {}));
    el.addEventListener('input', () => {
      const n = el.value === '' ? null : Number(el.value);
      onChange(Number.isFinite(n) ? n : null);
      markDirty();
    });
    return el;
  }

  function select(value, options, onChange) {
    const el = h('select', null, options.map(([v, label]) => h('option', { value: v }, label)));
    el.value = value || '';
    if (el.value !== (value || '') && value) {
      el.appendChild(h('option', { value }, value + ' (unknown)'));
      el.value = value;
    }
    el.addEventListener('change', () => { onChange(el.value); markDirty(); });
    return el;
  }

  function toggle(label, checked, onChange) {
    const box = h('input', { type: 'checkbox', checked: !!checked });
    box.addEventListener('change', () => { onChange(box.checked); markDirty(); });
    return h('label', { class: 'b-toggle' }, box, h('span', null, label));
  }

  /**
   * Formula or template editor with live checking and an insert picker.
   * template=true means plain text with {{formula}} placeholders.
   */
  function formulaInput(value, onChange, opts) {
    opts = opts || {};
    const area = h('textarea', { rows: opts.rows || (opts.template ? 2 : 2), class: opts.template ? 'b-template' : 'b-formula', spellcheck: opts.template ? 'true' : 'false',
      placeholder: opts.placeholder || null });
    area.value = value || '';
    autosize(area);
    const status = h('div', { class: 'b-fx-status' });
    let token = 0;
    const verify = debounce(async () => {
      const source = area.value;
      const mine = ++token;
      if (!source.trim() || (opts.template && !source.includes('{{'))) { status.textContent = ''; status.className = 'b-fx-status'; return; }
      try {
        const result = await api('POST', '/api/builder/check-formula', { source, template: !!opts.template });
        if (mine !== token) return;
        if (!result.ok) { status.textContent = result.error; status.className = 'b-fx-status bad'; return; }
        const known = knownNames(opts.context);
        const unknown = (result.references || []).filter((r) => !known.has(r.toLowerCase()) && !(opts.extra || []).includes(r.toLowerCase()));
        if (unknown.length) {
          status.textContent = 'Unknown name' + (unknown.length > 1 ? 's' : '') + ': ' + unknown.join(', ') + ' — will be treated as blank.';
          status.className = 'b-fx-status warn';
        } else {
          status.textContent = '✓ Looks good';
          status.className = 'b-fx-status ok';
        }
      } catch (e) {
        if (mine === token) { status.textContent = ''; status.className = 'b-fx-status'; }
      }
    }, 350);
    area.addEventListener('input', () => { onChange(area.value); markDirty(); verify(); });
    if (area.value.trim()) verify();
    const insert = h('button', { type: 'button', class: 'b-fx-btn', title: 'Insert a field or function',
      onclick: () => openPicker(area, opts, () => { onChange(area.value); markDirty(); verify(); }) }, 'ƒx Insert');
    return h('div', { class: 'b-fx' }, area, h('div', { class: 'b-fx-foot' }, status, insert));
  }

  /** Names a formula may mention, for the "unknown name" hint. */
  function knownNames(context) {
    const names = new Set(RESERVED);
    if (isFormTrigger()) state.def.form.fields.forEach((f) => { if (f.key && !['section', 'page', 'worksheet'].includes(f.type)) names.add(f.key.toLowerCase()); });
    if (context === 'flow') {
      state.def.flow.nodes.forEach((n) => {
        if (n.type === 'task') (n.fields || []).forEach((f) => { if (f.key) names.add(f.key.toLowerCase()); });
        if (n.type === 'set_variable' && n.config.name) names.add(n.config.name.trim().toLowerCase());
        if (n.type === 'fs_lookup' && n.config.saveAs) names.add(n.config.saveAs.trim().toLowerCase());
      });
    }
    return names;
  }

  function pickerValues(opts) {
    const items = [];
    if (opts.columns) opts.columns.forEach((c) => { if (c.key) items.push([c.key, 'This row: ' + (c.label || c.key)]); });
    if (isFormTrigger()) {
      for (const f of state.def.form.fields) {
        if (!f.key || ['section', 'page', 'worksheet'].includes(f.type)) continue;
        if (f.type === 'table') {
          items.push(['COUNT(' + f.key + ')', 'Number of rows in ' + (f.label || f.key)]);
          (f.columns || []).forEach((c) => { if (c.key) items.push([f.key + '.' + c.key, (f.label || f.key) + ' → ' + (c.label || c.key) + ' (list)']); });
        } else if (f.type === 'lookup') {
          items.push([f.key + '.name', (f.label || f.key) + ' (name)']);
          items.push([f.key + '.id', (f.label || f.key) + ' (Freshservice ID)']);
        } else if (f.type === 'department' || f.type === 'location') {
          items.push([f.key + '.name', (f.label || f.key) + ' (name)']);
        } else if (f.type === 'attachments') {
          items.push(['COUNT(' + f.key + ')', 'Number of files in ' + (f.label || f.key)]);
        } else if (f.type === 'approval_chain') {
          // A chain is not a simple value; it is used by the "Approval chain" flow step.
        } else {
          items.push([f.key, f.label || f.key]);
        }
      }
      items.push(['submitter.name', 'Name of the person submitting'], ['submitter.email', 'Email of the person submitting']);
    }
    if (opts.context === 'flow') {
      items.push(['ticket.id', 'Ticket number'], ['ticket.status', 'Ticket status (2 Open, 3 Pending, 4 Resolved, 5 Closed)'],
        ['ticket.priority', 'Ticket priority (1 Low … 4 Urgent)'], ['ticket.subject', 'Ticket subject'],
        ['approval.by', 'Who made the last approval decision'], ['approval.comment', "The approver's comment"],
        ['approval.approved', 'True if the last decision was Approve'], ['run.id', 'Request number in SchoolFlow'],
        ['JOIN(attachments.name, ", ")', 'Names of all the files uploaded with the request'], ['COUNT(attachments)', 'How many files were uploaded']);
      state.def.flow.nodes.forEach((n) => {
        if (n.type === 'set_variable' && n.config.name) items.push([n.config.name.trim(), 'Value set by “' + (n.label || 'Set value') + '”']);
        if (n.type === 'fs_lookup' && n.config.saveAs) items.push([n.config.saveAs.trim() + '.name', 'Looked up by “' + (n.label || 'Look up') + '”']);
        if (n.type === 'task') (n.fields || []).forEach((f) => {
          if (f.key && f.type !== 'section') items.push([f.type === 'attachments' ? 'COUNT(' + f.key + ')' : f.key, (f.label || f.key) + ' — filled in at “' + (n.label || 'Task') + '”']);
        });
      });
      if (state.def.flow.nodes.some((n) => n.type === 'task'))
        items.push(['task.by', 'Who completed the last task'], ['task.step', 'Name of the last task completed']);
    }
    return items;
  }

  function openPicker(area, opts, done) {
    const search = h('input', { type: 'search', placeholder: 'Search fields and functions…' });
    const list = h('div', { class: 'picker-list' });
    const dialog = h('dialog', { class: 'dialog picker' },
      h('div', { class: 'row-between' }, h('h2', null, 'Insert'), h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Close', onclick: () => dialog.close() }, '×')),
      search, list);
    const start = area.selectionStart ?? area.value.length;
    const end = area.selectionEnd ?? area.value.length;

    function insert(text, isFunction) {
      let piece = text;
      if (opts.template) {
        // Inside an open {{ … the piece goes in bare; otherwise wrap it as a placeholder.
        const before = area.value.slice(0, start);
        const inside = before.lastIndexOf('{{') > before.lastIndexOf('}}');
        if (!inside) piece = '{{' + text + (isFunction ? ')' : '') + '}}';
      }
      area.value = area.value.slice(0, start) + piece + area.value.slice(end);
      dialog.close();
      area.focus();
      const caret = start + piece.length - (opts.template && piece.endsWith(')}}') ? 3 : 0);
      area.setSelectionRange(caret, caret);
      done();
    }

    function render() {
      const q = search.value.trim().toLowerCase();
      list.textContent = '';
      const valuesMatched = pickerValues(opts).filter(([code, label]) => !q || code.toLowerCase().includes(q) || label.toLowerCase().includes(q));
      if (valuesMatched.length) {
        list.appendChild(h('div', { class: 'picker-head' }, 'Fields and values'));
        valuesMatched.forEach(([code, label]) => list.appendChild(
          h('button', { type: 'button', class: 'picker-item', onclick: () => insert(code, false) }, h('code', null, code), h('span', null, label))));
      }
      const functions = catalog.filter((f) => !q || f.name.toLowerCase().includes(q) || f.description.toLowerCase().includes(q));
      let category = null;
      for (const f of functions) {
        if (f.category !== category) { category = f.category; list.appendChild(h('div', { class: 'picker-head' }, category + ' functions')); }
        list.appendChild(h('button', { type: 'button', class: 'picker-item', onclick: () => insert(f.name + '(', true) },
          h('code', null, f.signature), h('span', null, f.description)));
      }
      if (!list.firstChild) list.appendChild(h('p', { class: 'muted' }, 'Nothing matches.'));
    }
    search.addEventListener('input', render);
    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    render();
    dialog.showModal();
    search.focus();
  }

  // ---- top bar -----------------------------------------------------------------------

  const top = h('div', { class: 'bld-top' });
  const issuesBox = h('div', { class: 'bld-issues', hidden: true });
  const body = h('div', { class: 'bld-body' });

  function renderTop() {
    const errors = state.issues.filter((i) => i.severity === 'error').length;
    const warnings = state.issues.length - errors;
    const status = !state.isPublished ? h('span', { class: 'pill muted' }, 'Draft — not published')
      : state.hasUnpublishedChanges || state.dirty ? h('span', { class: 'pill warn' }, 'Live v' + state.publishedVersion + ' · unpublished changes')
        : h('span', { class: 'pill ok' }, 'Live · v' + state.publishedVersion);
    const tabs = [['form', 'Form'], ['flow', 'Flow'], ['preview', 'Preview'], ['settings', 'Settings']]
      .filter(([id]) => isFormTrigger() || (id !== 'form' && id !== 'preview'))
      // Owners who are not admins change the flow only; the form is the administrators'.
      .filter(([id]) => state.canEditForm || id !== 'form');
    top.textContent = '';
    top.append(
      h('div', { class: 'bld-title' },
        h('a', { class: 'icon-btn', href: '/Builder', title: 'Back to workflows', 'aria-label': 'Back to workflows' }, '←'),
        h('div', null, h('strong', null, state.meta.name || 'Untitled workflow'), h('div', null, status,
          state.dirty ? h('span', { class: 'muted small' }, ' Unsaved changes') : null))),
      h('div', { class: 'bld-tabs', role: 'tablist' }, tabs.map(([id, label]) =>
        h('button', { type: 'button', role: 'tab', class: state.tab === id ? 'on' : '', 'aria-selected': state.tab === id ? 'true' : 'false',
          onclick: () => { state.tab = id; renderAll(); } }, label))),
      h('div', { class: 'bld-actions' },
        state.issues.length ? h('button', { type: 'button', class: 'btn small ' + (errors ? 'ghost-danger' : ''),
          onclick: () => { state.showIssues = !state.showIssues; renderIssues(); } },
        (errors ? errors + ' problem' + (errors === 1 ? '' : 's') : '') + (errors && warnings ? ', ' : '') + (warnings ? warnings + ' warning' + (warnings === 1 ? '' : 's') : '')) : null,
        h('button', { type: 'button', class: 'btn small', disabled: !undoStack.length, title: 'Undo the last change to steps or fields (Ctrl+Z)', onclick: undo }, 'Undo'),
        h('button', { type: 'button', class: 'btn small', onclick: check }, 'Check'),
        h('button', { type: 'button', class: 'btn small', disabled: state.saving, onclick: () => save(false) }, state.saving ? 'Saving…' : 'Save draft'),
        h('button', { type: 'button', class: 'btn small primary', disabled: state.saving, onclick: () => save(true) }, 'Publish')));
  }

  function renderIssues() {
    issuesBox.hidden = !state.showIssues || state.issues.length === 0;
    issuesBox.textContent = '';
    if (issuesBox.hidden) return;
    issuesBox.appendChild(h('div', { class: 'row-between' }, h('strong', null, 'Check results'),
      h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Hide', onclick: () => { state.showIssues = false; renderIssues(); } }, '×')));
    for (const issue of state.issues) {
      issuesBox.appendChild(h('button', { type: 'button', class: 'issue ' + issue.severity, onclick: () => goToIssue(issue) },
        h('span', { class: 'pill ' + (issue.severity === 'error' ? 'bad' : 'warn') }, issue.severity === 'error' ? 'Problem' : 'Warning'),
        h('span', null, issue.message)));
    }
  }

  function goToIssue(issue) {
    if (issue.area === 'form') {
      state.tab = 'form';
      state.selField = state.def.form.fields.findIndex((f) => f.key === issue.target);
    } else if (issue.area === 'flow') {
      state.tab = 'flow';
      state.selNode = issue.target || null;
      state.selEdge = -1;
    } else {
      state.tab = 'settings';
    }
    renderAll();
  }

  const issueTargets = (area) => new Set(state.issues.filter((i) => i.area === area && i.severity === 'error' && i.target).map((i) => i.target));

  function renderAll() {
    if (preview) { preview.destroy(); preview = null; }
    renderTop();
    renderIssues();
    body.textContent = '';
    body.className = 'bld-body tab-' + state.tab;
    if (state.tab === 'form') renderFormTab();
    else if (state.tab === 'flow') renderFlowTab();
    else if (state.tab === 'preview') renderPreviewTab();
    else renderSettingsTab();
  }

  // ---- form tab ----------------------------------------------------------------------

  function addField(type) {
    const info = FIELD_TYPES.find((t) => t.type === type);
    const label = type === 'section' ? 'New section' : type === 'page' ? 'Next page' : type === 'worksheet' ? 'Worksheet' : 'New ' + info.label.toLowerCase();
    remember();
    const field = { key: uniqueKey(slug(label), fieldKeys()), label, type };
    if (type === 'worksheet') field.rows = [{ number: '1.', item: 'Item', description: 'Description' }];
    if (['select', 'radio', 'multiselect'].includes(type)) field.options = ['Option 1', 'Option 2'];
    if (type === 'lookup') field.source = 'departments';
    if (type === 'calculated') field.formula = '';
    if (type === 'table') field.columns = [{ key: 'item', label: 'Item', type: 'text' }, { key: 'qty', label: 'Qty', type: 'number' }];
    autoKeys.add(field);
    const at = state.selField >= 0 ? state.selField + 1 : state.def.form.fields.length;
    state.def.form.fields.splice(at, 0, field);
    state.selField = at;
    markDirty();
    renderAll();
    const first = body.querySelector('.bld-inspector input');
    if (first) { first.focus(); first.select(); }
  }

  function fieldBadges(f) {
    const badges = [];
    if (f.required) badges.push(['Required', 'bad']);
    if (f.requiredWhen) badges.push(['Required when…', 'bad']);
    if (f.visibleWhen) badges.push(['Shown when…', 'info']);
    if (f.type === 'calculated') badges.push(['ƒx', 'ok']);
    if (f.rules && f.rules.length) badges.push([f.rules.length + ' rule' + (f.rules.length === 1 ? '' : 's'), 'warn']);
    if (f.noCopy && state.def.form.allowCopy) badges.push(['Not copied', 'muted']);
    return badges.map(([text, tone]) => h('span', { class: 'pill tiny ' + tone }, text));
  }

  let fieldList = null;
  function renderFieldList() {
    const fields = state.def.form.fields;
    const bad = issueTargets('form');
    fieldList.textContent = '';
    if (fields.length === 0) {
      fieldList.appendChild(h('div', { class: 'empty small' }, h('p', null, 'Add your first field from the list on the left.')));
      return;
    }
    fields.forEach((f, index) => {
      const info = FIELD_TYPES.find((t) => t.type === f.type) || { label: f.type };
      const card = h('div', {
        class: 'fld-card' + (index === state.selField ? ' selected' : '') + (['section', 'page', 'worksheet'].includes(f.type) ? ' section' : '') + (bad.has(f.key) ? ' invalid' : '') + (f.width === 'half' ? ' half' : ''),
        draggable: 'true', tabindex: '0',
        onclick: () => { state.selField = index; renderAll(); },
        onkeydown: (e) => { if (e.key === 'Enter' && e.target === card) { state.selField = index; renderAll(); } },
        ondragstart: (e) => { e.dataTransfer.setData('text/plain', String(index)); e.dataTransfer.effectAllowed = 'move'; card.classList.add('dragging'); },
        ondragend: () => card.classList.remove('dragging'),
        ondragover: (e) => { e.preventDefault(); card.classList.add('drop'); },
        ondragleave: () => card.classList.remove('drop'),
        ondrop: (e) => {
          e.preventDefault();
          card.classList.remove('drop');
          const from = Number(e.dataTransfer.getData('text/plain'));
          if (!Number.isInteger(from) || from === index || from < 0 || from >= fields.length) return;
          const [moved] = fields.splice(from, 1);
          fields.splice(index, 0, moved);
          state.selField = index;
          markDirty();
          renderAll();
        },
      },
      h('span', { class: 'fld-grip', 'aria-hidden': 'true' }, '⋮⋮'),
      h('div', { class: 'grow' },
        h('div', { class: 'fld-label' }, f.label || '(no label)'),
        h('div', { class: 'fld-meta' }, h('span', null, info.label), !['section', 'page', 'worksheet'].includes(f.type) ? h('code', null, f.key) : null, fieldBadges(f))),
      h('div', { class: 'fld-tools' },
        h('button', { type: 'button', class: 'icon-btn', title: 'Move up', 'aria-label': 'Move up', disabled: index === 0, onclick: (e) => { e.stopPropagation(); moveField(index, -1); } }, '↑'),
        h('button', { type: 'button', class: 'icon-btn', title: 'Move down', 'aria-label': 'Move down', disabled: index === fields.length - 1, onclick: (e) => { e.stopPropagation(); moveField(index, 1); } }, '↓'),
        h('button', { type: 'button', class: 'icon-btn', title: 'Duplicate', 'aria-label': 'Duplicate', onclick: (e) => { e.stopPropagation(); duplicateField(index); } }, '⧉'),
        h('button', { type: 'button', class: 'icon-btn danger', title: 'Delete', 'aria-label': 'Delete', onclick: (e) => { e.stopPropagation(); deleteField(index); } }, '×')));
      fieldList.appendChild(card);
    });
  }

  function moveField(index, delta) {
    remember();
    const fields = state.def.form.fields;
    const [moved] = fields.splice(index, 1);
    fields.splice(index + delta, 0, moved);
    state.selField = index + delta;
    markDirty();
    renderAll();
  }
  function duplicateField(index) {
    remember();
    const fields = state.def.form.fields;
    const copy = JSON.parse(JSON.stringify(fields[index]));
    copy.key = uniqueKey(copy.key, fieldKeys());
    copy.label = (copy.label || '') + ' (copy)';
    fields.splice(index + 1, 0, copy);
    state.selField = index + 1;
    markDirty();
    renderAll();
  }
  function deleteField(index) {
    const fields = state.def.form.fields;
    const key = fields[index].key;
    const used = key && JSON.stringify(Object.assign({}, state.def, { form: { fields: fields.filter((_, i) => i !== index) } }))
      .match(new RegExp('\\b' + key.replace(/[^A-Za-z0-9_]/g, '') + '\\b'));
    if (used && !window.confirm('Other formulas or flow steps mention “' + key + '”. Delete this field anyway?')) return;
    remember();
    fields.splice(index, 1);
    state.selField = Math.min(index, fields.length - 1);
    markDirty();
    renderAll();
  }

  function renderFormTab() {
    const palette = h('aside', { class: 'bld-palette' }, h('h3', null, 'Add a field'),
      FIELD_TYPES.filter((t) => state.isAdmin || t.type !== 'lookup').map((t) => h('button', { type: 'button', class: 'pal-item', onclick: () => addField(t.type) },
        h('strong', null, t.label), h('small', null, t.hint))));
    fieldList = h('div', { class: 'fld-list' });
    const center = h('section', { class: 'bld-center' },
      h('div', { class: 'bld-center-head' }, h('h3', null, 'Form fields'), h('span', { class: 'muted small' }, 'Drag to reorder · click to edit')),
      fieldList);
    const inspector = h('aside', { class: 'bld-inspector' });
    body.append(palette, center, inspector);
    renderFieldList();
    const field = state.def.form.fields[state.selField];
    if (field) renderFieldInspector(inspector, field);
    else renderFormSettings(inspector);
  }

  function renderFormSettings(panel) {
    const form = state.def.form;
    panel.append(
      h('h3', null, 'Form settings'),
      h('p', { class: 'muted small' }, 'Select a field to edit it. These settings apply to the whole form.'),
      row('Introduction', textArea(form.intro, (v) => { form.intro = clean(v); }, 4), 'Shown at the top of the form.'),
      row('Submit button text', textInput(form.submitLabel, (v) => { form.submitLabel = clean(v); }, { placeholder: 'Submit' })),
      row('Look', select(form.style || '', [['', 'Standard'], ['paper', 'Paper form — labels beside underlined answers']], (v) => { form.style = clean(v); }),
        'The paper look suits forms staff already know from a printed version. Start the introduction lines with • to show them as notices.'),
      h('h4', { class: 'b-section' }, 'When the form is open'),
      h('div', { class: 'b-pair' },
        row('Opens on', textInput(form.opensOn, (v) => { form.opensOn = clean(v); }, { type: 'date' })),
        row('Closes after', textInput(form.closesOn, (v) => { form.closesOn = clean(v); }, { type: 'date' }))),
      h('p', { class: 'b-help' }, 'Leave both blank for a form that is always open. Outside these dates the form stays on the home page but says when it opens or that it has closed. Requests already submitted carry on as normal.'),
      h('h4', { class: 'b-section' }, 'Start from a previous request'),
      row('Can people copy a previous request?', select(form.allowCopy || '', [
        ['', 'No — every request starts blank'],
        ['own', 'Yes — their own previous requests'],
        ['everyone', "Yes — anyone's previous requests"],
      ], (v) => { form.allowCopy = clean(v); renderAll(); }),
      form.allowCopy === 'everyone'
        ? 'Everyone who can open this form can see what others submitted on it (except fields you exclude). Good for yearly requests such as field trips; avoid it for forms with private details.'
        : form.allowCopy === 'own'
          ? 'People see a list of their own earlier requests and can prefill the form from one.'
          : 'Turn this on for forms that are filled in again and again, so people can prefill from an earlier request.'));
    if (form.allowCopy) {
      const excluded = form.fields.filter((f) => f.noCopy).map((f) => f.label || f.key);
      panel.append(h('p', { class: 'b-help' }, excluded.length
        ? 'Not copied: ' + excluded.join(', ') + '. Calculated fields are always worked out fresh.'
        : 'Every answer is copied. To leave one out (a date, a signature tick box…), select the field and tick “Don\'t copy from previous requests”. Calculated fields are always worked out fresh.'));
    }
  }

  function renderFieldInspector(panel, field) {
    const refresh = () => renderFieldList();
    const info = FIELD_TYPES.find((t) => t.type === field.type) || { label: field.type };
    const isSheet = field.type === 'worksheet';
    const isPage = field.type === 'page' || isSheet;   // layout-only: no logic settings
    const isSection = field.type === 'section' || isPage;
    const isCalc = field.type === 'calculated';
    const keyInput = textInput(field.key, (v) => { field.key = v.trim(); autoKeys.delete(field); refresh(); }, { spellcheck: 'false', class: 'mono' });

    panel.append(
      h('div', { class: 'row-between' }, h('h3', null, info.label),
        h('button', { type: 'button', class: 'btn small', onclick: () => { state.selField = -1; renderAll(); } }, 'Form settings')),
      row(isSheet ? 'Title' : isPage ? 'Page title' : isSection ? 'Heading' : 'Label', textInput(field.label, (v) => {
        field.label = v;
        if (autoKeys.has(field)) { field.key = uniqueKey(slug(v), fieldKeys(field)); keyInput.value = field.key; }
        refresh();
      })),
      row('Key', keyInput, isSection ? 'Internal name for this heading.' : 'The name formulas use for this field. Letters, digits and underscores.'),
      row('Help text', textInput(field.help, (v) => { field.help = clean(v); })));

    if (['text', 'textarea', 'number', 'currency', 'email', 'phone', 'lookup'].includes(field.type) && field.source !== 'departments')
      panel.append(row('Placeholder', textInput(field.placeholder, (v) => { field.placeholder = clean(v); })));

    if (isSheet) {
      const toText = (rows) => (rows || []).map((r) => [r.number || '', r.item || '', r.description || '', r.cost || ''].join(' | ')).join('\n');
      const fromText = (text) => text.split('\n').filter((l) => l.trim()).map((l) => {
        const [number, item, description, cost] = l.split('|').map((p) => p.trim());
        const r = {};
        if (number) r.number = number;
        if (item) r.item = item;
        if (description) r.description = description;
        if (cost) r.cost = cost;
        return r;
      });
      panel.append(
        row('Rows', textArea(toText(field.rows), (v) => { field.rows = fromText(v); refresh(); }, 12),
          'One row per line, in four parts separated by | :  number | item | description | cost field.'),
        h('p', { class: 'b-help' }, 'A worksheet arranges other fields of this form in a table, like the cost sheet on a paper form. Add those fields to the form as usual (number, currency, calculated…), then name them here: put a field inside the description by writing its key in braces, for example  # of nights {nights} x {rate} / night , and put the key of the field for the last column (often a calculated one) in the fourth part. Each field can be used once. A last line such as  | | Total: | total  makes a total row.'));
      return;
    }
    if (isPage)
      panel.append(h('p', { class: 'b-help' }, 'Everything below this starts a new page. People move between pages with Next and Back, and each page is checked before they go on. The title is shown in the step list at the top of the form.'));
    if (field.type === 'student')
      panel.append(h('p', { class: 'b-help' }, 'The person searches the student roster by name or ID. Only staff an admin has given student access (Admin → Student access) can fill this in, and only they see the student on the request afterwards — everyone else sees “Restricted”. In formulas and messages use ' + (field.key || 'field') + '.name, .id, .school, .grade and .academy. Be careful: anything you put in a ticket, email or approval message is seen by whoever receives it, with or without student access.'));
    if (field.type === 'signature')
      panel.append(h('p', { class: 'b-help' }, 'The person types their full name to sign. The request already records who submitted it and when; this adds their explicit agreement. It is never copied from a previous request.'));
    if (field.type === 'time')
      panel.append(h('p', { class: 'b-help' }, 'Stored as 24-hour text such as 14:30, so formulas can compare times: ' + (field.key || 'field') + ' >= "08:00".'));
    if (field.type === 'department')
      panel.append(h('p', { class: 'b-help' }, 'People choose from the departments admins have set up (Admin → Departments). The request is filed under it, and an “Approval chain” flow step can add that department\'s final approver.'));
    if (field.type === 'location')
      panel.append(h('p', { class: 'b-help' }, 'People choose from the school\'s locations (Admin → Locations, which can be synced from Freshservice). In formulas use ' + (field.key || 'field') + '.name.'));
    if (field.type === 'attachments')
      panel.append(
        row('Most files allowed', numberInput(field.maxFiles, (v) => { field.maxFiles = v === null ? null : Math.max(1, Math.min(20, Math.round(v))); }, { min: 1, max: 20, step: 1, placeholder: '5' })),
        h('p', { class: 'b-help' }, 'People can upload PDFs, images, Word, Excel, PowerPoint and text files. Files show on the request for the requester, approvers and builders. To send them to Freshservice, tick “Attach the request\'s files” on a Create ticket step.'));
    if (field.type === 'approval_chain')
      panel.append(h('p', { class: 'b-help' }, 'People pick a saved chain or list their own approvers in order. Add an “Approval chain” step to the flow and point it at this field to run it.'));

    if (!isSection && !['table', 'textarea', 'approval_chain', 'attachments'].includes(field.type))
      panel.append(row('Width', select(field.width || '', [['', 'Full width'], ['half', 'Half width (side by side)']], (v) => { field.width = clean(v); refresh(); })));

    if (['select', 'radio', 'multiselect'].includes(field.type))
      panel.append(row('Options', textArea((field.options || []).join('\n'), (v) => {
        field.options = v.split('\n').map((o) => o.trim()).filter(Boolean);
      }, 5), 'One option per line.'));

    if (field.type === 'number' || field.type === 'currency')
      panel.append(h('div', { class: 'b-pair' },
        row('Minimum', numberInput(field.min, (v) => { field.min = v; }, { step: 'any' })),
        row('Maximum', numberInput(field.max, (v) => { field.max = v; }, { step: 'any' }))));

    if (field.type === 'lookup')
      panel.append(row('Look up', select(field.source, LOOKUP_SOURCES, (v) => { field.source = v; }), 'Choices are loaded live from Freshservice. In formulas use ' + (field.key || 'field') + '.name or .id.'));

    if (isCalc) {
      panel.append(
        row('Formula', formulaInput(field.formula, (v) => { field.formula = v; refresh(); }, { rows: 4, placeholder: 'e.g. quantity * unit_price' }),
          'Recalculated whenever the fields it mentions change.'),
        h('div', { class: 'b-pair' },
          row('Show as', select(field.format || '', [['', 'Automatic'], ['number', 'Number'], ['currency', 'Currency ($)']], (v) => { field.format = clean(v); })),
          row('Decimal places', numberInput(field.decimals, (v) => { field.decimals = v === null ? null : Math.max(0, Math.min(10, Math.round(v))); }, { min: 0, max: 10, step: 1 }))));
    }

    if (field.type === 'table') renderColumnsEditor(panel, field);

    if (!isSection && !isCalc && !['table', 'lookup', 'department', 'approval_chain', 'location', 'attachments'].includes(field.type))
      panel.append(row('Default value (formula)', formulaInput(field.default, (v) => { field.default = clean(v); }, { placeholder: field.type === 'date' ? 'TODAY()' : 'e.g. submitter.email' }),
        'Worked out once when the form opens. Put plain text in quotes.'));

    // ---- logic ----
    if (isPage) return;
    panel.append(h('h4', { class: 'b-section' }, 'Logic'));
    panel.append(row('Show this ' + (isSection ? 'heading' : 'field') + ' only when', formulaInput(field.visibleWhen, (v) => { field.visibleWhen = clean(v); refresh(); },
      { placeholder: 'Always shown. e.g. kind == "Software"' }), isSection ? null : 'While hidden, the field is cleared and never required.'));

    if (!isSection && !isCalc && state.def.form.allowCopy)
      panel.append(toggle("Don't copy from previous requests", field.noCopy, (v) => { if (v) field.noCopy = true; else delete field.noCopy; refresh(); }),
        h('p', { class: 'b-help' }, 'When someone starts from a previous request, this field stays blank (or takes its default) and its old answer is not shown.'));

    if (!isSection && !isCalc) {
      panel.append(
        toggle('Always required', field.required, (v) => { field.required = v; refresh(); }),
        row('Required when', formulaInput(field.requiredWhen, (v) => { field.requiredWhen = clean(v); refresh(); }, { placeholder: 'e.g. total >= 500' })));

      const rulesBox = h('div', { class: 'b-rules' });
      const renderRules = () => {
        rulesBox.textContent = '';
        (field.rules || []).forEach((rule, i) => {
          rulesBox.appendChild(h('div', { class: 'b-rule' },
            h('div', { class: 'row-between' }, h('span', { class: 'b-label' }, 'Must be true'),
              h('button', { type: 'button', class: 'icon-btn danger', 'aria-label': 'Remove rule', onclick: () => { field.rules.splice(i, 1); if (!field.rules.length) delete field.rules; markDirty(); renderRules(); refresh(); } }, '×')),
            formulaInput(rule.check, (v) => { rule.check = v; }, { placeholder: 'e.g. ' + (field.key || 'value') + ' <= 100' }),
            row('Message if not', textInput(rule.message, (v) => { rule.message = v; }, { placeholder: 'Shown to the person filling in the form' }))));
        });
        rulesBox.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => {
          field.rules = field.rules || [];
          field.rules.push({ check: '', message: '' });
          markDirty(); renderRules(); refresh();
        } }, '+ Add validation rule'));
      };
      renderRules();
      panel.append(h('h4', { class: 'b-section' }, 'Validation rules'), rulesBox);
    }
  }

  function renderColumnsEditor(panel, field) {
    field.columns = field.columns || [];
    const box = h('div', { class: 'b-columns' });
    const render = () => {
      box.textContent = '';
      field.columns.forEach((col, i) => {
        const taken = () => field.columns.filter((c) => c !== col).map((c) => c.key);
        const keyInput = textInput(col.key, (v) => { col.key = v.trim(); autoKeys.delete(col); }, { spellcheck: 'false', class: 'mono' });
        const extra = h('div');
        const renderExtra = () => {
          extra.textContent = '';
          if (col.type === 'select') extra.append(row('Options', textArea((col.options || []).join('\n'), (v) => { col.options = v.split('\n').map((o) => o.trim()).filter(Boolean); }, 3), 'One per line.'));
          if (col.type === 'calculated') extra.append(
            row('Formula', formulaInput(col.formula, (v) => { col.formula = v; }, { columns: field.columns, extra: field.columns.map((c) => (c.key || '').toLowerCase()), placeholder: 'e.g. qty * unit_price' }),
              'Use the other column keys for values in the same row.'),
            h('div', { class: 'b-pair' },
              row('Show as', select(col.format || '', [['', 'Automatic'], ['number', 'Number'], ['currency', 'Currency ($)']], (v) => { col.format = clean(v); })),
              row('Decimals', numberInput(col.decimals, (v) => { col.decimals = v === null ? null : Math.max(0, Math.min(10, Math.round(v))); }, { min: 0, max: 10, step: 1 }))));
          if (col.type !== 'calculated') extra.append(toggle('Required in every row', col.required, (v) => { col.required = v; }));
        };
        renderExtra();
        box.appendChild(h('div', { class: 'b-column' },
          h('div', { class: 'row-between' }, h('strong', null, 'Column ' + (i + 1)),
            h('span', null,
              h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Move column left', disabled: i === 0, onclick: () => { field.columns.splice(i - 1, 0, field.columns.splice(i, 1)[0]); markDirty(); render(); } }, '↑'),
              h('button', { type: 'button', class: 'icon-btn danger', 'aria-label': 'Remove column', onclick: () => { field.columns.splice(i, 1); markDirty(); render(); } }, '×'))),
          h('div', { class: 'b-pair' },
            row('Label', textInput(col.label, (v) => {
              col.label = v;
              if (autoKeys.has(col)) { col.key = uniqueKey(slug(v), taken()); keyInput.value = col.key; }
            })),
            row('Key', keyInput)),
          row('Type', select(col.type, COLUMN_TYPES, (v) => { col.type = v; if (v === 'select' && !col.options) col.options = ['Option 1']; renderExtra(); })),
          extra));
      });
      box.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => {
        const col = { key: uniqueKey('column', field.columns.map((c) => c.key)), label: 'New column', type: 'text' };
        autoKeys.add(col);
        field.columns.push(col);
        markDirty(); render();
      } }, '+ Add column'));
    };
    render();
    panel.append(h('h4', { class: 'b-section' }, 'Columns'), box,
      h('div', { class: 'b-pair' },
        row('Minimum rows', numberInput(field.minRows, (v) => { field.minRows = v === null ? null : Math.max(0, Math.round(v)); }, { min: 0, step: 1 })),
        row('Maximum rows', numberInput(field.maxRows, (v) => { field.maxRows = v === null ? null : Math.max(1, Math.round(v)); }, { min: 1, step: 1 }))),
      h('p', { class: 'b-help' }, 'Total a column from another field with SUM(' + (field.key || 'table') + '.column_key).'));
  }

  // ---- flow tab ----------------------------------------------------------------------

  let canvas = null, canvasInner = null, svg = null;
  const nodeEls = new Map();
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const nodeById = (id) => state.def.flow.nodes.find((n) => n.id === id);

  function newNodeId(type) {
    const base = type.replace(/^fs_/, '');
    let id;
    do { id = base + '_' + Math.random().toString(36).slice(2, 7); } while (nodeById(id));
    return id;
  }

  function addNode(type) {
    const info = NODE_INFO[type];
    const anchor = nodeById(state.selNode);
    let x, y;
    if (anchor) { x = anchor.x; y = anchor.y + ((nodeEls.get(anchor.id)?.offsetHeight) || 90) + 50; }
    else { x = Math.round((canvas.scrollLeft + 80) / 10) * 10; y = Math.round((canvas.scrollTop + 60) / 10) * 10; }
    // Nudge sideways until it does not sit on top of another step.
    while (state.def.flow.nodes.some((n) => Math.abs(n.x - x) < 40 && Math.abs(n.y - y) < 40)) x += 260;
    remember();
    const node = { id: newNodeId(type), type, label: info.label, x, y, config: {}, customFields: {} };
    if (type === 'fs_create_ticket') Object.assign(node.config, { subject: '', priority: '2', status: '2', attachFiles: 'true' });
    if (type === 'fs_add_note') node.config.visibility = 'private';
    if (type === 'task') node.fields = [];
    if (type === 'fs_lookup') Object.assign(node.config, { kind: 'requester', saveAs: 'person' });
    if (type === 'wait_ticket') node.config.until = 'IN(ticket.status, 4, 5)';
    if (type === 'end') node.label = 'Done';
    if (type === 'approval_chain') {
      // Point at the form's chain and department fields when it has them.
      const chainField = state.def.form.fields.find((f) => f.type === 'approval_chain');
      const departmentField = state.def.form.fields.find((f) => f.type === 'department');
      if (chainField) Object.assign(node.config, { chainSource: 'field', chainField: chainField.key });
      if (departmentField) Object.assign(node.config, { departmentSource: 'field', departmentField: departmentField.key });
    }
    if (type === 'notify') Object.assign(node.config, { to: isFormTrigger() ? '{{submitter.email}}' : '', subject: '' });
    state.def.flow.nodes.push(node);
    // Convenience: if the selected step has a free single output, connect it.
    if (anchor) {
      const free = portsOf(anchor.type).map(([p]) => p).filter((p) => !state.def.flow.edges.some((e) => e.from === anchor.id && e.port === p));
      if (free.length === 1) state.def.flow.edges.push({ from: anchor.id, port: free[0], to: node.id });
    }
    state.selNode = node.id;
    state.selEdge = -1;
    markDirty();
    renderAll();
    nodeEls.get(node.id)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  function removeNode(id) {
    const node = nodeById(id);
    if (!node) return;
    if (node.type === 'start') { toast('The Start step cannot be removed.', 'bad'); return; }
    remember();
    state.def.flow.nodes = state.def.flow.nodes.filter((n) => n.id !== id);
    state.def.flow.edges = state.def.flow.edges.filter((e) => e.from !== id && e.to !== id);
    state.selNode = null;
    markDirty();
    renderAll();
  }

  /** Copies a step (with its settings and, for a task, its fields under new keys) next to the original. */
  function duplicateNode(id) {
    const node = nodeById(id);
    if (!node || node.type === 'start') return;
    remember();
    const copy = JSON.parse(JSON.stringify(node));
    copy.id = newNodeId(node.type);
    copy.x = node.x + 30;
    copy.y = node.y + 30;
    copy.label = (node.label || '') + ' (copy)';
    if (Array.isArray(copy.fields) && copy.fields.length) {
      // Answers are stored by key, and keys must be unique across the form and every task.
      const taken = fieldKeys().concat(state.def.flow.nodes.flatMap((n) => (n.fields || []).map((f) => f.key)));
      const renamed = {};
      for (const f of copy.fields) {
        if (!f.key) continue;
        const fresh = uniqueKey(f.key, taken);
        taken.push(fresh);
        renamed[f.key] = fresh;
        f.key = fresh;
      }
      // Formulas inside the copied task that read its own fields read the copies.
      const swap = (text) => typeof text !== 'string' ? text
        : text.replace(/[A-Za-z_][A-Za-z0-9_]*/g, (word) => Object.prototype.hasOwnProperty.call(renamed, word) ? renamed[word] : word);
      for (const f of copy.fields) {
        for (const prop of ['formula', 'default', 'requiredWhen', 'visibleWhen']) if (f[prop]) f[prop] = swap(f[prop]);
        for (const rule of f.rules || []) rule.check = swap(rule.check);
      }
    }
    state.def.flow.nodes.push(copy);
    state.selNode = copy.id;
    state.selEdge = -1;
    markDirty();
    renderAll();
    toast('Step duplicated. Connect it where it should run.');
  }

  function removeEdge(index) {
    remember();
    state.def.flow.edges.splice(index, 1);
    state.selEdge = -1;
    markDirty();
    renderAll();
  }

  function nodeSummary(n) {
    const c = n.config || {};
    switch (n.type) {
      case 'start': return isFormTrigger() ? 'When the form is submitted' : 'When Freshservice sends a ticket event';
      case 'condition': return c.expression || 'No condition yet';
      case 'approval': return c.approvers ? 'Ask ' + c.approvers : 'No approver yet';
      case 'approval_chain': {
        const parts = [];
        if (c.chainSource === 'template') parts.push('a saved chain');
        if (c.chainSource === 'field') parts.push('the requester\'s chain');
        if (c.departmentSource === 'field' || c.departmentSource === 'fixed') parts.push('the department\'s final approver');
        return parts.length ? 'Runs ' + parts.join(', then ') : 'Nobody to approve yet';
      }
      case 'task': return (c.assignees ? c.assignees + ' ' : 'Nobody yet — ') + ((n.fields || []).length ? 'fills in ' + n.fields.map((f) => f.label || f.key).join(', ') : 'marks it done');
      case 'notify': return c.to ? 'To ' + c.to + (c.subject ? ': ' + c.subject : '') : 'No recipients yet';
      case 'fs_create_ticket': return c.subject || 'No subject yet';
      case 'fs_update_ticket': return 'Change ' + (['status', 'priority', 'priorityFormula', 'groupId', 'responderId'].filter((k) => c[k]).map((k) => k.replace('Formula', '').replace('Id', '')).concat(Object.keys(n.customFields || {})).join(', ') || 'nothing yet');
      case 'fs_add_note': return (c.visibility === 'public' ? 'Public: ' : 'Private: ') + (c.body || 'empty');
      case 'fs_service_request': return c.itemId ? 'Catalog item ' + c.itemId : 'No catalog item yet';
      case 'fs_lookup': return (c.kind === 'ticket' ? 'Ticket' : 'Person') + ' → ' + (c.saveAs || '?');
      case 'wait_ticket': return c.until ? 'Until ' + c.until : 'Until any update';
      case 'set_variable': return (c.name || '?') + ' = ' + (c.expression || '?');
      case 'end': return c.outcome === 'rejected' ? 'Ends as “Not approved”' : 'Ends as “Completed”';
      default: return '';
    }
  }

  function renderFlowTab() {
    const palette = h('aside', { class: 'bld-palette' }, h('h3', null, 'Add a step'),
      NODE_TYPES.filter((t) => state.isAdmin || !HELP_DESK_STEPS.includes(t.type)).map((t) => h('button', { type: 'button', class: 'pal-item tone-' + t.tone, onclick: () => addNode(t.type) },
        h('strong', null, t.label), h('small', null, t.hint))),
      state.isAdmin ? null : h('p', { class: 'b-help' }, 'Help desk (Freshservice) steps are added by administrators.'),
      h('p', { class: 'b-help' }, 'Drag from a dot under a step to another step to connect them. Select a step or line and press Delete to remove it.'));
    svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('class', 'fl-edges');
    canvasInner = h('div', { class: 'fl-inner' });
    canvasInner.appendChild(svg);
    canvas = h('section', { class: 'bld-center fl-canvas', onpointerdown: (e) => {
      if (e.target === canvas || e.target === canvasInner || e.target === svg) {
        if (state.selNode || state.selEdge >= 0) { state.selNode = null; state.selEdge = -1; renderAll(); }
      }
    } }, canvasInner);
    const inspector = h('aside', { class: 'bld-inspector' });
    const scroll = { left: 0, top: 0 };
    const old = body.querySelector('.fl-canvas');
    if (old) { scroll.left = old.scrollLeft; scroll.top = old.scrollTop; }
    body.append(palette, canvas, inspector);
    renderNodes();
    if (!lastScroll.set) {
      // First visit: on a narrow screen, bring the Start step into view instead of an empty corner.
      const start = state.def.flow.nodes.find((n) => n.type === 'start') || state.def.flow.nodes[0];
      if (start) {
        lastScroll.left = Math.max(0, start.x - Math.max(12, (canvas.clientWidth - NODE_W) / 2));
        lastScroll.top = Math.max(0, start.y - 30);
      }
      lastScroll.set = true;
    }
    canvas.scrollLeft = lastScroll.left;
    canvas.scrollTop = lastScroll.top;
    canvas.addEventListener('scroll', () => { lastScroll.left = canvas.scrollLeft; lastScroll.top = canvas.scrollTop; });

    const node = nodeById(state.selNode);
    if (node) renderNodeInspector(inspector, node);
    else if (state.selEdge >= 0 && state.def.flow.edges[state.selEdge]) {
      const edge = state.def.flow.edges[state.selEdge];
      inspector.append(h('h3', null, 'Connection'),
        h('p', { class: 'muted' }, 'From “' + (nodeById(edge.from)?.label || edge.from) + '” to “' + (nodeById(edge.to)?.label || edge.to) + '”.'),
        h('button', { type: 'button', class: 'btn danger', onclick: () => removeEdge(state.selEdge) }, 'Delete connection'));
    } else {
      inspector.append(h('h3', null, 'Flow'),
        h('p', { class: 'muted' }, 'The flow runs from Start, following the lines. Select a step to edit it.'),
        h('ul', { class: 'b-tips' },
          h('li', null, h('strong', null, 'Decision'), ' sends the request down Yes or No.'),
          h('li', null, h('strong', null, 'Approval'), ' pauses until an approver decides.'),
          h('li', null, h('strong', null, 'Wait for ticket'), ' pauses until Freshservice reports a change (needs the webhook, see Settings in the admin menu).'),
          h('li', null, 'A branch with no line simply ends the request.')));
    }
  }
  const lastScroll = { left: 0, top: 0, set: false };

  function renderNodes() {
    nodeEls.clear();
    const bad = issueTargets('flow');
    for (const el of Array.from(canvasInner.querySelectorAll('.fl-node'))) el.remove();
    for (const node of state.def.flow.nodes) {
      const info = NODE_INFO[node.type] || { label: node.type, tone: 'slate' };
      const ports = portsOf(node.type);
      const el = h('div', {
        class: 'fl-node tone-' + info.tone + (node.id === state.selNode ? ' selected' : '') + (bad.has(node.id) ? ' invalid' : '') + (node.type === 'start' || node.type === 'end' ? ' terminal' : ''),
        dataset: { id: node.id }, tabindex: '0',
        onkeydown: (e) => { if (e.key === 'Enter' && e.target === el) { state.selNode = node.id; state.selEdge = -1; renderAll(); } },
      },
      h('div', { class: 'fl-node-head' }, h('span', { class: 'fl-kind' }, info.label), h('strong', null, node.label || info.label)),
      h('div', { class: 'fl-node-sum' }, nodeSummary(node)),
      ports.length ? h('div', { class: 'fl-ports' }, ports.map(([port, label]) =>
        h('div', { class: 'fl-port port-' + port, dataset: { port }, title: 'Drag to another step to connect' },
          label ? h('span', null, label) : null, h('i', null)))) : null);
      el.style.left = node.x + 'px';
      el.style.top = node.y + 'px';
      el.style.width = NODE_W + 'px';
      el.addEventListener('pointerdown', (e) => onNodePointerDown(e, node, el));
      canvasInner.appendChild(el);
      nodeEls.set(node.id, el);
    }
    resizeCanvas();
    drawEdges();
  }

  function resizeCanvas() {
    let w = 1400, hgt = 900;
    for (const n of state.def.flow.nodes) {
      w = Math.max(w, n.x + NODE_W + 400);
      hgt = Math.max(hgt, n.y + 500);
    }
    canvasInner.style.width = w + 'px';
    canvasInner.style.height = hgt + 'px';
    svg.setAttribute('width', w);
    svg.setAttribute('height', hgt);
  }

  function portPoint(node, port) {
    const el = nodeEls.get(node.id);
    const dot = el && el.querySelector('.fl-port[data-port="' + port + '"] i');
    if (!el) return { x: node.x + NODE_W / 2, y: node.y + 80 };
    if (!dot) return { x: node.x + NODE_W / 2, y: node.y + el.offsetHeight };
    const a = dot.getBoundingClientRect();
    const b = canvasInner.getBoundingClientRect();
    return { x: a.left - b.left + a.width / 2, y: a.top - b.top + a.height / 2 };
  }

  function curve(a, b) {
    const dy = Math.max(40, Math.abs(b.y - a.y) / 2);
    return 'M' + a.x + ',' + a.y + ' C' + a.x + ',' + (a.y + dy) + ' ' + b.x + ',' + (b.y - dy) + ' ' + b.x + ',' + b.y;
  }

  function svgEl(name, attrs) {
    const el = document.createElementNS(SVG_NS, name);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
    return el;
  }

  function drawEdges(temp) {
    while (svg.firstChild) svg.removeChild(svg.firstChild);
    const defs = svgEl('defs', {});
    for (const [id, cls] of [['arrow', 'fl-arrow'], ['arrow-sel', 'fl-arrow sel']]) {
      const marker = svgEl('marker', { id, viewBox: '0 0 10 10', refX: '9', refY: '5', markerWidth: '7', markerHeight: '7', orient: 'auto-start-reverse' });
      marker.appendChild(svgEl('path', { d: 'M0,0 L10,5 L0,10 z', class: cls }));
      defs.appendChild(marker);
    }
    svg.appendChild(defs);
    state.def.flow.edges.forEach((edge, index) => {
      const from = nodeById(edge.from), to = nodeById(edge.to);
      if (!from || !to) return;
      const a = portPoint(from, edge.port);
      const b = { x: to.x + NODE_W / 2, y: to.y - 2 };
      const d = curve(a, b);
      const selected = index === state.selEdge;
      svg.appendChild(svgEl('path', { d, class: 'fl-edge port-' + edge.port + (selected ? ' selected' : ''), 'marker-end': 'url(#' + (selected ? 'arrow-sel' : 'arrow') + ')' }));
      const hit = svgEl('path', { d, class: 'fl-edge-hit' });
      hit.addEventListener('pointerdown', (e) => { e.stopPropagation(); state.selEdge = index; state.selNode = null; renderAll(); });
      svg.appendChild(hit);
    });
    if (temp) svg.appendChild(svgEl('path', { d: curve(temp.a, temp.b), class: 'fl-edge temp' }));
  }

  function onNodePointerDown(e, node, el) {
    if (e.button !== undefined && e.button !== 0) return;
    const portEl = e.target.closest('.fl-port');
    e.stopPropagation();
    if (portEl) { startConnect(e, node, portEl.dataset.port); return; }

    const startX = e.clientX, startY = e.clientY, originX = node.x, originY = node.y;
    const before = JSON.stringify(state.def); // kept for Undo only if the step is actually moved
    let moved = false;
    el.setPointerCapture(e.pointerId);
    const move = (ev) => {
      const dx = ev.clientX - startX, dy = ev.clientY - startY;
      if (!moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      moved = true;
      node.x = Math.max(10, Math.round((originX + dx) / 10) * 10);
      node.y = Math.max(10, Math.round((originY + dy) / 10) * 10);
      el.style.left = node.x + 'px';
      el.style.top = node.y + 'px';
      drawEdges();
    };
    const up = () => {
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', up);
      el.removeEventListener('pointercancel', up);
      if (moved) { undoStack.push(before); if (undoStack.length > 30) undoStack.shift(); markDirty(); renderTop(); resizeCanvas(); drawEdges(); }
      if (state.selNode !== node.id || state.selEdge >= 0) { state.selNode = node.id; state.selEdge = -1; renderAll(); }
    };
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
  }

  function startConnect(e, node, port) {
    e.preventDefault();
    const a = portPoint(node, port);
    const origin = canvasInner.getBoundingClientRect();
    let target = null;
    const move = (ev) => {
      const b = { x: ev.clientX - origin.left, y: ev.clientY - origin.top };
      const under = document.elementFromPoint(ev.clientX, ev.clientY);
      const overEl = under && under.closest ? under.closest('.fl-node') : null;
      const over = overEl ? nodeById(overEl.dataset.id) : null;
      const valid = over && over.id !== node.id && over.type !== 'start';
      for (const el of nodeEls.values()) el.classList.remove('drop-ok');
      target = valid ? over : null;
      if (target) nodeEls.get(target.id).classList.add('drop-ok');
      drawEdges({ a, b: target ? { x: target.x + NODE_W / 2, y: target.y - 2 } : b });
    };
    const up = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      document.removeEventListener('pointercancel', up);
      if (target) {
        // One line per output: a new line replaces the old one.
        remember();
        state.def.flow.edges = state.def.flow.edges.filter((ed) => !(ed.from === node.id && ed.port === port));
        state.def.flow.edges.push({ from: node.id, port, to: target.id });
        markDirty();
      }
      renderAll();
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
    document.addEventListener('pointercancel', up);
  }

  /** Dropdown fed from Freshservice (groups, agents) with a fallback for typing an ID or {{formula}}. */
  function lookupSelect(source, value, onChange, noneLabel) {
    const wrap = h('div', { class: 'b-lookup' });
    const custom = h('input', { type: 'text', placeholder: 'ID or {{formula}}', value: value || '', hidden: true });
    const sel = h('select', null, h('option', { value: '' }, 'Loading…'));
    sel.disabled = true;
    custom.addEventListener('input', () => { onChange(custom.value); markDirty(); });
    sel.addEventListener('change', () => {
      if (sel.value === '__custom') { custom.hidden = false; custom.focus(); return; }
      custom.hidden = true;
      custom.value = sel.value;
      onChange(sel.value);
      markDirty();
    });
    const fill = (items) => {
      sel.textContent = '';
      sel.appendChild(h('option', { value: '' }, noneLabel));
      items.forEach((i) => sel.appendChild(h('option', { value: String(i.id) }, i.name)));
      sel.appendChild(h('option', { value: '__custom' }, 'Other: type an ID or formula…'));
      const current = value || '';
      if (current && !items.some((i) => String(i.id) === current)) { sel.value = '__custom'; custom.hidden = false; }
      else sel.value = current;
      sel.disabled = false;
    };
    window.SF.loadLookup(source).then(fill).catch(() => fill([]));
    wrap.append(sel, custom);
    return wrap;
  }

  /** Dropdown whose options arrive from the server ([[value, label], …]); keeps an unknown saved value visible. */
  function asyncSelect(loader, value, onChange, noneLabel) {
    const sel = h('select', { disabled: true }, h('option', { value: '' }, 'Loading…'));
    sel.addEventListener('change', () => { onChange(sel.value); markDirty(); });
    loader().then((options) => {
      sel.textContent = '';
      sel.appendChild(h('option', { value: '' }, noneLabel));
      options.forEach(([v, label]) => sel.appendChild(h('option', { value: String(v) }, label)));
      if (value && !options.some(([v]) => String(v) === String(value))) sel.appendChild(h('option', { value }, 'No longer available (#' + value + ')'));
      sel.value = value || '';
      sel.disabled = false;
    }).catch(() => {
      sel.textContent = '';
      sel.appendChild(h('option', { value: value || '' }, 'Could not load the list'));
    });
    return sel;
  }

  function customFieldsEditor(node) {
    const box = h('div', { class: 'b-cf' });
    let rows = Object.entries(node.customFields || {}).map(([name, value]) => ({ name, value }));
    const commit = () => {
      node.customFields = {};
      rows.forEach((r) => { if (r.name.trim()) node.customFields[r.name.trim()] = r.value; });
      markDirty();
    };
    const render = () => {
      box.textContent = '';
      rows.forEach((r, i) => {
        const name = h('input', { type: 'text', value: r.name, placeholder: 'Field name in Freshservice', class: 'mono', spellcheck: 'false' });
        name.addEventListener('input', () => { r.name = name.value; commit(); });
        box.appendChild(h('div', { class: 'b-cf-row' },
          h('div', { class: 'row-between' }, name,
            h('button', { type: 'button', class: 'icon-btn danger', 'aria-label': 'Remove', onclick: () => { rows.splice(i, 1); commit(); render(); } }, '×')),
          formulaInput(r.value, (v) => { r.value = v; commit(); }, { template: true, context: 'flow', rows: 1, placeholder: 'Value, e.g. {{total}}' })));
      });
      box.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => { rows.push({ name: '', value: '' }); render(); } }, '+ Map a custom field'));
    };
    render();
    return h('div', null, h('h4', { class: 'b-section' }, 'Custom ticket fields'),
      h('p', { class: 'b-help' }, 'Use the field\'s API name from Freshservice (Admin → Field Manager). A single {{formula}} keeps its type, so numbers stay numbers.'), box);
  }

  function renderNodeInspector(panel, node) {
    const info = NODE_INFO[node.type] || { label: node.type };
    const c = node.config;
    const refreshNode = () => {
      const el = nodeEls.get(node.id);
      if (!el) return;
      el.querySelector('.fl-node-head strong').textContent = node.label || info.label;
      el.querySelector('.fl-node-sum').textContent = nodeSummary(node);
      drawEdges();
    };
    const set = (key) => (v) => { if (v === '' || v === null) delete c[key]; else c[key] = v; refreshNode(); };
    const tpl = (key, rows, placeholder) => formulaInput(c[key], set(key), { template: true, context: 'flow', rows: rows || 1, placeholder });
    const fx = (key, placeholder) => formulaInput(c[key], set(key), { context: 'flow', placeholder });
    const PRIORITY = [['1', 'Low'], ['2', 'Medium'], ['3', 'High'], ['4', 'Urgent']];
    const STATUS = [['2', 'Open'], ['3', 'Pending'], ['4', 'Resolved'], ['5', 'Closed']];
    /** The four standard statuses, or the number of a status you added in Freshservice (e.g. "Travel Pending"). */
    const statusControl = (c, allowBlank) => {
      const standard = STATUS.map((s) => s[0]);
      const isCustom = () => !!c.status && !standard.includes(String(c.status));
      const number = h('input', { type: 'number', min: 6, max: 999, step: 1, placeholder: 'Status number from Freshservice', value: isCustom() ? c.status : '', hidden: !isCustom(), 'aria-label': 'Custom status number' });
      number.addEventListener('input', () => { c.status = number.value.trim(); markDirty(); });
      const options = (allowBlank ? [['', 'Don\'t change']] : []).concat(STATUS, [['custom', 'A status you added in Freshservice…']]);
      const choice = select(isCustom() ? 'custom' : (c.status || (allowBlank ? '' : '2')), options, (v) => {
        number.hidden = v !== 'custom';
        c.status = v === 'custom' ? number.value.trim() : v;
        markDirty();
      });
      return h('div', { class: 'b-status' }, choice, number,
        h('p', { class: 'b-help' }, 'For a custom status, enter its number. Freshservice shows it in Admin → Field Manager → Ticket Fields → Status, or in ticket.status of a webhook event.'));
    };
    // Reminder and escalation settings shared by the two approval steps.
    const people = (key, placeholder) => {
      const input = tpl(key, 1, placeholder);
      // Suggest staff addresses as the builder types (the last address in the list is completed).
      const box = input.matches && input.matches('input') ? input : input.querySelector && input.querySelector('input');
      if (box) { box.setAttribute('data-people', 'multi'); box.setAttribute('autocomplete', 'off'); }
      if (!state.teams.length) return input;
      const pick = select('', [['', 'Add a team…']].concat(state.teams.map((t) => [t, t])), (v) => {
        if (!v) return;
        const current = (c[key] || '').trim();
        set(key)(current ? current + ', [' + v + ']' : '[' + v + ']');
        renderAll();
      });
      pick.setAttribute('aria-label', 'Add a team');
      return h('div', { class: 'b-people' }, input, pick);
    };
    const followUp = () => [
      h('h4', { class: 'b-section' }, 'If nobody decides'),
      h('div', { class: 'b-pair' },
        row('Remind every (school days)', textInput(c.remindDays, set('remindDays'), { inputmode: 'numeric', placeholder: '2' }), '0 for no reminders. Up to 5 are sent.'),
        row('Escalate after (school days)', textInput(c.escalateDays, set('escalateDays'), { inputmode: 'numeric', placeholder: 'never' }))),
      row('Escalate to', tpl('escalateTo', 1, 'e.g. principal@school.org'), 'After that long with no decision, these people can also decide the step. Leave both blank for no escalation.'),
      h('h4', { class: 'b-section' }, 'Due date'),
      row('Due by (formula giving a date)', fx('dueDate', "e.g. DATEADD(to_date, 21) — 3 weeks after the return date"),
        'Optional. Worked out when the step is reached. The person gets a heads-up 3 days before, and an overdue notice on the day if it is still not done.'),
      row('If overdue, also tell', people('dueNotify', 'e.g. a team or office@school.org'), 'They are told once, on the due date. Leave blank to remind only the person who owes it.'),
    ];
    /** A "who" setting: emails, {{formulas}}, or a team picked from the list (written as [Team name]). */
    const WHO_HELP = 'Emails separated by commas, or a team from Admin → Teams (written as [Team name]). Any one of them can act.';
    const ticketRow = () => row('Which ticket', tpl('ticketId', 1, '{{ticket.id}}'), 'Leave blank to use the ticket this request created or was started by.');

    panel.append(
      h('div', { class: 'row-between' }, h('h3', null, info.label),
        node.type !== 'start' ? h('div', { class: 'row' },
          h('button', { type: 'button', class: 'btn small', onclick: () => duplicateNode(node.id) }, 'Duplicate step'),
          h('button', { type: 'button', class: 'btn small ghost-danger', onclick: () => removeNode(node.id) }, 'Delete step')) : null),
      row('Name', textInput(node.label, (v) => { node.label = v; refreshNode(); })));

    switch (node.type) {
      case 'start':
        panel.append(h('p', { class: 'muted' }, isFormTrigger()
          ? 'The flow begins when someone submits the form. Every field is available to the steps below by its key.'
          : 'The flow begins when Freshservice sends a ticket event that passes the start condition (see the Settings tab). The ticket is available as ticket.id, ticket.status and so on.'));
        break;
      case 'condition':
        panel.append(row('Condition', fx('expression', 'e.g. total >= 500'), 'True follows the Yes line; false follows the No line.'));
        break;
      case 'approval':
        panel.append(
          row('Approvers', people('approvers', 'e.g. {{approver_email}} or principal@school.org'), 'Emails separated by commas, or a team from Admin → Teams (written as [Team name]).'),
          row('If there is more than one', select(c.rule || 'any', [['any', 'Any one of them can decide'], ['all', 'All of them must approve']], (v) => { set('rule')(v === 'any' ? '' : v); })),
          row('Instructions for the approver', tpl('instructions', 4, 'What should they check? e.g. Total: {{TEXT(total, "C2")}}')),
          h('p', { class: 'b-help' }, 'For several approvers in sequence, or a department\'s final approver, use an “Approval chain” step instead.'),
          followUp());
        break;
      case 'approval_chain': {
        const fieldsOf = (type) => state.def.form.fields.filter((f) => f.type === type).map((f) => [f.key, f.label || f.key]);
        const chainFields = fieldsOf('approval_chain');
        const departmentFields = fieldsOf('department');
        panel.append(row('First, these approvers', select(c.chainSource || '', [
          ['', 'None — only the department\'s final approver'],
          ['field', 'The chain the requester builds on the form'],
          ['template', 'A saved chain template (same for everyone)'],
        ], (v) => { set('chainSource')(v); renderAll(); })));
        if (c.chainSource === 'field')
          panel.append(row('Form field', chainFields.length
            ? select(c.chainField || '', [['', 'Choose a field…']].concat(chainFields), set('chainField'))
            : h('p', { class: 'b-fx-status bad' }, 'The form has no Approval chain field yet. Add one on the Form tab.')));
        if (c.chainSource === 'template')
          panel.append(row('Chain template', asyncSelect(() => api('GET', '/api/chains').then((d) => d.shared.map((ch) => [ch.id, ch.name])), c.chainId, set('chainId'), 'Choose a template…'),
            'Shared templates are managed under Approval chains in the menu.'));
        panel.append(row('Then, the department\'s final approver', select(c.departmentSource || '', [
          ['', 'None'],
          ['field', 'For the department chosen on the form'],
          ['fixed', 'Always the same department'],
        ], (v) => { set('departmentSource')(v); renderAll(); }), 'Admins set each department\'s final approver. Requesters cannot change or skip it.'));
        if (c.departmentSource === 'field')
          panel.append(row('Form field', departmentFields.length
            ? select(c.departmentField || '', [['', 'Choose a field…']].concat(departmentFields), set('departmentField'))
            : h('p', { class: 'b-fx-status bad' }, 'The form has no Department field yet. Add one on the Form tab.')));
        if (c.departmentSource === 'fixed')
          panel.append(row('Department', asyncSelect(() => api('GET', '/api/departments').then((d) => d.items.map((dep) => [dep.id, dep.name])), c.departmentId, set('departmentId'), 'Choose a department…')));
        panel.append(row('Instructions for the approvers', tpl('instructions', 4, 'Shown at every step. e.g. Trip to {{destination}} on {{TEXT(trip_date, "MMM d")}}')),
          h('p', { class: 'b-help' }, 'Each step waits for its approver(s) before the next begins. A rejection at any step follows the Rejected line. Approvers can also send a request back to the requester for changes; it returns to the same step when resubmitted.'),
          followUp());
        break;
      }
      case 'task': {
        node.fields = node.fields || [];
        const taken = (except) => {
          const keys = new Set(state.def.form.fields.map((f) => f.key));
          state.def.flow.nodes.forEach((n) => (n.fields || []).forEach((f) => { if (f !== except) keys.add(f.key); }));
          return [...keys];
        };
        const list = h('div');
        const renderFields = () => {
          list.textContent = '';
          node.fields.forEach((f, i) => {
            const keyInput = textInput(f.key, (v) => { f.key = v.trim(); autoKeys.delete(f); refreshNode(); }, { spellcheck: 'false', class: 'mono' });
            const box = h('div', { class: 'b-task-field' },
              h('div', { class: 'row-between' }, h('span', { class: 'b-label' }, 'Field ' + (i + 1)),
                h('button', { type: 'button', class: 'icon-btn danger', 'aria-label': 'Remove field ' + (i + 1), onclick: () => { node.fields.splice(i, 1); markDirty(); renderFields(); refreshNode(); } }, '×')),
              row('Label', textInput(f.label, (v) => {
                f.label = v;
                if (autoKeys.has(f)) { f.key = uniqueKey(slug(v), taken(f)); keyInput.value = f.key; }
                refreshNode();
              })),
              row('Key', keyInput, 'The name later steps and messages use, e.g. {{' + (f.key || 'key') + '}}.'),
              row('Type', select(f.type, TASK_FIELD_TYPES, (v) => {
                f.type = v;
                if (['select', 'radio', 'multiselect'].includes(v)) f.options = f.options || ['Option 1', 'Option 2']; else delete f.options;
                if (!['number', 'currency'].includes(v)) { delete f.min; delete f.max; }
                if (v !== 'attachments') delete f.maxFiles;
                if (v !== 'calculated') delete f.formula;
                renderFields();
              })));
            if (['number', 'currency'].includes(f.type))
              box.append(h('div', { class: 'b-pair' },
                row('Minimum', numberInput(f.min, (v) => { f.min = v; }, { step: 'any' })),
                row('Maximum', numberInput(f.max, (v) => { f.max = v; }, { step: 'any' }))));
            if (f.type === 'attachments')
              box.append(row('Most files allowed', numberInput(f.maxFiles, (v) => { f.maxFiles = v === null ? null : Math.max(1, Math.min(20, Math.round(v))); }, { min: 1, max: 20, step: 1, placeholder: '5' })));
            if (['select', 'radio', 'multiselect'].includes(f.type))
              box.append(row('Options', textArea((f.options || []).join('\n'), (v) => { f.options = v.split('\n').map((o) => o.trim()).filter(Boolean); }, 3), 'One option per line.'));
            if (f.type === 'section') { list.appendChild(box); return; }
            if (f.type === 'calculated')
              box.append(row('Formula', formulaInput(f.formula, (v) => { f.formula = v; }, { context: 'flow', placeholder: 'e.g. total - actual_total' })));
            else
              box.append(
                toggle('Required', f.required, (v) => { f.required = v; }),
                row('Required when', formulaInput(f.requiredWhen, (v) => { f.requiredWhen = clean(v); }, { context: 'flow', placeholder: 'e.g. total >= 150' })));
            box.append(row('Show only when', formulaInput(f.visibleWhen, (v) => { f.visibleWhen = clean(v); }, { context: 'flow', placeholder: 'Always shown' })));
            if (!['calculated', 'attachments', 'checkbox'].includes(f.type))
              box.append(row('Starts as (formula)', formulaInput(f.default, (v) => { f.default = clean(v); }, { context: 'flow', placeholder: 'e.g. budget_account' }), 'Filled in when the task opens. Can use the request\'s answers.'));
            box.append(row('Help text', textInput(f.help, (v) => { f.help = clean(v); })));
            list.appendChild(box);
          });
          list.appendChild(h('button', { type: 'button', class: 'btn small', onclick: () => {
            const field = { key: uniqueKey('answer', taken()), label: 'New field', type: 'text' };
            autoKeys.add(field);
            node.fields.push(field);
            markDirty();
            renderFields();
            refreshNode();
          } }, '+ Add a field'));
        };
        renderFields();
        panel.append(
          row('Who does this', people('assignees', 'e.g. {{submitter.email}} or a team'), WHO_HELP),
          row('Instructions', tpl('instructions', 4, 'What should they do? e.g. Record the purchase order number.')),
          h('h4', { class: 'b-section' }, 'What they fill in'),
          h('p', { class: 'b-help' }, 'The request waits here until one of these people completes the task. What they enter is added to the request and can be used by later steps. With no fields, the task is simply marked as done.'),
          list,
          followUp());
        break;
      }
      case 'notify':
        panel.append(
          row('Send to', people('to', 'e.g. {{submitter.email}}, office@school.org'), 'Emails separated by commas, or a team. Staff also see it in their Notifications; outside addresses only get the email.'),
          row('Subject', tpl('subject', 1, 'e.g. Your trip to {{destination}} is approved')),
          row('Message', tpl('message', 5, 'Plain text. Use {{field}} placeholders.')),
          h('p', { class: 'b-help' }, 'Approvers and requesters are notified automatically (approval needed, completed, not approved). Use this step for anything extra.'));
        break;
      case 'set_variable':
        panel.append(
          row('Value name', textInput(c.name, set('name'), { class: 'mono', spellcheck: 'false', placeholder: 'e.g. priority_level' }), 'Later steps can use this name in formulas.'),
          row('Formula', fx('expression', 'e.g. IF(total > 1000, 3, 2)')));
        break;
      case 'fs_create_ticket':
        panel.append(
          row('Subject', tpl('subject', 1, 'e.g. Purchase request: {{title}}')),
          row('Description', tpl('description', 6, 'Plain text. Use {{field}} placeholders.')),
          row('Requester email', tpl('requesterEmail', 1, '{{submitter.email}}'), 'Leave blank for the person who submitted the form.'),
          h('div', { class: 'b-pair' },
            row('Priority', select(c.priority || '2', PRIORITY, set('priority'))),
            row('Status', statusControl(c, false))),
          row('Priority formula (optional)', fx('priorityFormula', 'e.g. IF(total > 1000, 3, 2)'), 'Overrides Priority. Must give 1 (Low) to 4 (Urgent).'),
          row('Assign to group', lookupSelect('groups', c.groupId, set('groupId'), 'No group')),
          row('Department', tpl('departmentId', 1, 'e.g. {{department}}'), 'A Department lookup field, or a department ID.'),
          h('div', { class: 'b-pair' },
            row('Category', tpl('category', 1)),
            row('Sub-category', tpl('subCategory', 1))),
          row('Tags', tpl('tags', 1, 'comma, separated')),
          toggle('Attach the request\'s files to the ticket', c.attachFiles === 'true', (v) => { set('attachFiles')(v ? 'true' : ''); }),
          h('p', { class: 'b-help' }, 'Sends everything uploaded through Attachments fields. If Freshservice refuses a file the ticket is still created, and the request\'s timeline says which files were left out.'),
          customFieldsEditor(node));
        break;
      case 'fs_service_request':
        panel.append(
          row('Catalog item ID', tpl('itemId', 1, 'e.g. 42'), 'The number at the end of the item\'s URL in the service catalog.'),
          row('Requester email', tpl('requesterEmail', 1, '{{submitter.email}}')),
          row('Quantity', tpl('quantity', 1, '1')),
          customFieldsEditor(node));
        break;
      case 'fs_update_ticket':
        panel.append(ticketRow(),
          h('div', { class: 'b-pair' },
            row('Status', statusControl(c, true)),
            row('Priority', select(c.priority || '', [['', 'Don\'t change']].concat(PRIORITY), set('priority')))),
          row('Priority formula (optional)', fx('priorityFormula')),
          row('Move to group', lookupSelect('groups', c.groupId, set('groupId'), 'Don\'t change')),
          row('Assign to agent', lookupSelect('agents', c.responderId, set('responderId'), 'Don\'t change')),
          customFieldsEditor(node));
        break;
      case 'fs_add_note':
        panel.append(ticketRow(),
          row('Note', tpl('body', 5, 'e.g. Approved by {{approval.by}}')),
          row('Who can see it', select(c.visibility || 'private', [['private', 'Private (agents only)'], ['public', 'Public (requester can see it)']], set('visibility'))));
        break;
      case 'fs_lookup':
        panel.append(
          row('Look up', select(c.kind || 'requester', [['requester', 'A person, by email'], ['ticket', 'A ticket, by number']], (v) => { set('kind')(v); renderAll(); })),
          row(c.kind === 'ticket' ? 'Ticket number' : 'Email', tpl('value', 1, c.kind === 'ticket' ? '{{ticket.id}}' : '{{submitter.email}}')),
          row('Save as', textInput(c.saveAs, set('saveAs'), { class: 'mono', spellcheck: 'false' }),
            'Later steps read it like ' + (c.saveAs || 'person') + (c.kind === 'ticket' ? '.status' : '.name') + '. Blank if nothing is found.'));
        break;
      case 'wait_ticket':
        panel.append(ticketRow(),
          row('Continue when', fx('until', 'e.g. IN(ticket.status, 4, 5)'), 'Checked each time Freshservice reports a change to the ticket. Leave blank to continue on any change.'),
          h('p', { class: 'b-help' }, 'Needs the Freshservice webhook to be set up (Admin → Settings).'));
        break;
      case 'end':
        panel.append(row('Finish as', select(c.outcome || 'completed', [['completed', 'Completed'], ['rejected', 'Not approved']], (v) => { set('outcome')(v === 'completed' ? '' : v); })));
        break;
    }
  }

  // ---- preview and settings ----------------------------------------------------------

  function renderPreviewTab() {
    const holder = h('div', { class: 'form-shell tone-' + state.meta.color });
    body.append(h('section', { class: 'bld-preview' },
      h('p', { class: 'muted' }, 'This is the form as people will see it, using your unsaved changes. Field logic is live; submitting only checks validation.'),
      holder));
    const mount = h('div');
    holder.append(h('header', { class: 'form-head' }, h('div', null, h('h1', null, state.meta.name || 'Untitled workflow'),
      state.meta.description ? h('p', { class: 'sub' }, state.meta.description) : null)), mount);
    const definition = JSON.parse(JSON.stringify(state.def.form));
    const run = (values, init) => api('POST', '/api/forms/evaluate', { definition, values, init });
    preview = window.SF.mountForm(mount, {
      definition, preview: true,
      evaluate: run,
      submit: async (values) => {
        const result = await run(values, false);
        if (result.valid) toast('The form is valid. In the live form this would now start the flow.');
        return { ok: result.valid, state: result, runId: null };
      },
    });
  }

  /** "Everyone" or a set of registered Entra groups. Applies as soon as the workflow is saved, without publishing. */
  function audienceEditor() {
    const box = h('div', { class: 'b-audience' });
    const groups = state.groups || [];
    const limited = state.meta.audienceGroups.length > 0;
    const choice = (value, title, text) => {
      const radio = h('input', { type: 'radio', name: 'audience', checked: limited === value });
      radio.addEventListener('change', () => {
        state.meta.audienceGroups = value && groups.length ? [groups[0].id] : [];
        markDirty();
        renderAll();
      });
      radio.disabled = value && groups.length === 0;
      return h('label', { class: 'choice' }, radio, h('span', null, h('strong', null, title), h('small', null, text)));
    };
    box.append(
      choice(false, 'Everyone who can sign in', 'The form appears on the home page for all staff.'),
      choice(true, 'Only members of these groups', groups.length
        ? 'People outside the chosen groups do not see the form at all.'
        : 'No Entra groups have been registered yet. An admin adds them under Admin → Settings.'));
    if (limited) {
      const list = h('div', { class: 'b-checks' });
      for (const group of groups) {
        const check = h('input', { type: 'checkbox', checked: state.meta.audienceGroups.includes(group.id) });
        check.addEventListener('change', () => {
          const chosen = new Set(state.meta.audienceGroups);
          if (check.checked) chosen.add(group.id); else chosen.delete(group.id);
          // Unticking the last group would silently mean "everyone"; keep at least one ticked.
          if (chosen.size === 0) { check.checked = true; toast('Choose at least one group, or pick “Everyone who can sign in”.', 'bad'); return; }
          state.meta.audienceGroups = Array.from(chosen);
          markDirty();
        });
        list.appendChild(h('label', { class: 'b-toggle' }, check, h('span', null, group.name)));
      }
      const stale = state.meta.audienceGroups.filter((id) => !groups.some((g) => g.id === id));
      if (stale.length) list.appendChild(h('p', { class: 'b-fx-status bad' }, 'This workflow refers to a group that is no longer registered. Tick the groups you want and save.'));
      box.append(list, h('p', { class: 'b-help' }, 'This takes effect as soon as you save — it does not wait for Publish. Owners and admins can always open the form to test it.'));
    }
    return box;
  }

  function renderSettingsTab() {
    if (!state.canEditForm) {
      body.append(h('section', { class: 'bld-settings panel' },
        h('h3', null, 'About this workflow'),
        h('p', null, h('strong', null, state.meta.name)),
        state.meta.description ? h('p', { class: 'muted' }, state.meta.description) : null,
        h('p', { class: 'b-help' }, 'You are an owner of this workflow: you can change its steps on the Flow tab and publish them. The form — its questions, name, who can submit it and who looks after it — is changed by an administrator.'),
        h('h3', null, 'Owners'),
        h('p', null, (state.meta.owners || []).join(', ') || '—')));
      body.append(versionsPanel());
      return;
    }
    const trigger = state.def.trigger;
    const swatches = h('div', { class: 'swatches' }, COLORS.map((color) =>
      h('button', { type: 'button', class: 'swatch tone-' + color + (state.meta.color === color ? ' on' : ''), title: color, 'aria-label': color,
        onclick: () => { state.meta.color = color; markDirty(); renderAll(); } })));
    const triggerChoice = (value, title, text) => {
      const radio = h('input', { type: 'radio', name: 'trigger', checked: trigger.type === value });
      radio.addEventListener('change', () => {
        trigger.type = value;
        const start = state.def.flow.nodes.find((n) => n.type === 'start');
        if (start && ['Form submitted', 'Ticket event received'].includes(start.label)) start.label = value === 'ticket' ? 'Ticket event received' : 'Form submitted';
        markDirty();
        renderAll();
      });
      return h('label', { class: 'choice' }, radio, h('span', null, h('strong', null, title), h('small', null, text)));
    };
    const panel = h('section', { class: 'bld-settings panel' },
      h('h3', null, 'About this workflow'),
      row('Name', textInput(state.meta.name, (v) => { state.meta.name = v; renderTop(); }, { maxlength: 200 })),
      row('Description', textArea(state.meta.description, (v) => { state.meta.description = v; }, 2), 'Shown on the home page card.'),
      row('Category', textInput(state.meta.category, (v) => { state.meta.category = v; }, { maxlength: 100, placeholder: 'e.g. Help Desk, Purchasing' }), 'Forms are grouped by category on the home page.'),
      row('Colour', swatches),
      h('h3', null, 'Who looks after it'),
      row('Owners', textArea(state.meta.owners.join('\n'), (v) => { state.meta.owners = v.split(/[\s,;]+/).map((o) => o.trim()).filter(Boolean); }, 3),
        'One email per line. Owners (and admins) can edit this workflow, and see, retry and cancel its requests. Other builders cannot.'),
      trigger.type === 'ticket' ? null : h('h3', null, 'Who can submit it'),
      trigger.type === 'ticket' ? null : audienceEditor(),
      h('h3', null, 'How it starts'),
      triggerChoice('form', 'Someone fills in a form', 'Appears on the home page for everyone who signs in.'),
      state.isAdmin || trigger.type === 'ticket' ? triggerChoice('ticket', 'A Freshservice ticket event', 'Starts when Freshservice sends a ticket to the SchoolFlow webhook. Runs once per ticket.') : null);
    if (trigger.type === 'ticket') {
      panel.append(row('Only start when', formulaInput(trigger.condition, (v) => { trigger.condition = clean(v); }, { context: 'flow', placeholder: 'e.g. ticket.priority >= 3' }),
        'Leave blank to start for every ticket event. Uses the ticket\'s API values: status 2 Open … 5 Closed, priority 1 Low … 4 Urgent.'));
      if (state.def.form.fields.length) panel.append(h('p', { class: 'flash warn' }, 'This workflow has form fields from when it was form-based. They are ignored while it starts from a ticket event.'));
    }
    body.append(panel);
    body.append(versionsPanel());
  }

  function versionsPanel() {
    const list = h('div', { class: 'muted' }, 'Loading…');
    const panel = h('section', { class: 'bld-settings panel' },
      h('h3', null, 'Version history'),
      h('p', { class: 'b-help' }, state.canEditForm
        ? 'Every time you publish, that version is kept. Restoring one puts it back as the draft — staff see no change until you publish it.'
        : 'Every time the workflow is published, that version is kept. Restoring one puts its steps back into the draft (the form is not changed) — staff see no change until you publish it.'),
      list,
      h('h3', null, 'Move or back up'),
      h('p', { class: 'b-help' }, 'Export downloads this workflow (the current saved draft) as a file. Import it from the Workflows page, here or on another SchoolFlow site.'),
      h('a', { class: 'btn small', href: '/Builder?handler=Export&id=' + workflowId, download: '' }, 'Export this workflow'));
    api('GET', '/api/builder/' + workflowId + '/versions').then((data) => {
      list.textContent = '';
      list.className = '';
      if (!data.versions.length) { list.appendChild(h('p', { class: 'muted' }, state.isPublished ? 'History starts with the next version you publish.' : 'Nothing has been published yet.')); return; }
      list.appendChild(h('ul', { class: 'version-list' }, data.versions.map((v) => h('li', null,
        h('span', null, h('strong', null, 'Version ' + v.version), ' ', v.isLive ? h('span', { class: 'pill ok tiny' }, 'Live') : null,
          h('div', { class: 'muted small' }, v.publishedAt + (v.publishedBy ? ' · ' + v.publishedBy : ''))),
        v.sameAsDraft ? h('span', { class: 'muted small' }, 'Same as the draft')
          : h('button', { type: 'button', class: 'btn small', onclick: async () => {
            if (!window.confirm('Replace the current draft with version ' + v.version + '?' + (state.dirty ? ' Your unsaved changes will be lost.' : ' The draft as it is now will be lost unless it was published.'))) return;
            try {
              await api('POST', '/api/builder/' + workflowId + '/versions/' + v.version + '/restore');
              state.dirty = false;
              window.location.reload();
            } catch (e) { window.alert(e.message); }
          } }, 'Restore as draft')))));
    }).catch((e) => { list.textContent = e.message; });
    return panel;
  }

  root.textContent = '';
  root.append(top, issuesBox, body);
  load();
})();
