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
  let mounted = null;

  function mount(initialValues) {
    if (mounted) mounted.destroy();
    mounted = mountForm(root, {
      definition,
      initialValues,
      evaluate: (values, init) => api('POST', '/api/forms/evaluate', { workflowId, values, init }),
      submit: (values) => api('POST', '/api/forms/submit', { workflowId, values }),
      onSuccess: (runId) => { window.location.href = '/Requests/Detail/' + encodeURIComponent(runId); },
    });
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

  // A link such as /Start/3?copy=41 (from a request's page) prefills straight away.
  const requested = copyMode ? Number(new URLSearchParams(window.location.search).get('copy')) : 0;
  if (Number.isInteger(requested) && requested > 0) {
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
