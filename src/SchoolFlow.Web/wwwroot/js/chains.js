// Approval chains page: shared templates (managed by builders and admins) and each person's own chains.
(function () {
  'use strict';
  const { h, api, toast, chainEditor, describeChain } = window.SF;
  const root = document.getElementById('chains-root');
  const actions = document.getElementById('chain-actions');
  if (!root) return;

  function card(chain) {
    return h('article', { class: 'panel chain-card' },
      h('div', { class: 'row-between' },
        h('div', null, h('strong', null, chain.name), chain.description ? h('div', { class: 'muted small' }, chain.description) : null),
        chain.canEdit ? h('span', { class: 'row' },
          h('button', { type: 'button', class: 'btn small', onclick: () => edit(chain, chain.shared) }, 'Edit'),
          h('button', { type: 'button', class: 'btn small ghost-danger', onclick: () => remove(chain) }, 'Delete')) : null),
      h('ol', { class: 'chain-steps' }, chain.steps.map((s) => h('li', null, h('strong', null, s.name), ' ',
        h('span', { class: 'muted' }, s.approvers.join(', ') + (s.rule === 'all' && s.approvers.length > 1 ? ' — all must approve' : ''))))));
  }

  async function load() {
    try {
      const data = await api('GET', '/api/chains');
      actions.textContent = '';
      actions.append(h('button', { type: 'button', class: 'btn primary', onclick: () => edit(null, false) }, '+ New chain of my own'),
        data.canManageShared ? h('button', { type: 'button', class: 'btn', onclick: () => edit(null, true) }, '+ New shared template') : null);
      root.textContent = '';
      root.append(
        h('h2', { class: 'section-title' }, 'Shared templates'),
        data.shared.length ? h('div', { class: 'stack' }, data.shared.map(card))
          : h('div', { class: 'empty small' }, h('p', null, data.canManageShared ? 'No shared templates yet. Create one for everyone to use.' : 'No shared templates yet.')),
        h('h2', { class: 'section-title' }, 'My chains'),
        data.mine.length ? h('div', { class: 'stack' }, data.mine.map(card))
          : h('div', { class: 'empty small' }, h('p', null, 'You have not saved any chains. Create one here, or save one while filling in a request.')));
    } catch (e) {
      root.textContent = '';
      root.appendChild(h('div', { class: 'flash bad' }, e.message));
    }
  }

  function edit(chain, shared) {
    const name = h('input', { type: 'text', maxlength: 200, value: chain ? chain.name : '', placeholder: 'e.g. Field trip sign-off' });
    const description = h('input', { type: 'text', maxlength: 500, value: chain ? chain.description : '', placeholder: 'Optional: when to use it' });
    const holder = h('div');
    const error = h('div', { class: 'sf-error', hidden: true });
    const editor = chainEditor(holder, { steps: chain ? chain.steps : [{ name: '', approvers: [], rule: 'any' }] });
    const save = h('button', { type: 'button', class: 'btn primary' }, 'Save');
    const dialog = h('dialog', { class: 'dialog picker' },
      h('h2', null, (chain ? 'Edit ' : 'New ') + (shared ? 'shared template' : 'chain')),
      h('label', { class: 'lbl' }, 'Name'), name,
      h('label', { class: 'lbl' }, 'Description'), description,
      h('label', { class: 'lbl' }, 'Steps, in order'), holder, error,
      h('div', { class: 'actions end' }, h('button', { type: 'button', class: 'btn', onclick: () => dialog.close() }, 'Cancel'), save));
    save.addEventListener('click', async () => {
      save.disabled = true;
      try {
        const body = { name: name.value, description: description.value, shared, steps: editor.get() };
        if (chain) await api('PUT', '/api/chains/' + chain.id, body); else await api('POST', '/api/chains', body);
        dialog.close();
        toast('Saved.');
        load();
      } catch (e) {
        error.textContent = e.message;
        error.hidden = false;
      } finally {
        save.disabled = false;
      }
    });
    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();
    name.focus();
  }

  async function remove(chain) {
    if (!window.confirm('Delete “' + chain.name + '”? Requests already submitted keep the approvers they started with.')) return;
    try {
      await api('DELETE', '/api/chains/' + chain.id);
      toast('Deleted.');
      load();
    } catch (e) {
      toast(e.message, 'bad');
    }
  }

  load();
})();
