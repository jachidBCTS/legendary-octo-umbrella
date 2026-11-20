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
  // A confirmation that is hard to miss: a coloured bar at the top of the screen with a tick (or a cross),
  // for a few seconds. "kind" is ok (default) or bad.
  function toast(message, kind) {
    let el = document.getElementById('toast');
    if (!el) {
      el = h('div', { id: 'toast', role: 'status', 'aria-live': 'polite' });
      el.addEventListener('click', () => { el.className = 'toast'; });
      document.body.appendChild(el);
    }
    const bad = kind === 'bad';
    el.replaceChildren(h('span', { class: 'toast-icon', 'aria-hidden': 'true' }, bad ? '!' : '✓'), h('span', { class: 'toast-text' }, message));
    el.className = 'toast show ' + (kind || 'ok');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.className = 'toast'; }, bad ? 8000 : 4500);
  }

  // A confirmation for the page we are about to go to (e.g. "Request submitted" shown on the request).
  function toastAfterLoad(message, kind) {
    try { sessionStorage.setItem('sf_toast', JSON.stringify({ message, kind: kind || 'ok' })); } catch (e) { /* storage blocked */ }
  }

  // ---- staff email suggestions ---------------------------------------------------------
  // Any <input data-people> suggests staff addresses as you type (from /api/people), in a list just
  // below the box. With data-people="multi" the box holds several addresses separated by commas or
  // semicolons and only the last one is completed. Inputs built later by scripts work too: the
  // listeners are on the document. Up/Down move through the list, Enter picks, Escape closes.
  const peopleCache = new Map();
  const pop = { el: null, input: null, items: [], active: -1, head: '' };
  let quiet = false;   // set while we fill the box ourselves, so that does not start a new lookup

  function popEl() {
    if (!pop.el) {
      pop.el = h('ul', { class: 'people-pop', role: 'listbox', id: 'sf-people-pop', hidden: true });
      pop.el.addEventListener('mousedown', (e) => {
        const li = e.target.closest('li[data-i]');
        e.preventDefault();   // keep the focus in the box
        if (li) choosePerson(Number(li.getAttribute('data-i')));
      });
      document.body.appendChild(pop.el);
    }
    return pop.el;
  }
  function placePop() {
    if (!pop.input || !pop.el || pop.el.hidden) return;
    const r = pop.input.getBoundingClientRect();
    const width = Math.max(r.width, 260);
    pop.el.style.width = Math.min(width, window.innerWidth - 16) + 'px';
    pop.el.style.left = Math.max(8, Math.min(r.left, window.innerWidth - Math.min(width, window.innerWidth - 16) - 8)) + 'px';
    // Always under the box; it is kept short enough to fit the space below.
    pop.el.style.top = (r.bottom + 4) + 'px';
    pop.el.style.maxHeight = Math.max(120, Math.min(280, window.innerHeight - r.bottom - 12)) + 'px';
  }
  function closePop() {
    if (pop.el) pop.el.hidden = true;
    if (pop.input) { pop.input.setAttribute('aria-expanded', 'false'); pop.input.removeAttribute('aria-activedescendant'); }
    pop.items = []; pop.active = -1;
  }
  function markActive(i) {
    pop.active = i;
    [...popEl().children].forEach((li, n) => li.setAttribute('aria-selected', n === i ? 'true' : 'false'));
    const li = popEl().children[i];
    if (li) { pop.input.setAttribute('aria-activedescendant', li.id); li.scrollIntoView({ block: 'nearest' }); }
    else pop.input.removeAttribute('aria-activedescendant');
  }
  function showPeople(input, people, head) {
    const el = popEl();
    pop.input = input; pop.items = people; pop.head = head; pop.active = -1;
    input.setAttribute('role', 'combobox');
    input.setAttribute('aria-autocomplete', 'list');
    input.setAttribute('aria-controls', el.id);
    if (!people.length) { closePop(); return; }
    el.replaceChildren(...people.map((p, i) => h('li', { role: 'option', id: 'sf-person-' + i, 'data-i': i, 'aria-selected': 'false' },
      h('strong', null, p.name || p.email), p.name ? h('span', null, p.email) : null)));
    el.hidden = false;
    input.setAttribute('aria-expanded', 'true');
    // Too close to the bottom of the screen for the list to show under the box: bring the box up first.
    if (window.innerHeight - input.getBoundingClientRect().bottom < 160) input.scrollIntoView({ block: 'center' });
    placePop();
  }
  function choosePerson(i) {
    const input = pop.input;
    const person = pop.items[i];
    if (!input || !person) return;
    const head = pop.head;
    closePop();
    quiet = true;
    input.value = (head && !/\s$/.test(head) ? head + ' ' : head) + person.email;
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
    quiet = false;
    if (input.hasAttribute('data-people-add')) addPerson(input);
  }

  const lookupPeople = debounce(async (input) => {
    const multi = input.getAttribute('data-people') === 'multi';
    const text = input.value;
    const cut = multi ? Math.max(text.lastIndexOf(','), text.lastIndexOf(';')) + 1 : 0;
    const head = text.slice(0, cut);
    const query = text.slice(cut).trim();
    if (query.length < 2) { closePop(); return; }
    let people = peopleCache.get(query.toLowerCase());
    if (!people) {
      try { people = await api('GET', '/api/people?q=' + encodeURIComponent(query)); } catch (e) { return; }
      peopleCache.set(query.toLowerCase(), people || []);
    }
    if (input.value !== text || document.activeElement !== input) return; // they kept typing, or left the box
    showPeople(input, people || [], head);
  }, 250);

  function addPerson(input) {
    const target = document.getElementById(input.getAttribute('data-people-add') || '');
    const email = input.value.trim();
    if (!target || !email) return;
    const lines = target.value.split(/[\r\n,;]+/).map((x) => x.trim()).filter(Boolean);
    if (!lines.some((x) => x.toLowerCase() === email.toLowerCase())) lines.push(email);
    target.value = lines.join('\n');
    target.rows = Math.max(target.rows, lines.length + 1);
    input.value = '';
    input.focus();
  }

  document.addEventListener('input', (e) => {
    const input = e.target;
    if (quiet || !input.matches || !input.matches('input[data-people]')) return;
    lookupPeople(input);
  });
  document.addEventListener('keydown', (e) => {
    const input = e.target;
    if (!input.matches || !input.matches('input[data-people]')) return;
    const open = pop.input === input && pop.el && !pop.el.hidden && pop.items.length > 0;
    if (open && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      e.preventDefault();
      const n = pop.items.length;
      markActive(e.key === 'ArrowDown' ? (pop.active + 1) % n : (pop.active <= 0 ? n - 1 : pop.active - 1));
    } else if (open && e.key === 'Enter' && pop.active >= 0) {
      e.preventDefault();
      choosePerson(pop.active);
    } else if (open && e.key === 'Escape') {
      e.preventDefault();
      closePop();
    } else if (e.key === 'Enter' && input.matches('input[data-people-add]')) {
      e.preventDefault();
      closePop();
      addPerson(input);
    }
  });
  document.addEventListener('focusout', (e) => { if (e.target === pop.input) setTimeout(() => { if (document.activeElement !== pop.input) closePop(); }, 120); });
  window.addEventListener('resize', placePop);
  document.addEventListener('scroll', placePop, true);

  // ---- information tips (ⓘ) -----------------------------------------------------------
  // Instructions sit behind a small "i" next to the heading or label they belong to, and open on
  // hover, on keyboard focus, or with a tap (which keeps them open until tapped again, Escape, or a tap
  // elsewhere). Pages mark them with data-tip (data-tip="page" ties one to the page title); scripts use
  // SF.tip(content) or SF.tipify(root), which turns help paragraphs (.b-help, .sf-help) into tips.
  let tipUid = 0;
  let pinned = null;
  function tip(content) {
    const id = 'sf-tip-' + (++tipUid);
    const text = h('span', { class: 'tip-text', role: 'tooltip', id, hidden: true }, content);
    const button = h('button', { type: 'button', class: 'tip-btn', 'aria-label': 'More information', 'aria-describedby': id }, 'i');
    return h('span', { class: 'tip' }, button, text);
  }
  function placeTip(wrap) {
    const button = wrap.querySelector('.tip-btn');
    const text = wrap.querySelector('.tip-text');
    const r = button.getBoundingClientRect();
    const width = Math.min(340, window.innerWidth - 16);
    text.style.maxWidth = width + 'px';
    const w = Math.min(text.offsetWidth || width, width);
    text.style.left = Math.max(8, Math.min(r.left + r.width / 2 - 24, window.innerWidth - w - 8)) + 'px';
    const below = window.innerHeight - r.bottom;
    if (below < text.offsetHeight + 12 && r.top > below) { text.style.top = (r.top - text.offsetHeight - 8) + 'px'; text.classList.add('above'); }
    else { text.style.top = (r.bottom + 8) + 'px'; text.classList.remove('above'); }
    text.style.setProperty('--arrow', Math.max(10, r.left + r.width / 2 - parseFloat(text.style.left) - 5) + 'px');
  }
  function showTip(wrap) {
    const text = wrap && wrap.querySelector('.tip-text');
    if (!text) return;
    text.hidden = false;
    placeTip(wrap);
  }
  function hideTip(wrap) {
    if (!wrap || wrap === pinned) return;
    const text = wrap.querySelector('.tip-text');
    if (text) text.hidden = true;
  }
  function unpin() {
    const was = pinned;
    pinned = null;
    if (was) { was.querySelector('.tip-btn').setAttribute('aria-expanded', 'false'); hideTip(was); }
  }
  document.addEventListener('pointerover', (e) => {
    const wrap = e.pointerType === 'mouse' && e.target.closest && e.target.closest('.tip');
    if (wrap) showTip(wrap);
  });
  document.addEventListener('pointerout', (e) => {
    const wrap = e.pointerType === 'mouse' && e.target.closest && e.target.closest('.tip');
    if (wrap && !wrap.contains(e.relatedTarget)) hideTip(wrap);
  });
  document.addEventListener('focusin', (e) => { if (e.target.matches && e.target.matches('.tip-btn')) showTip(e.target.parentElement); });
  document.addEventListener('focusout', (e) => { if (e.target.matches && e.target.matches('.tip-btn')) hideTip(e.target.parentElement); });
  document.addEventListener('click', (e) => {
    const button = e.target.closest && e.target.closest('.tip-btn');
    if (button) {
      e.preventDefault();   // a tip inside a <label> must not move the focus to the label's box
      e.stopPropagation();
      const wrap = button.parentElement;
      if (pinned === wrap) { unpin(); return; }
      unpin();
      pinned = wrap;
      button.setAttribute('aria-expanded', 'true');
      showTip(wrap);
      return;
    }
    if (pinned && !(e.target.closest && e.target.closest('.tip-text'))) unpin();
  }, true);
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    const open = document.querySelector('.tip-text:not([hidden])');
    if (open) { unpin(); open.hidden = true; }
  });
  document.addEventListener('scroll', () => { unpin(); document.querySelectorAll('.tip-text:not([hidden])').forEach((t) => { t.hidden = true; }); }, true);

  const HEADING = 'h1, h2, h3, h4, summary';
  const LABEL = 'label, .lbl, .b-label, .sf-label, .b-toggle';
  // The heading or label a piece of help belongs to: the label just before it (help under a box), else
  // the nearest heading before it, looking outwards through the containers it sits in.
  function anchorFor(el) {
    if (el.getAttribute('data-tip') === 'page') return document.querySelector('main .page-head h1');
    // Look back through what comes before it, then through the containers it sits in. Only at its own
    // level does a label count (help inside a field belongs to the field's label).
    for (let node = el, depth = 0; node && depth < 6 && !node.matches('main, body, .bld-body'); node = node.parentElement, depth++) {
      if (depth > 0 && node.matches('summary')) return node;
      for (let sib = node.previousElementSibling; sib; sib = sib.previousElementSibling) {
        if (sib.matches(HEADING) || (depth === 0 && sib.matches(LABEL))) return sib;
        if (sib.matches('.tip, .people-add, .b-row, .sf-field, form, .row, .b-pair, .fld-list, .bld-center')) continue;
        const inner = sib.querySelectorAll(HEADING);
        if (inner.length) return inner[inner.length - 1];
      }
    }
    return null;
  }
  function tipify(root) {
    (root || document).querySelectorAll('[data-tip], .b-help, .sf-help').forEach((el) => {
      if (el.hidden || el.classList.contains('no-tip') || el.closest('.tip-text') || !el.textContent.trim()) return;
      const anchor = anchorFor(el);
      if (!anchor) return;   // nowhere sensible to put it: leave the text where it is
      const content = h('span', { class: 'tip-par' }, ...el.childNodes);
      // A summary or heading that holds more than text gets the tip after its own text.
      const existing = anchor.querySelector('.tip');
      if (existing) existing.querySelector('.tip-text').appendChild(content);
      else {
        const at = anchor.matches('summary') ? anchor.querySelector('.team-name, span, strong') : null;
        (at || anchor).appendChild(tip(content));
      }
      el.remove();
    });
  }

  window.SF = { api, h, debounce, toast, toastAfterLoad, tip, tipify };
  try {
    const pending = JSON.parse(sessionStorage.getItem('sf_toast') || 'null');
    sessionStorage.removeItem('sf_toast');
    if (pending && pending.message) setTimeout(() => toast(pending.message, pending.kind), 150);
  } catch (e) { /* storage blocked */ }
  tipify(document);
  // Screens built by scripts (the builder, forms, tasks) get the same treatment as they are drawn. The
  // observer runs before the browser paints, so the help never flashes up as a paragraph first.
  new MutationObserver((records) => {
    for (const record of records)
      for (const node of record.addedNodes)
        if (node.nodeType === 1 && node.isConnected && !node.closest('.tip')) {
          if (node.matches('[data-tip], .b-help, .sf-help')) tipify(node.parentElement || node);
          else if (node.querySelector('[data-tip], .b-help, .sf-help')) tipify(node);
        }
  }).observe(document.body, { childList: true, subtree: true });

  // Sidebar groups (Build, Admin) remember whether they were left open or closed, on this device only.
  // The group holding the page you are on is always open.
  document.querySelectorAll('details.nav-group[data-nav-group]').forEach((group) => {
    const key = 'sf_nav_' + group.getAttribute('data-nav-group');
    if (group.getAttribute('data-here') !== 'true') {
      try { const saved = localStorage.getItem(key); if (saved !== null) group.open = saved === '1'; } catch (e) { /* storage blocked */ }
    }
    // Only the person's own click (or Enter/Space on the heading) is remembered, not the automatic opening.
    const heading = group.querySelector('summary');
    if (heading) heading.addEventListener('click', () => { try { localStorage.setItem(key, group.open ? '0' : '1'); } catch (e) { /* ignore */ } });
  });

  // Fold-out groups marked data-fold (e.g. the form groups on Home) remember being folded, on this device.
  document.querySelectorAll('details[data-fold]').forEach((group) => {
    const key = 'sf_fold_' + group.getAttribute('data-fold');
    try { const saved = localStorage.getItem(key); if (saved !== null) group.open = saved === '1'; } catch (e) { /* storage blocked */ }
    const heading = group.querySelector('summary');
    if (heading) heading.addEventListener('click', () => { try { localStorage.setItem(key, group.open ? '0' : '1'); } catch (e) { /* ignore */ } });
  });

  // A request's journey can be wider than the screen: start with the stop it is at in view.
  document.querySelectorAll('.train-scroll').forEach((track) => {
    const here = track.querySelector('[aria-current="step"]');
    if (here && track.scrollWidth > track.clientWidth)
      track.scrollLeft = Math.max(0, here.offsetLeft - (track.clientWidth - here.offsetWidth) / 2);
    // Fade whichever edge has more stops beyond it, so it is clear the track scrolls.
    const edges = () => {
      track.classList.toggle('more-left', track.scrollLeft > 4);
      track.classList.toggle('more-right', track.scrollLeft + track.clientWidth < track.scrollWidth - 4);
    };
    edges();
    track.addEventListener('scroll', edges, { passive: true });
    window.addEventListener('resize', edges);
  });

  // Lists edited in one go (form[data-batch]): changes are only marked until "Save changes" is pressed;
  // the bar counts changed rows, and leaving with unsaved changes asks first.
  document.querySelectorAll('form[data-batch]').forEach((form) => {
    const bar = form.querySelector('[data-batch-bar]');
    const text = bar && bar.querySelector('.save-bar-text');
    const save = form.querySelector('[data-batch-save]');
    const undo = form.querySelector('[data-batch-undo]');
    let submitting = false;
    const changed = (el) => el.type === 'checkbox' || el.type === 'radio' ? el.checked !== el.defaultChecked
      : el.tagName === 'SELECT' ? [...el.options].some((o) => o.selected !== o.defaultSelected)
      : el.type !== 'hidden' && el.value !== el.defaultValue;
    const refresh = () => {
      let rows = 0;
      form.querySelectorAll('[data-batch-row]').forEach((row) => {
        const dirty = [...row.querySelectorAll('input, select, textarea')].some(changed);
        row.classList.toggle('row-changed', dirty);
        const on = row.querySelector('[data-row-on]');
        if (on) {
          row.classList.toggle('row-off', !on.checked);
          const label = on.parentElement.querySelector('span');
          if (label) label.textContent = on.checked ? 'Offered' : 'Off';
        }
        if (dirty) rows++;
      });
      if (text) text.textContent = rows === 0 ? 'No unsaved changes' : rows + (rows === 1 ? ' row changed, not saved yet' : ' rows changed, not saved yet');
      if (bar) bar.classList.toggle('dirty', rows > 0);
      if (save) save.disabled = rows === 0;
      if (undo) undo.disabled = rows === 0;
      form.dataset.dirty = rows > 0 ? 'true' : '';
    };
    form.addEventListener('input', refresh);
    form.addEventListener('change', refresh);
    form.addEventListener('reset', () => setTimeout(refresh, 0));
    form.addEventListener('submit', () => { submitting = true; });
    window.addEventListener('beforeunload', (e) => {
      if (!submitting && form.dataset.dirty) { e.preventDefault(); e.returnValue = ''; }
    });
    refresh();
  });

  // ---- new-notification alerts -----------------------------------------------------------
  // Every open page asks every 30 seconds (a minute when the tab is in the background) whether a new
  // notification arrived. If so: the bell's count updates, a short chime plays and, when the person
  // has allowed it on this device, a desktop pop-up appears (on Windows, in the corner like other apps).
  // Works while Greenlight is open in any tab, even minimised; not when the browser is closed.
  const alerts = (() => {
    const store = {
      get(key, fallback) { try { const v = localStorage.getItem('sf.alerts.' + key); return v === null ? fallback : v; } catch (e) { return fallback; } },
      set(key, value) { try { localStorage.setItem('sf.alerts.' + key, String(value)); } catch (e) { /* private window */ } },
    };
    const soundOn = () => store.get('sound', '1') === '1';
    const popupsOn = () => store.get('popups', '1') === '1' && 'Notification' in window && Notification.permission === 'granted';

    let audio = null;
    function chime() {
      try {
        audio = audio || new (window.AudioContext || window.webkitAudioContext)();
        if (audio.state === 'suspended') audio.resume();
        const start = audio.currentTime + 0.02;
        [[880, 0], [1318.5, 0.14]].forEach(([freq, delay]) => {
          const osc = audio.createOscillator();
          const gain = audio.createGain();
          osc.type = 'sine';
          osc.frequency.value = freq;
          gain.gain.setValueAtTime(0.0001, start + delay);
          gain.gain.exponentialRampToValueAtTime(0.18, start + delay + 0.02);
          gain.gain.exponentialRampToValueAtTime(0.0001, start + delay + 0.45);
          osc.connect(gain).connect(audio.destination);
          osc.start(start + delay);
          osc.stop(start + delay + 0.5);
        });
      } catch (e) { /* no sound on this browser */ }
    }
    // Browsers only allow sound after the person has clicked or typed on the page once.
    document.addEventListener('pointerdown', () => { try { audio = audio || new (window.AudioContext || window.webkitAudioContext)(); audio.resume(); } catch (e) { } }, { once: true });

    function popup(item) {
      try {
        const note = new Notification(item.title || 'Greenlight', { body: item.body || '', tag: 'greenlight-' + item.id, silent: soundOn() });
        note.onclick = () => { window.focus(); location.href = item.link; note.close(); };
      } catch (e) { /* some browsers only allow pop-ups from an installed app */ }
    }

    function setBadge(count) {
      document.querySelectorAll('[data-note-badge]').forEach((badge) => {
        badge.textContent = count > 99 ? '99+' : String(count);
        badge.hidden = count === 0;
      });
    }

    let timer = null;
    async function check() {
      clearTimeout(timer);
      const seen = Number(store.get('last', '0')) || 0;
      try {
        const data = await api('GET', '/api/notifications/new?after=' + seen);
        setBadge(data.unread);
        // Another open tab may already have announced these.
        const already = Number(store.get('last', '0')) || 0;
        const fresh = (data.items || []).filter((i) => i.id > already).reverse();
        store.set('last', Math.max(already, data.latest));
        if (fresh.length && seen > 0) {
          if (soundOn()) chime();
          if (popupsOn()) fresh.slice(-3).forEach(popup);
          if (!document.hidden) toast(fresh.length === 1 ? 'New notification: ' + fresh[0].title : fresh.length + ' new notifications');
        }
      } catch (e) { /* signed out or offline: try again later */ }
      timer = setTimeout(check, document.hidden ? 60000 : 30000);
    }
    function start() {
      if (document.body.dataset.alerts !== 'on') return;
      timer = setTimeout(check, 1500);
      document.addEventListener('visibilitychange', () => { if (!document.hidden) check(); });
    }
    return { start, chime, popup, soundOn, store };
  })();
  window.SF.alerts = alerts;
  alerts.start();

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
    const adder = target.closest('[data-people-add-button]');
    if (adder) {
      const input = document.getElementById(adder.getAttribute('data-people-add-button'));
      if (input) addPerson(input);
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
    // (Tapping the dimmed backdrop also lands in the branch above, which closes the menu.)
    const sidebar = document.getElementById('sidebar');
    if (sidebar && sidebar.classList.contains('open') && !target.closest('#sidebar')) sidebar.classList.remove('open');
  });
})();
