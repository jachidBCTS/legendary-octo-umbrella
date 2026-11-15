// Page script for /Start/{id}: mounts the live form for a published workflow and, when the
// form allows it, lets the person prefill it from a previous request.
(function () {
  'use strict';
  const { h, api, debounce, mountForm } = window.SF;
  const root = document.getElementById('form-root');
  if (!root) return;
  const workflowId = Number(root.dataset.workflowId);
  const definition = JSON.parse(document.getElementById('form-definition').textContent);
  const copyBar = document.getElementById('copy-bar');
  const copyMode = definition.allowCopy === 'own' || definition.allowCopy === 'everyone' ? definition.allowCopy : null;
  const draftBar = document.getElementById('draft-bar');
  let mounted = null;
  let draftId = null; // set once this form has been saved for later (or was opened from a draft)

  function mount(initialValues) {
    if (mounted) mounted.destroy();
    mounted = mountForm(root, {
      definition,
      workflowId,
      initialValues,
      draftAlreadySaved: draftId !== null,
      evaluate: (values, init) => api('POST', '/api/forms/evaluate', { workflowId, values, init }),
      submit: (values) => api('POST', '/api/forms/submit', { workflowId, values, draftId }),
      saveDraft: async (values) => {
        const saved = await api('POST', '/api/forms/drafts', { workflowId, draftId, values });
        if (draftId !== saved.draftId) {
          draftId = saved.draftId;
          // Put the draft in the address so a refresh (or a bookmark) comes back to it.
          window.history.replaceState(null, '', window.location.pathname + '?draft=' + encodeURIComponent(draftId));
        }
        showDraftBar('Saved as a draft. Changes you make from now on are saved automatically.');
        return saved;
      },
      onSuccess: (runId) => { window.location.href = '/Requests/Detail/' + encodeURIComponent(runId); },
    });
  }

  function showDraftBar(message) {
    draftBar.textContent = '';
    draftBar.className = 'copy-bar filled';
    draftBar.append(h('span', { class: 'grow' }, message),
      h('button', { type: 'button', class: 'btn small ghost-danger', onclick: async () => {
        if (!window.confirm('Delete this draft? What you filled in will be lost.')) return;
        try {
          await api('DELETE', '/api/forms/drafts/' + encodeURIComponent(draftId));
        } catch (e) {
          // Already gone is fine; anything else is reported.
          if (!/no longer|Not Found|404/i.test(e.message)) { window.SF.toast(e.message, 'bad'); return; }
        }
        window.location.href = window.location.pathname;
      } }, 'Delete draft'));
  }

  function showOffer() {
    if (!copyMode) return;
    copyBar.textContent = '';
    copyBar.className = 'copy-bar';
    copyBar.append(
      h('span', { class: 'grow' }, copyMode === 'everyone'
        ? 'Done before? Start from a previous request and just change what is different.'
        : 'Submitted this before? Start from one of your previous requests.'),
      h('button', { type: 'button', class: 'btn small', onclick: openPicker }, 'Start from a previous request'));
  }

  function showFilled(source) {
    copyBar.textContent = '';
    copyBar.className = 'copy-bar filled';
    copyBar.append(
      h('span', { class: 'grow' }, h('strong', null, 'Prefilled from ' + source.submitter + '’s request of ' + source.date + '. '),
        'Check every answer, especially dates, before you submit.'),
      h('button', { type: 'button', class: 'btn small', onclick: openPicker }, 'Choose another'),
      h('button', { type: 'button', class: 'btn small', onclick: () => { mount(); showOffer(); } }, 'Start blank'));
  }

  async function useRun(runId) {
    const source = await api('GET', '/api/forms/' + workflowId + '/previous/' + encodeURIComponent(runId));
    mount(source.values || {});
    showFilled(source);
  }

  function openPicker() {
    const search = h('input', { type: 'search', placeholder: copyMode === 'everyone' ? 'Search by person or by what was requested…' : 'Search your previous requests…' });
    const list = h('div', { class: 'copy-list' });
    const dialog = h('dialog', { class: 'dialog picker' },
      h('div', { class: 'row-between' }, h('h2', null, 'Start from a previous request'),
        h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Close', onclick: () => dialog.close() }, '×')),
      search, list);
    let latest = 0;

    async function load() {
      const mine = ++latest;
      try {
        const data = await api('GET', '/api/forms/' + workflowId + '/previous?q=' + encodeURIComponent(search.value.trim()));
        if (mine !== latest) return;
        list.textContent = '';
        if (!data.items.length) {
          list.appendChild(h('p', { class: 'muted' }, search.value.trim() ? 'No previous requests match.' : 'There are no previous requests to copy yet.'));
          return;
        }
        for (const item of data.items) {
          list.appendChild(h('button', { type: 'button', class: 'copy-item', onclick: async () => {
            try {
              await useRun(item.runId);
              dialog.close();
            } catch (e) {
              window.SF.toast(e.message, 'bad');
            }
          } },
          h('span', { class: 'who' }, h('strong', null, item.mine ? 'You' : item.submitter), h('span', { class: 'muted small' }, item.date)),
          h('span', { class: 'what' }, item.summary || 'Request #' + item.runId)));
        }
      } catch (e) {
        if (mine !== latest) return;
        list.textContent = '';
        list.appendChild(h('p', { class: 'flash bad' }, e.message));
      }
    }

    search.addEventListener('input', debounce(load, 250));
    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();
    search.focus();
    load();
  }

  const query = new URLSearchParams(window.location.search);
  const requestedDraft = Number(query.get('draft'));
  // A link such as /Start/3?copy=41 (from a request's page) prefills straight away.
  const requested = copyMode ? Number(query.get('copy')) : 0;
  if (Number.isInteger(requestedDraft) && requestedDraft > 0) {
    // Continue a saved draft.
    api('GET', '/api/forms/drafts/' + encodeURIComponent(requestedDraft)).then((draft) => {
      if (draft.workflowId !== workflowId) throw new Error('That draft belongs to a different form.');
      draftId = draft.draftId;
      mount(draft.values || {});
      showDraftBar('Continuing your draft, last saved ' + draft.savedAt + '. Changes are saved automatically.');
      showOffer();
    }).catch((e) => {
      window.history.replaceState(null, '', window.location.pathname);
      mount();
      showOffer();
      window.SF.toast('That draft could not be opened. ' + e.message, 'bad');
    });
  } else if (Number.isInteger(requested) && requested > 0) {
    useRun(requested).catch((e) => {
      mount();
      showOffer();
      window.SF.toast('That request could not be used as a starting point. ' + e.message, 'bad');
    });
  } else {
    mount();
    showOffer();
  }
})();
