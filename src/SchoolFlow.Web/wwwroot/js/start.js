// Page script for /Start/{id}: mounts the live form for a published workflow.
(function () {
  'use strict';
  const root = document.getElementById('form-root');
  if (!root) return;
  const workflowId = Number(root.dataset.workflowId);
  const definition = JSON.parse(document.getElementById('form-definition').textContent);

  window.SF.mountForm(root, {
    definition,
    evaluate: (values, init) => window.SF.api('POST', '/api/forms/evaluate', { workflowId, values, init }),
    submit: (values) => window.SF.api('POST', '/api/forms/submit', { workflowId, values }),
    onSuccess: (runId) => { window.location.href = '/Requests/Detail/' + encodeURIComponent(runId); },
  });
})();
