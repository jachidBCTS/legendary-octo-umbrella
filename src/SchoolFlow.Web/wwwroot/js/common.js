// Shared helpers for every page. No inline scripts are used anywhere (the site's
// Content-Security-Policy forbids them), so behaviour is attached through data- attributes.
(function () {
  'use strict';

  const csrf = () => document.querySelector('meta[name="csrf-token"]')?.content || '';

  /** JSON API call. Rejects with an Error whose message is safe to show. */
  async function api(method, url, body) {
    let response;
    try {
      response = await fetch(url, {
        method,
        credentials: 'same-origin',
        headers: Object.assign({ 'Accept': 'application/json', 'RequestVerificationToken': csrf() },
          body === undefined ? {} : { 'Content-Type': 'application/json' }),
        body: body === undefined ? undefined : JSON.stringify(body),
        redirect: 'manual',
      });
    } catch (e) {
      throw new Error('Could not reach the server. Check your connection and try again.');
    }
    // A redirect means the session expired and the server wants us to sign in again.
    if (response.type === 'opaqueredirect' || response.status === 401) {
      throw new Error('Your session has expired. Reload the page to sign in again (copy any unsaved text first).');
    }
    let data = null;
    const text = await response.text();
    if (text) {
      try { data = JSON.parse(text); } catch (e) { data = null; }
    }
    if (!response.ok) {
      const message = (data && (data.error || data.title)) ||
        (response.status === 403 ? 'You do not have permission to do that.' :
          response.status === 400 ? 'The request was not accepted. Reload the page and try again.' :
            'Something went wrong (' + response.status + ').');
      throw new Error(message);
    }
    return data;
  }

  /** Tiny DOM builder: h('div', {class: 'x', onclick: fn}, 'text', childNode, …). Text is never parsed as HTML. */
  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    if (attrs) {
      for (const [key, value] of Object.entries(attrs)) {
        if (value === null || value === undefined || value === false) continue;
        if (key === 'class') el.className = value;
        else if (key === 'dataset') Object.assign(el.dataset, value);
        else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
        else if (key === 'value') el.value = value;
        else if (key === 'checked' || key === 'disabled' || key === 'selected' || key === 'hidden') el[key] = !!value;
        else el.setAttribute(key, value === true ? '' : String(value));
      }
    }
    append(el, children);
    return el;
  }

  function append(el, children) {
    for (const child of children) {
      if (child === null || child === undefined || child === false) continue;
      if (Array.isArray(child)) append(el, child);
      else el.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
    }
  }

  function debounce(fn, ms) {
    let timer = null;
    const wrapped = (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => fn(...args), ms);
    };
    wrapped.cancel = () => clearTimeout(timer);
    return wrapped;
  }

  let toastTimer = null;
  function toast(message, kind) {
    let el = document.getElementById('toast');
    if (!el) {
      el = h('div', { id: 'toast', role: 'status', 'aria-live': 'polite' });
      document.body.appendChild(el);
    }
    el.textContent = message;
    el.className = 'toast show ' + (kind || 'ok');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.className = 'toast'; }, kind === 'bad' ? 7000 : 3200);
  }

  window.SF = { api, h, debounce, toast };

  document.addEventListener('submit', (e) => {
    const message = e.target.getAttribute && e.target.getAttribute('data-confirm');
    if (message && !window.confirm(message)) e.preventDefault();
  });

  document.addEventListener('change', (e) => {
    if (!e.target.matches) return;
    if (e.target.matches('[data-autosubmit]') && e.target.form) e.target.form.submit();
    // Parts of the "new workflow" dialog only apply to one way of starting.
    if (e.target.matches('input[name="trigger"]') && e.target.form) {
      e.target.form.querySelectorAll('[data-show-when-trigger]').forEach((el) => {
        el.hidden = el.getAttribute('data-show-when-trigger') !== e.target.value;
      });
    }
  });

  document.addEventListener('click', (e) => {
    const target = e.target.closest ? e.target : null;
    if (!target) return;
    const opener = target.closest('[data-open-dialog]');
    if (opener) {
      const dialog = document.getElementById(opener.getAttribute('data-open-dialog'));
      if (dialog && dialog.showModal) {
        dialog.showModal();
        const first = dialog.querySelector('input:not([type=hidden]), textarea, select');
        if (first) first.focus();
      }
      return;
    }
    const closer = target.closest('[data-close-dialog]');
    if (closer) {
      const dialog = closer.closest('dialog');
      if (dialog) dialog.close();
      return;
    }
    if (target.closest('[data-toggle-sidebar]')) {
      document.getElementById('sidebar')?.classList.toggle('open');
      return;
    }
    const sidebar = document.getElementById('sidebar');
    if (sidebar && sidebar.classList.contains('open') && !target.closest('#sidebar')) sidebar.classList.remove('open');
  });
})();
