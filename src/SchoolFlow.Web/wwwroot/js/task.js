// Page script for /Requests/Detail/{id} when the person has a task to complete on the request.
(function () {
  'use strict';
  const { api, mountForm } = window.SF;
  const root = document.getElementById('task-root');
  if (!root) return;
  const taskId = Number(root.dataset.taskId);
  const definition = JSON.parse(document.getElementById('task-definition').textContent);

  mountForm(root, {
    definition,
    taskId,
    workflowId: Number(root.dataset.workflowId),
    // Sent back to be done again: what was entered last time, and how it read.
    initialValues: JSON.parse((document.getElementById('task-values') || {}).textContent || '{}'),
    previousDisplay: JSON.parse((document.getElementById('task-previous') || {}).textContent || '{}'),
    evaluate: (values, init) => api('POST', '/api/tasks/' + taskId + '/evaluate', { values, init }),
    submit: (values) => api('POST', '/api/tasks/' + taskId + '/complete', { values }),
    onSuccess: () => { window.SF.toastAfterLoad('Task completed. The request has moved on.'); window.location.reload(); },
  });
})();
