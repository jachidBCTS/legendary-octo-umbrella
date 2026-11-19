// Page script for /Requests/Revise/{id}: edit a request that was sent back and resubmit it.
(function () {
  'use strict';
  const { api, mountForm } = window.SF;
  const root = document.getElementById('form-root');
  if (!root) return;
  const runId = Number(root.dataset.runId);
  const definition = JSON.parse(document.getElementById('form-definition').textContent);
  definition.submitLabel = 'Resubmit';

  mountForm(root, {
    definition,
    runId,
    workflowId: Number(root.dataset.workflowId),
    initialValues: JSON.parse(document.getElementById('form-values').textContent),
    previousDisplay: JSON.parse((document.getElementById('form-previous') || {}).textContent || '{}'),
    evaluate: (values, init) => api('POST', '/api/forms/evaluate', { runId, values, init }),
    submit: (values) => api('POST', '/api/forms/resubmit', { runId, values }),
    onSuccess: () => { window.SF.toastAfterLoad('Changes sent. The request is back with the approver.'); window.location.href = '/Requests/Detail/' + encodeURIComponent(runId); },
  });
})();
