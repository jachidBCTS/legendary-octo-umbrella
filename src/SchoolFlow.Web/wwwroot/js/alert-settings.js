// "Alerts on this device" on the Notifications page: sound and desktop pop-ups for new notifications
// (the checking itself is in common.js). Choices are kept in this browser only.
(function () {
  'use strict';
  const box = document.querySelector('[data-alert-settings]');
  if (!box || !window.SF || !SF.alerts) return;
  const { store } = SF.alerts;
  const sound = box.querySelector('[data-alert-sound]');
  const popups = box.querySelector('[data-alert-popups]');
  const status = box.querySelector('[data-alert-status]');
  const supported = 'Notification' in window;

  function show() {
    sound.checked = store.get('sound', '1') === '1';
    if (!supported) {
      popups.checked = false;
      popups.disabled = true;
      status.textContent = 'This browser cannot show pop-ups. The sound still works.';
      return;
    }
    const permission = Notification.permission;
    popups.checked = store.get('popups', '1') === '1' && permission === 'granted';
    status.textContent = permission === 'denied'
      ? 'Pop-ups are blocked for this site in the browser. To allow them: click the padlock (or "site information") icon left of the address, set Notifications to Allow, then reload this page.'
      : popups.checked ? 'Pop-ups are on. If none appear, check that Windows notifications are on for your browser (Settings → System → Notifications) and that "Do not disturb" is off.'
        : '';
  }

  sound.addEventListener('change', () => { store.set('sound', sound.checked ? '1' : '0'); if (sound.checked) SF.alerts.chime(); show(); });
  popups.addEventListener('change', async () => {
    if (!popups.checked) { store.set('popups', '0'); show(); return; }
    store.set('popups', '1');
    // The browser asks the person once; their answer is remembered for this site.
    if (Notification.permission === 'default') {
      try { await Notification.requestPermission(); } catch (e) { /* older browsers */ }
    }
    show();
  });
  box.querySelector('[data-alert-test]').addEventListener('click', () => {
    if (sound.checked) SF.alerts.chime();
    if (supported && Notification.permission === 'granted' && popups.checked)
      SF.alerts.popup({ id: 'test', title: 'Greenlight', body: 'This is how a new notification will look.', link: '/Notifications' });
    else SF.toast(sound.checked ? 'Sound played. Switch on pop-ups to see them too.' : 'Switch on the sound or pop-ups first.');
  });
  show();
})();
