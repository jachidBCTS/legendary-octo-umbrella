// Editor for an approval chain: an ordered list of steps, each with a name, one or more
// approver emails and a rule (any one approves / all must approve). Used on the Approval
// chains page and inside "Approval chain" form fields.
(function () {
  'use strict';
  const { h } = window.SF;

  const splitEmails = (text) => String(text || '').split(/[\s,;]+/).map((e) => e.trim()).filter(Boolean);

  /**
   * @param container element to render into
   * @param options   { steps: [{name, approvers: [], rule}], onChange(steps), maxSteps }
   * @returns { get(), set(steps) }
   */
  function chainEditor(container, options) {
    const maxSteps = options.maxSteps || 10;
    let steps = [];
    const list = h('div', { class: 'ce-steps' });
    const addButton = h('button', { type: 'button', class: 'btn small', onclick: () => { steps.push({ name: '', approvers: [], rule: 'any' }); render(); changed(); focusLast(); } }, '+ Add a step');
    container.textContent = '';
    container.append(list, addButton);

    const clean = () => steps
      .map((s) => ({ name: s.name.trim(), approvers: s.approvers.slice(), rule: s.rule === 'all' ? 'all' : 'any' }))
      .filter((s) => s.name || s.approvers.length);
    const changed = () => { if (options.onChange) options.onChange(clean()); };
    const focusLast = () => { const inputs = list.querySelectorAll('.ce-step:last-child input'); if (inputs[0]) inputs[0].focus(); };

    function render() {
      list.textContent = '';
      if (steps.length === 0) list.appendChild(h('p', { class: 'muted small' }, 'No steps yet.'));
      steps.forEach((step, index) => {
        const name = h('input', { type: 'text', value: step.name, placeholder: 'Step name, e.g. Team leader', maxlength: 100, 'aria-label': 'Step ' + (index + 1) + ' name' });
        name.addEventListener('input', () => { step.name = name.value; changed(); });
        const approvers = h('input', { type: 'text', value: step.approvers.join(', '), placeholder: 'Approver email(s), separated by commas', 'aria-label': 'Step ' + (index + 1) + ' approvers', autocomplete: 'off', spellcheck: 'false' });
        const rule = h('select', { 'aria-label': 'Step ' + (index + 1) + ' rule' }, h('option', { value: 'any' }, 'Any one can approve'), h('option', { value: 'all' }, 'All must approve'));
        rule.value = step.rule === 'all' ? 'all' : 'any';
        const showRule = () => { rule.hidden = step.approvers.length < 2; };
        approvers.addEventListener('input', () => { step.approvers = splitEmails(approvers.value); showRule(); changed(); });
        rule.addEventListener('change', () => { step.rule = rule.value; changed(); });
        showRule();
        const move = (delta) => { steps.splice(index + delta, 0, steps.splice(index, 1)[0]); render(); changed(); };
        list.appendChild(h('div', { class: 'ce-step' },
          h('span', { class: 'ce-num' }, String(index + 1)),
          h('div', { class: 'ce-fields' }, name, approvers, rule),
          h('div', { class: 'ce-tools' },
            h('button', { type: 'button', class: 'icon-btn', title: 'Move earlier', 'aria-label': 'Move earlier', disabled: index === 0, onclick: () => move(-1) }, '↑'),
            h('button', { type: 'button', class: 'icon-btn', title: 'Move later', 'aria-label': 'Move later', disabled: index === steps.length - 1, onclick: () => move(1) }, '↓'),
            h('button', { type: 'button', class: 'icon-btn danger', title: 'Remove step', 'aria-label': 'Remove step', onclick: () => { steps.splice(index, 1); render(); changed(); } }, '×'))));
      });
      addButton.disabled = steps.length >= maxSteps;
    }

    function set(value) {
      steps = (Array.isArray(value) ? value : []).slice(0, maxSteps).map((s) => ({
        name: String((s && s.name) || ''),
        approvers: Array.isArray(s && s.approvers) ? s.approvers.map(String) : splitEmails(s && s.approvers),
        rule: s && s.rule === 'all' ? 'all' : 'any',
      }));
      render();
    }

    set(options.steps);
    return { get: clean, set };
  }

  /** "Team leader: a@x.org → Principal: b@x.org, c@x.org (all)" */
  function describeChain(steps) {
    return (steps || []).map((s) => s.name + ': ' + (s.approvers || []).join(', ') + (s.rule === 'all' && (s.approvers || []).length > 1 ? ' (all)' : '')).join('  →  ');
  }

  window.SF.chainEditor = chainEditor;
  window.SF.describeChain = describeChain;
})();
