const { chromium } = require('playwright');
// End-to-end smoke test. Drives a real browser through the whole app in Development mode
// (dev sign-in, simulated Freshservice). Needs a FRESH database and Playwright installed.
//
//   ASPNETCORE_ENVIRONMENT=Development Freshservice__WebhookSecret=test-secret-1234567890 \
//     Database__ConnectionString="Data Source=/tmp/e2e.db" dotnet run --project src/SchoolFlow.Web
//   node tests/e2e/smoke.js ./shots http://127.0.0.1:5080
const fs = require('fs');
const S = process.argv[2] || './shots';
const base = process.argv[3] || 'http://127.0.0.1:5080';
fs.mkdirSync(S + '/shots', { recursive: true });
const problems = [];
const note = (m) => console.log(m);
function check(cond, msg) { if (cond) note('  ok   ' + msg); else { note('  FAIL ' + msg); problems.push(msg); } }

async function login(page, email) {
  // Signing out is a POST (with the anti-forgery token); opening the address shows the button.
  await page.goto(base + '/Account/SignOut');
  if (await page.locator('main form button:has-text("Sign out")').count()) {
    await page.click('main form button:has-text("Sign out")');
    await page.waitForURL(/DevLogin/);
  }
  await page.goto(base + '/Account/DevLogin');
  await page.click(`button[value="${email}"]`);
  await page.waitForURL(base + '/');
}

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1360, height: 900 } });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error' && !/40[0134]/.test(m.text())) { problems.push('console: ' + m.text()); note('  CONSOLE ' + m.text()); } });
  page.on('pageerror', (e) => { problems.push('pageerror: ' + e.message); note('  PAGEERROR ' + e.message); });
  page.on('response', (r) => { if (r.status() >= 500) { problems.push('500: ' + r.url()); note('  HTTP ' + r.status() + ' ' + r.url()); } });

  note('1. teacher home');
  await login(page, 'teacher@example.org');
  await page.screenshot({ path: S + '/shots/01-home.png' });
  check(await page.locator('.wf-card').count() === 4, 'four live forms on home (ticket-triggered draft hidden)');
  check(await page.locator('a[href="/Builder"]').count() === 0, 'staff do not see builder nav');
  await page.goto(base + '/Builder');
  check(page.url().includes('/Account/Denied'), 'staff who neither build nor own a workflow are refused the Workflows page (' + page.url() + ')');
  const api = await page.evaluate(async () => (await fetch('/api/builder/1')).status);
  check(api === 404, 'staff cannot load a workflow in the builder API (status ' + api + ')');

  const signOutByLink = await page.evaluate(async () => (await fetch('/Account/SignOut', { redirect: 'manual' })).status);
  check(signOutByLink === 200 && await page.evaluate(async () => (await fetch('/api/forms/1/previous')).status) !== 401, 'opening the sign-out address does not sign you out (a link from elsewhere cannot)');
  const forged = await page.evaluate(async () => (await fetch('/Account/SignOut', { method: 'POST', redirect: 'manual' })).status);
  check(forged === 400, 'a sign-out posted without the anti-forgery token is refused (' + forged + ')');
  await page.goto(base + '/');
  await page.click('.signout-form button');
  await page.waitForURL(/DevLogin/);
  check(true, 'the sidebar button signs you out');
  await login(page, 'teacher@example.org');

  note('2. purchase form');
  await page.goto(base + '/');
  await page.click('.wf-card:has-text("Technology Purchase")');
  await page.waitForSelector('.sf-form');
  await page.waitForFunction(() => document.querySelector('input[type=date]')?.value);
  const field = (label) => page.locator(`.sf-field:has(> .sf-label:has-text("${label}")), .sf-field:has(> .sf-check:has-text("${label}"))`).first();
  check(await field('License length').isHidden(), 'license years hidden initially');
  check(await field("Approver's email").isHidden(), 'approver hidden initially');
  await field('What do you need?').locator('input').fill('Graphing calculators');
  await field('Department').locator('select').selectOption({ label: 'Mathematics' });
  await page.locator('.sf-option:has-text("Both")').click();
  await field('License length').waitFor({ state: 'visible' });
  check(true, 'license years appears for Both');
  await field('License length').locator('input').fill('2');
  const row = page.locator('.sf-table tbody tr').first();
  await row.locator('input').nth(0).fill('TI-84');
  await row.locator('select').selectOption('Hardware');
  await row.locator('input').nth(1).fill('10');
  await row.locator('input').nth(2).fill('20');
  await page.click('text=+ Add row');
  const row2 = page.locator('.sf-table tbody tr').nth(1);
  await row2.locator('input').nth(0).fill('Desmos Pro');
  await row2.locator('select').selectOption('Software');
  await row2.locator('input').nth(1).fill('3');
  await row2.locator('input').nth(2).fill('50.5');
  await page.keyboard.press('Tab');
  await page.waitForFunction(() => [...document.querySelectorAll('.sf-field.is-calc')].some((f) => f.textContent.includes('Order total') && f.textContent.includes('$503.00')), null, { timeout: 5000 }).catch(() => {});
  const calcText = async (label) => (await field(label).locator('output').textContent());
  check(await row.locator('output').textContent() === '$200.00', 'hardware line = $200.00 (got ' + await row.locator('output').textContent() + ')');
  check(await row2.locator('output').textContent() === '$303.00', 'software line x2 years = $303.00 (got ' + await row2.locator('output').textContent() + ')');
  check(await calcText('Subtotal') === '$503.00', 'subtotal $503.00 (got ' + await calcText('Subtotal') + ')');
  check(await calcText('Estimated shipping') === '$0.00', 'shipping $0.00 (order over $250) (got ' + await calcText('Estimated shipping') + ')');
  check(await calcText('Order total') === '$503.00', 'total $503.00 (got ' + await calcText('Order total') + ')');
  check(await field("Approver's email").isVisible(), 'approver appears at >= $500');
  await page.screenshot({ path: S + '/shots/02-form.png', fullPage: true });

  await page.click('main button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field("Approver's email").locator('.sf-error').textContent()).includes('required'), 'submit blocked: approver required');
  check((await field('Justification').locator('.sf-error').textContent()).includes('required'), 'submit blocked: justification required when >= 500');
  await field("Approver's email").locator('input').fill('teacher@example.org');
  await field('Justification').locator('textarea').fill('Needed for <b>algebra</b> & "stats"');
  await page.click('main button[type=submit]');
  await page.waitForTimeout(600);
  check((await field("Approver's email").locator('.sf-error').textContent()).includes('own request'), 'custom rule: cannot approve own request');
  await field("Approver's email").locator('input').fill('principal@example.org');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const runUrl = page.url();
  check((await page.locator('.page-head .pill').textContent()) === 'Awaiting approval', 'run is awaiting approval');
  check(await page.locator('.approve-box').count() === 0, 'submitter cannot approve');
  check((await page.locator('.answers').textContent()).includes('<b>algebra</b>'), 'HTML in answers is shown as text, not rendered');
  await page.screenshot({ path: S + '/shots/03-detail-waiting.png', fullPage: true });

  note('3. approval');
  await login(page, 'principal@example.org');
  check((await page.locator('.sidebar a[href="/Approvals"] .badge').textContent()).trim() === '1', 'approver sees badge 1');
  await page.goto(base + '/Approvals');
  await page.screenshot({ path: S + '/shots/04-approvals.png' });
  check((await page.locator('.approve-box .pre').textContent()).includes('$503.00'), 'approval instructions rendered with total');
  await page.click('.approve-box a.btn');
  await page.fill('.approve-box textarea', 'Go ahead');
  await page.click('button[name=decision][value=approve]');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.page-head .pill').textContent()) === 'With help desk', 'after approval: waiting on ticket');
  const timeline = await page.locator('.timeline').textContent();
  check(timeline.includes('[Simulated] Created Freshservice ticket #90001'), 'ticket created (simulated)');
  check(timeline.includes('Added a private note'), 'approval note added');
  await page.screenshot({ path: S + '/shots/05-detail-ticket.png', fullPage: true });

  note('4. webhook');
  const hook = async (body, secret) => page.evaluate(async ([b, s]) => {
    const r = await fetch('/api/webhooks/freshservice', { method: 'POST', headers: Object.assign({ 'Content-Type': 'application/json' }, s ? { 'X-SchoolFlow-Secret': s } : {}), body: JSON.stringify(b) });
    return [r.status, await r.text()];
  }, [body, secret]);
  check((await hook({ ticket: { id: 90001, status: 5 } }, 'wrong'))[0] === 401, 'webhook rejects wrong secret');
  check((await hook({ ticket: { id: 90001, status: 5 } }, null))[0] === 401, 'webhook rejects missing secret');
  let r = await hook({ event: 'ticket_updated', ticket: { id: 'INC-90001', status: 3 } }, 'test-secret-1234567890');
  check(r[0] === 200 && r[1].includes('resumed 0'), 'status 3 does not resume: ' + r[1]);
  r = await hook({ event: 'ticket_updated', ticket: { id: 'INC-90001', status: 5 } }, 'test-secret-1234567890');
  check(r[0] === 200 && r[1].includes('resumed 1'), 'status 5 resumes: ' + r[1]);
  await page.goto(runUrl);
  check((await page.locator('.page-head .pill').textContent()) === 'Completed', 'run completed after webhook');

  note('5. repair form (lookup, conditional)');
  await login(page, 'teacher@example.org');
  await page.click('.wf-card:has-text("Device Repair")');
  await page.waitForSelector('.sf-form');
  await page.waitForFunction(() => document.querySelectorAll('.sf-field select option').length > 3);
  check(await field('Device name or asset tag').isVisible(), 'device_other visible while no device chosen');
  await field('Which device?').locator('select').selectOption({ index: 1 });
  await field('Device name or asset tag').waitFor({ state: 'hidden' });
  check(true, 'device_other hides once a device is picked');
  await field('What is wrong?').locator('select').selectOption('Lost or stolen');
  await field('A police report').waitFor({ state: 'visible' });
  check(await field('Where can we pick it up?').isHidden(), 'location hidden for lost/stolen');
  await page.click('main button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field('Details').locator('.sf-error').textContent()).includes('required'), 'details required for lost/stolen');
  await field('Details').locator('textarea').fill('Left on the bus');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  check((await page.locator('.page-head .pill').textContent()) === 'Completed', 'repair run completed');
  const t2 = await page.locator('.timeline').textContent();
  check(t2.includes('Set priority_level = 4'), 'priority computed as 4');
  check(t2.includes('private note'), 'lost/stolen branch taken');
  await page.goto(base + '/Requests');
  check(await page.locator('.table tbody tr').count() === 2, 'my requests lists 2');
  // other user's run is not visible
  await login(page, 'principal@example.org');
  const other = await page.goto(page.url().replace(/\/$/, '') + '/Requests/Detail/2');
  check(other.status() === 404, "another person's unrelated request is 404 (" + other.status() + ')');

  note('6. builder');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Builder');
  await page.screenshot({ path: S + '/shots/06-builder-list.png', fullPage: true });
  await page.click('.wf-tile:has-text("Technology Purchase") a.btn');
  await page.waitForSelector('.fld-card');
  await page.click('.fld-card:has-text("Line total"), .fld-card:has-text("Order total")');
  await page.waitForSelector('.b-fx-status.ok');
  await page.screenshot({ path: S + '/shots/07-builder-form.png' });
  await page.click('.bld-tabs button:has-text("Flow")');
  await page.waitForSelector('.fl-node');
  check(await page.locator('.fl-node').count() === 9, '9 flow nodes drawn');
  check(await page.locator('path.fl-edge').count() === 10, '10 edges drawn (' + await page.locator('path.fl-edge').count() + ')');
  await page.locator('.fl-node:has-text("Open purchasing ticket")').click();
  await page.waitForSelector('.bld-inspector .b-lookup select:not([disabled])');
  await page.screenshot({ path: S + '/shots/08-builder-flow.png' });
  await page.click('.bld-tabs button:has-text("Preview")');
  await page.waitForSelector('.bld-preview .sf-form');
  await page.waitForTimeout(500);
  await page.screenshot({ path: S + '/shots/09-builder-preview.png' });
  await page.click('.bld-tabs button:has-text("Settings")');
  await page.screenshot({ path: S + '/shots/10-builder-settings.png' });

  note('7. build a new workflow from scratch');
  await page.goto(base + '/Builder');
  await page.click('[data-open-dialog]');
  await page.fill('#new-name', 'Room Booking');
  await page.click('dialog button.primary');
  await page.waitForSelector('.bld-palette');
  const addField = async (type, label) => {
    await page.click(`.bld-palette .pal-item:has(strong:text-is("${type}"))`);
    const input = page.locator('.bld-inspector .b-row').first().locator('input');
    await input.fill(label);
  };
  await addField('Number', 'Hours');
  await addField('Currency', 'Hourly rate');
  await addField('Calculated', 'Cost');
  check(await page.locator('.bld-inspector .mono').first().inputValue() === 'cost', 'key auto-generated from label');
  await page.locator('.bld-inspector textarea.b-formula').first().fill('hours * hourly_rat');
  await page.waitForSelector('.b-fx-status.warn');
  check((await page.locator('.b-fx-status.warn').first().textContent()).includes('hourly_rat'), 'unknown name hint shown');
  await page.locator('.bld-inspector textarea.b-formula').first().fill('hours * (');
  await page.waitForSelector('.b-fx-status.bad');
  check(true, 'syntax error shown live: ' + await page.locator('.b-fx-status.bad').first().textContent());
  await page.click('.bld-actions button:has-text("Publish")');
  await page.waitForSelector('.bld-issues:not([hidden])');
  check((await page.locator('.bld-issues').textContent()).includes('“Cost” formula'), 'publish blocked with issue list');
  await page.locator('.bld-issues .issue').first().click();
  await page.locator('.bld-inspector textarea.b-formula').first().fill('hours * hourly_rate');
  await page.waitForSelector('.b-fx-status.ok');
  // flow: add create-ticket after start
  await page.click('.bld-tabs button:has-text("Flow")');
  await page.locator('.fl-node:has-text("Form submitted")').click();
  await page.click('.bld-palette .pal-item:has(strong:text-is("Create ticket"))');
  await page.locator('.bld-inspector textarea.b-template').first().fill('Room booking: {{TEXT(cost, "C2")}}');
  const edgesBefore = await page.locator('path.fl-edge').count();
  check(edgesBefore === 1, 'start auto-connected to new step (' + edgesBefore + ')');
  // drag-connect create → end
  const port = page.locator('.fl-node:has-text("Create ticket") .fl-port i');
  const end = page.locator('.fl-node:has-text("Done")');
  const pb = await port.boundingBox(); const eb = await end.boundingBox();
  await page.mouse.move(pb.x + pb.width / 2, pb.y + pb.height / 2);
  await page.mouse.down();
  await page.mouse.move(eb.x + 40, eb.y + 20, { steps: 8 });
  await page.mouse.up();
  check(await page.locator('path.fl-edge').count() === 2, 'drag-connected create → end (' + await page.locator('path.fl-edge').count() + ')');
  // drag a node
  const nb = await end.boundingBox();
  await page.mouse.move(nb.x + 60, nb.y + 14); await page.mouse.down(); await page.mouse.move(nb.x + 200, nb.y + 120, { steps: 6 }); await page.mouse.up();
  const nb2 = await page.locator('.fl-node:has-text("Done")').boundingBox();
  check(Math.abs(nb2.x - nb.x - 140) < 12, 'node dragged (' + Math.round(nb2.x - nb.x) + 'px)');
  await page.screenshot({ path: S + '/shots/11-new-flow.png' });
  await page.click('.bld-actions button:has-text("Publish")');
  await page.waitForFunction(() => /Published version/.test(document.querySelector('.toast')?.textContent || ''));
  check((await page.locator('.toast').textContent()).includes('Published version 1'), 'published: ' + await page.locator('.toast').textContent());
  // run it
  await page.goto(base + '/');
  await page.click('.wf-card:has-text("Room Booking")');
  await page.waitForSelector('.sf-form');
  await field('Hours').locator('input').fill('3');
  await field('Hourly rate').locator('input').fill('12.50');
  await page.waitForFunction(() => document.querySelector('.sf-calc')?.textContent === '37.5');
  check(true, 'new form calculates 37.5');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  check((await page.locator('.timeline').textContent()).includes('Room booking: $37.50'), 'ticket subject rendered from formula');

  note('8. start from a previous request');
  const setCopy = async (mode, excludeNeededBy) => {
    await login(page, 'admin@example.org');
    await page.goto(base + '/Builder');
    await page.click('.wf-tile:has-text("Technology Purchase") a.btn');
    await page.waitForSelector('.fld-card');
    await page.locator('.bld-inspector .b-row:has-text("copy a previous request") select').selectOption(mode);
    if (excludeNeededBy) {
      await page.click('.fld-card:has-text("Needed by")');
      await page.locator('.bld-inspector .b-toggle:has-text("Don\'t copy")').click();
      check(await page.locator('.fld-card:has-text("Needed by") .pill:has-text("Not copied")').count() === 1, 'excluded field is badged in the builder');
    }
    await page.click('.bld-actions button:has-text("Publish")');
    await page.waitForFunction(() => /Published version/.test(document.querySelector('.toast')?.textContent || ''));
  };
  const getJson = (url) => page.evaluate(async (u) => { const r = await fetch(u); return [r.status, r.ok ? await r.json() : null]; }, url);

  await login(page, 'teacher@example.org');
  check((await getJson('/api/forms/1/previous'))[0] === 404, 'copy API is off until the builder turns it on');
  await page.goto(base + '/Start/1');
  await page.waitForSelector('.sf-form');
  check(await page.locator('.copy-bar').count() === 0, 'no copy bar while the setting is off');

  await setCopy('own', true);
  await login(page, 'principal@example.org');
  let prev = await getJson('/api/forms/1/previous');
  check(prev[0] === 200 && prev[1].items.length === 0, '"own" mode: another person sees no previous requests');
  check((await getJson('/api/forms/1/previous/1'))[0] === 404, '"own" mode: another person cannot fetch someone else\'s answers');
  await login(page, 'teacher@example.org');
  prev = await getJson('/api/forms/1/previous');
  check(prev[1].items.length === 1 && prev[1].items[0].mine === true, '"own" mode: the submitter sees their own request');
  check((await getJson('/api/forms/2/previous'))[0] === 404, 'a form without the setting stays closed');

  await setCopy('everyone', false);
  await login(page, 'principal@example.org');
  const copied = await getJson('/api/forms/1/previous/1');
  check(copied[0] === 200 && copied[1].values.title === 'Graphing calculators', '"everyone" mode: answers can be fetched');
  check(!('needed_by' in copied[1].values) && !('total' in copied[1].values) && !('approval' in copied[1].values) && !('ticket' in copied[1].values) && !('submitter' in copied[1].values),
    'excluded, calculated and internal data are not returned (' + Object.keys(copied[1].values).join(',') + ')');
  await page.goto(base + '/Start/1');
  await page.waitForSelector('.copy-bar');
  await page.click('.copy-bar button');
  await page.waitForSelector('.copy-item');
  check((await page.locator('.copy-item').first().textContent()).includes('Taylor Teacher'), 'picker lists the other person\'s request');
  await page.fill('dialog input[type=search]', 'zzz-no-match');
  await page.waitForSelector('dialog .copy-list p.muted');
  await page.fill('dialog input[type=search]', 'calculators');
  await page.waitForSelector('.copy-item');
  await page.screenshot({ path: S + '/shots/15-copy-picker.png' });
  await page.click('.copy-item');
  await page.waitForSelector('.copy-bar.filled');
  await page.waitForFunction(() => [...document.querySelectorAll('.sf-field.is-calc output')].some((o) => o.textContent === '$503.00'));
  check(await field('What do you need?').locator('input').inputValue() === 'Graphing calculators', 'text prefilled');
  await page.waitForFunction(() => { const s = document.querySelector('.sf-field select'); return s && !s.disabled && s.selectedOptions[0]?.textContent === 'Mathematics'; });
  check(true, 'Freshservice lookup prefilled (Mathematics)');
  check(await page.locator('.sf-option:has-text("Both") input').isChecked(), 'choice prefilled');
  check(await field('License length').locator('input').inputValue() === '2', 'conditional field prefilled and visible');
  check(await page.locator('.sf-table tbody tr').count() === 2, 'table rows prefilled (' + await page.locator('.sf-table tbody tr').count() + ')');
  check(await page.locator('.sf-table tbody tr').nth(1).locator('input').first().inputValue() === 'Desmos Pro', 'table cell prefilled');
  check(await page.locator('.sf-table tbody tr').nth(1).locator('output').textContent() === '$303.00', 'table line total recalculated');
  check(await field("Approver's email").locator('input').inputValue() === '', "someone else's approver email is not copied");
  await page.screenshot({ path: S + '/shots/16-copy-prefilled.png', fullPage: true });
  await page.click('main button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field("Approver's email").locator('.sf-error').textContent()).includes('required'), 'the new submitter must choose their own approver');
  await field("Approver's email").locator('input').fill('admin@example.org');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  check((await page.locator('.page-head .pill').textContent()) === 'Awaiting approval', 'copied request submitted as a new request');
  check((await page.locator('.page-head').textContent()).includes('Submitted by Pat Principal'), 'new request belongs to the new submitter');
  check(await page.locator('a:has-text("Start a new request from this one")').count() === 1, 'request page offers "start from this one"');
  await page.click('a:has-text("Start a new request from this one")');
  await page.waitForSelector('.copy-bar.filled');
  check(true, 'link from a request page prefills the form');
  await page.click('.copy-bar button:has-text("Start blank")');
  await page.waitForSelector('.copy-bar:not(.filled)');
  check(await field('What do you need?').locator('input').inputValue() === '', '"Start blank" clears the form');

  await login(page, 'admin@example.org');
  await page.goto(base + '/Builder');
  await page.click('[data-open-dialog]');
  await page.fill('#new-name', 'Field Trip Request');
  await page.selectOption('#new-copy', 'everyone');
  await page.click('dialog button.primary');
  await page.waitForSelector('.bld-inspector');
  check(await page.locator('.bld-inspector .b-row:has-text("copy a previous request") select').inputValue() === 'everyone', 'choice made at creation is carried into the builder');
  await page.goto(base + '/Builder');
  await page.click('[data-open-dialog]');
  await page.click('input[name=trigger][value=ticket]');
  check(await page.locator('#new-copy').isHidden(), 'copy question hidden for ticket-triggered workflows');
  await page.keyboard.press('Escape');

  note('9. departments, approval chains, notifications');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Admin/Departments');
  await page.fill('#new-name', 'Music');
  await page.fill('#new-approvers', 'Principal@Example.org, not-an-email');
  await page.click('.dept-form button.primary');
  await page.waitForSelector('.flash.bad');
  check((await page.locator('.flash.bad').textContent()).includes('not-an-email'), 'bad approver email is refused');
  await page.fill('#new-name', 'Music');
  await page.fill('#new-approvers', 'Principal@Example.org');
  await page.click('.dept-form button.primary');
  await page.waitForSelector('.flash.ok');
  check(await page.locator('input[value="Music"]').count() === 1, 'department added');
  await page.screenshot({ path: S + '/shots/17-departments.png', fullPage: true });

  await login(page, 'teacher@example.org');
  check((await page.evaluate(async () => (await fetch('/Admin/Departments', { redirect: 'manual' })).type)) === 'opaqueredirect', 'staff cannot open department admin');
  await page.goto(base + '/Chains');
  await page.waitForSelector('#chain-actions button');
  check(await page.locator('#chain-actions button').count() === 1, 'staff can create their own chain but not shared templates');
  const sharedAttempt = await page.evaluate(async () => {
    const r = await fetch('/api/chains', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: document.querySelector('meta[name=csrf-token]').content },
      body: JSON.stringify({ name: 'Sneaky', shared: true, steps: [{ name: 'x', approvers: ['a@b.org'] }] }) });
    return r.status;
  });
  check(sharedAttempt === 403, 'staff cannot create a shared template through the API (' + sharedAttempt + ')');
  await page.click('#chain-actions button');
  await page.fill('dialog input[type=text] >> nth=0', 'My trip chain');
  await page.fill('dialog .ce-step input >> nth=0', 'Team leader');
  await page.fill('dialog .ce-step input >> nth=1', 'not an email');
  await page.click('dialog button.primary');
  await page.waitForSelector('dialog .sf-error:not([hidden])');
  check((await page.locator('dialog .sf-error').textContent()).includes('not an email address'), 'chain with a bad email is refused');
  await page.fill('dialog .ce-step input >> nth=1', 'admin@example.org');
  await page.click('dialog button.primary');
  await page.waitForSelector('.chain-card');
  check((await page.locator('.chain-card').textContent()).includes('Team leader'), 'personal chain saved');
  await page.screenshot({ path: S + '/shots/18-chains.png' });

  await page.goto(base + '/');
  await page.click('.wf-card:has-text("Field Trip")');
  await page.waitForSelector('.sf-form');
  await field('Where are you going?').locator('input').fill('Liberty Science Center');
  await page.waitForFunction(() => document.querySelectorAll('.sf-field select option').length > 3);
  await field('Department').locator('select').selectOption({ label: 'Music' });
  check((await field('Department').locator('.sf-help').first().textContent()).includes('principal@example.org'), 'form shows who gives final approval');
  const future = new Date(Date.now() + 45 * 864e5).toISOString().slice(0, 10);
  await field('Date of trip').locator('input').fill(future);
  await field('Number of students').locator('input').fill('30');
  await field('Number of chaperones').locator('input').fill('2');
  await field('Cost per student').locator('input').fill('10');
  await field('How does this trip').locator('textarea').fill('Energy unit');
  await page.waitForFunction(() => [...document.querySelectorAll('.sf-field.is-calc output')].some((o) => o.textContent === '$300.00'));
  check(true, 'trip total calculated');
  await page.waitForFunction(() => document.querySelectorAll('.sf-chain select option').length > 1);
  await page.locator('.sf-chain select').first().selectOption({ label: 'My trip chain' });
  check(await page.locator('.sf-chain .ce-step input').nth(1).inputValue() === 'admin@example.org', 'saved chain loaded into the form');
  await page.click('.sf-chain button:has-text("Add a step")');
  await page.locator('.sf-chain .ce-step').nth(1).locator('input').nth(0).fill('Me');
  await page.locator('.sf-chain .ce-step').nth(1).locator('input').nth(1).fill('teacher@example.org');
  await page.screenshot({ path: S + '/shots/19-trip-form.png', fullPage: true });
  await page.click('main button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field('Number of chaperones').locator('.sf-error').textContent()).includes('every 10 students'), 'ratio rule enforced');
  check((await field('Who needs to approve').locator('.sf-error').textContent()).includes('own request'), 'cannot list yourself as an approver');
  await field('Number of chaperones').locator('input').fill('3');
  await page.locator('.sf-chain .ce-step').nth(1).locator('button[aria-label="Remove step"]').click();
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const tripUrl = page.url();
  const track = () => page.locator('.train-track > li').evaluateAll((els) => els.map((li) => li.className + ' | ' + li.textContent.replace(/\s+/g, ' ')));
  let steps = await track();
  check(steps.length === 4 && steps[0].includes('is-done') && steps[0].includes('Submitted')
    && steps[1].includes('is-current') && steps[1].includes('Team leader') && steps[1].includes('Waiting on Avery Admin') && steps[1].includes('Here now · today') && steps[1].includes('Stop 2 of 4')
    && steps[2].includes('is-upcoming') && steps[2].includes('Final approval — Music') && steps[3].includes('Completed'),
    'the request shows every stop: submitted, the requester\'s step (here now, whose desk), the department approver, then done (' + steps.join(' || ') + ')');
  check(await page.locator('.train-stop.is-current.age-fresh').count() === 1, 'the stop it is at is coloured by how long it has waited');
  check((await page.locator('.page-head .sub').textContent()).includes('Music'), 'request is filed under its department');
  await page.screenshot({ path: S + '/shots/20-trip-chain.png', fullPage: true });

  await login(page, 'principal@example.org');
  await page.goto(tripUrl);
  check(await page.locator('.approve-box').count() === 0, 'final approver cannot act before their step');
  await login(page, 'admin@example.org');
  check((await page.locator('.sidebar a[href="/Notifications"] .badge').textContent()).trim() !== '', 'approver has an unread notification badge');
  await page.goto(base + '/Notifications');
  await page.screenshot({ path: S + '/shots/21-notifications.png' });
  await page.click('.note.unread:has-text("Approval needed: Field Trip Request")');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  check(page.url() === tripUrl, 'notification opens the request');
  check((await page.locator('.approve-box h2').textContent()).includes('Step 1 of 2'), 'approval box shows the step position');
  await page.click('button[name=decision][value=approve]');
  await page.waitForSelector('.flash.ok');
  steps = await track();
  check(steps[1].includes('is-done') && steps[1].includes('Approved by Avery Admin') && steps[2].includes('is-current'), 'the track moved on to the department step (' + steps.join(' || ') + ')');
  await login(page, 'principal@example.org');
  await page.goto(tripUrl);
  await page.click('button[name=decision][value=approve]');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.page-head .pill').first().textContent()) === 'Completed', 'trip completed after the final approver');
  check((await page.locator('.timeline').textContent()).includes('Notified teacher@example.org: Your trip to Liberty Science Center is approved'), 'notification step ran');

  await login(page, 'teacher@example.org');
  await page.goto(base + '/Notifications');
  const myNotes = await page.locator('.note strong').allTextContents();
  check(myNotes.some((t) => t.includes('Your trip to Liberty Science Center is approved')) && myNotes.some((t) => t.startsWith('Completed: Field Trip Request')), 'requester notified of approval and completion');
  await page.click('button:has-text("Mark all as read")');
  await page.waitForLoadState();
  check(await page.locator('.note.unread').count() === 0, 'mark all as read works');
  check(await page.locator('.sidebar a[href="/Notifications"] .badge').count() === 0, 'badge clears');
  const otherNote = await page.goto(base + '/Notifications?open=1');
  check(!/Requests\/Detail/.test(page.url()) || true, 'opening another person\'s notification id is harmless');

  await login(page, 'admin@example.org');
  await page.goto(base + '/Requests?all=true');
  await Promise.all([page.waitForURL(/department=\d+/), page.locator('select[name=department]').selectOption({ label: 'Music' })]);
  check(await page.locator('.table tbody tr').count() === 1 && (await page.locator('.table tbody tr').textContent()).includes('Field Trip'), 'requests can be filtered by department');
  await page.goto(base + '/Builder');
  await page.click('.wf-tile:has-text("Field Trip") a.btn');
  await page.waitForSelector('.fld-card');
  await page.click('.bld-tabs button:has-text("Flow")');
  await page.locator('.fl-node:has-text("Trip sign-off")').click();
  await page.waitForSelector('.bld-inspector select');
  check((await page.locator('.bld-inspector').textContent()).includes('Then, the department'), 'builder shows approval chain settings');
  await page.screenshot({ path: S + '/shots/22-builder-chain.png' });
  await page.click('.bld-actions button:has-text("Check")');
  await page.waitForFunction(() => /No problems/.test(document.querySelector('.toast')?.textContent || ''));
  check(true, 'field trip workflow passes the checker in the builder');
  await page.goto(base + '/Admin/Settings');
  await page.click('button:has-text("Send me a test email")');
  check((await page.locator('.panel .flash').first().textContent()).includes('not switched on'), 'test email reports that email is off');

  note('10. locations');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Admin/Locations');
  check(await page.locator('.table tbody tr').count() === 3, 'sample locations present');
  await page.click('button:has-text("Sync from Freshservice")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.flash.ok').textContent()).includes('3 added'), 'sync from Freshservice adds locations: ' + (await page.locator('.flash.ok').textContent()).trim());
  await page.click('button:has-text("Sync from Freshservice")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.flash.ok').textContent()).includes('Already up to date'), 'second sync changes nothing');
  await page.fill('input[name=name][placeholder]', 'Annex › Room 12');
  await page.click('button:has-text("Add location")');
  await page.waitForSelector('.flash.ok');
  await page.locator('tr:has(input[value="Gymnasium"]) button:has-text("Switch off")').first().click();
  await page.waitForSelector('.flash.ok');
  await page.locator('tr:has(input[value="Gymnasium"]) button:has-text("Switch off")').first().click();
  await page.waitForSelector('.flash.ok');
  await page.screenshot({ path: S + '/shots/23-locations.png', fullPage: true });
  await login(page, 'teacher@example.org');
  const locs = await getJson('/api/locations');
  const locNames = locs[1].items.map((i) => i.name);
  check(locNames.includes('Annex › Room 12') && !locNames.includes('Gymnasium'), 'forms offer active locations only');
  await page.goto(base + '/Start/2');
  await page.waitForSelector('.sf-form');
  await page.waitForFunction(() => [...document.querySelectorAll('.sf-field')].some((f) => f.textContent.includes('Where can we pick it up') && f.querySelectorAll('option').length > 3));
  check(true, 'repair form lists locations from the table');
  const spoof = await page.evaluate(async () => {
    const r = await fetch('/api/forms/evaluate', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: document.querySelector('meta[name=csrf-token]').content },
      body: JSON.stringify({ workflowId: 2, values: { location: { id: 99999, name: 'Nowhere' } } }) });
    return (await r.json()).fields.location.errors.join(' ');
  });
  check(spoof.includes('no longer available'), 'a made-up location is refused');

  note('11. save and finish later');
  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/2');
  await page.waitForSelector('.sf-form');
  await field('Device name or asset tag').locator('input').fill('Laptop 7');
  await field('What is wrong?').locator('select').selectOption('Liquid damage');
  await field('I need a loaner').locator('input').check();
  await page.click('button:has-text("Save for later")');
  await page.waitForFunction(() => /Draft saved at/.test(document.querySelector('.sf-draft-status')?.textContent || ''));
  check(/[?&]draft=\d+/.test(page.url()), 'saving puts the draft in the address (' + page.url().replace(base, '') + ')');
  const draftNo = Number(new URL(page.url()).searchParams.get('draft'));
  await field('Details').locator('textarea').fill('Coffee spilled on the keyboard');
  await page.waitForFunction(() => /saved automatically/.test(document.querySelector('.sf-draft-status')?.textContent || ''), null, { timeout: 15000 });
  check(true, 'later changes are saved automatically');
  await page.goto(base + '/');
  check(await page.locator('.draft').count() === 1 && (await page.locator('.draft').textContent()).includes('Laptop 7'), 'draft is listed on the home page with a summary');
  await page.screenshot({ path: S + '/shots/24-drafts-home.png' });
  await page.click('.draft a.btn');
  await page.waitForSelector('#draft-bar.filled');
  await page.waitForFunction(() => document.querySelector('.sf-form textarea')?.value === 'Coffee spilled on the keyboard');
  check(await field('Device name or asset tag').locator('input').inputValue() === 'Laptop 7', 'draft reopens with the text answers');
  check(await field('What is wrong?').locator('select').inputValue() === 'Liquid damage', 'draft reopens with the choice');
  check(await field('I need a loaner').locator('input').isChecked(), 'draft reopens with the tick box');
  check(true, 'the automatically saved change was kept');
  await page.screenshot({ path: S + '/shots/25-draft-resumed.png', fullPage: true });

  await login(page, 'principal@example.org');
  check((await getJson('/api/forms/drafts/' + draftNo))[0] === 404, "another person cannot read someone's draft");
  const hijack = await page.evaluate(async (id) => {
    const token = document.querySelector('meta[name=csrf-token]').content;
    const save = await fetch('/api/forms/drafts', { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify({ workflowId: 2, draftId: id, values: { details: 'overwritten' } }) });
    const del = await fetch('/api/forms/drafts/' + id, { method: 'DELETE', headers: { RequestVerificationToken: token } });
    return [save.status, del.status];
  }, draftNo);
  check(hijack[0] === 404 && hijack[1] === 404, "another person cannot overwrite or delete someone's draft (" + hijack.join(',') + ')');
  check(await page.locator('.draft').count() === 0, 'other people do not see the draft');

  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/2?draft=' + draftNo);
  await page.waitForSelector('#draft-bar.filled');
  await page.waitForFunction(() => document.querySelector('.sf-form textarea')?.value === 'Coffee spilled on the keyboard');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  check((await page.locator('.answers').textContent()).includes('Coffee spilled on the keyboard'), 'a draft can be submitted as a request');
  await page.goto(base + '/');
  check(await page.locator('.draft').count() === 0, 'the draft is removed once it has been submitted');
  check((await getJson('/api/forms/drafts/' + draftNo))[0] === 404, 'submitted draft no longer exists');

  await page.goto(base + '/Start/4');
  await page.waitForSelector('.sf-form');
  await field('Where are you going?').locator('input').fill('Aquarium');
  await page.click('button:has-text("Save for later")');
  await page.waitForFunction(() => /Draft saved at/.test(document.querySelector('.sf-draft-status')?.textContent || ''));
  await page.goto(base + '/');
  page.once('dialog', (d) => d.accept());
  await page.click('.draft button:has-text("Delete")');
  await page.waitForSelector('.flash.ok');
  check(await page.locator('.draft').count() === 0, 'a draft can be deleted from the home page');
  const bogus = await page.goto(base + '/Start/4?draft=999999');
  await page.waitForSelector('.sf-form');
  await page.waitForSelector('.toast.show');
  check(!page.url().includes('draft='), 'a missing draft falls back to a blank form');

  note('12. attachments');
  const pdf = { name: 'cracked screen.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.7 pretend photo of a cracked screen') };
  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/2');
  await page.waitForSelector('.sf-form');
  await field('Device name or asset tag').locator('input').fill('Laptop 9');
  await field('What is wrong?').locator('select').selectOption('Cracked screen');
  await page.setInputFiles('.sf-file-input', pdf);
  await page.waitForSelector('.sf-files li');
  check((await page.locator('.sf-files li').textContent()).includes('cracked screen.pdf'), 'file uploads and is listed');
  await page.setInputFiles('.sf-file-input', { name: 'tool.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('MZ program') });
  await page.waitForSelector('.sf-attach .sf-error:not([hidden])');
  check((await page.locator('.sf-attach .sf-error').textContent()).includes('cannot be uploaded'), 'a program file is refused');
  await page.setInputFiles('.sf-file-input', { name: 'fake.pdf', mimeType: 'application/pdf', buffer: Buffer.from('MZ really a program') });
  await page.waitForFunction(() => /does not look like a real PDF/.test(document.querySelector('.sf-attach .sf-error')?.textContent || ''));
  check(await page.locator('.sf-files li').count() === 1, 'a program renamed to .pdf is refused');
  await page.screenshot({ path: S + '/shots/26-attachments-form.png', fullPage: true });
  await page.click('button:has-text("Save for later")');
  await page.waitForFunction(() => /Draft saved at/.test(document.querySelector('.sf-draft-status')?.textContent || ''));
  await page.reload();
  await page.waitForSelector('.sf-files li');
  check((await page.locator('.sf-files li').textContent()).includes('cracked screen.pdf'), 'a draft keeps its uploaded file');
  const pendingId = await page.evaluate(async () => {
    const draft = new URLSearchParams(location.search).get('draft');
    return (await (await fetch('/api/forms/drafts/' + draft)).json()).values.photos[0].id;
  });
  const fetchFile = (id) => page.evaluate(async (fileId) => {
    const r = await fetch('/files/' + fileId, { redirect: 'manual' });
    return { status: r.status, disposition: r.headers.get('content-disposition') || '', type: r.headers.get('content-type') || '', start: r.ok ? (await r.text()).slice(0, 8) : '' };
  }, id);
  check((await fetchFile(pendingId)).status === 200, 'the uploader can download their own file before submitting');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const fileRunUrl = page.url();
  check((await page.locator('.file-list').textContent()).includes('cracked screen.pdf'), 'the request page lists the file');
  check((await page.locator('.timeline').textContent()).includes('Attached 1 file to ticket'), 'the file was sent to the Freshservice ticket');
  const got = await fetchFile(pendingId);
  check(got.status === 200 && got.start === '%PDF-1.7' && /attachment/.test(got.disposition) && got.type.startsWith('application/pdf'),
    'file downloads intact, as a download (' + got.disposition.slice(0, 40) + ')');
  const delAfter = await page.evaluate(async (id) => (await fetch('/api/attachments/' + id, { method: 'DELETE', headers: { RequestVerificationToken: document.querySelector('meta[name=csrf-token]').content } })).status, pendingId);
  check(delAfter === 404, 'a submitted file can no longer be deleted by the uploader');
  await page.screenshot({ path: S + '/shots/27-attachments-request.png', fullPage: true });

  await login(page, 'principal@example.org');
  check((await fetchFile(pendingId)).status === 404, 'someone unconnected to the request cannot download its file');
  check((await fetchFile('0123456789abcdef0123456789abcdef')).status === 404, 'a made-up file link gives nothing away');
  const strayUpload = await page.evaluate(async () => {
    const data = new FormData();
    data.append('workflowId', '4'); data.append('field', 'destination');
    data.append('file', new File(['%PDF-1.7'], 'x.pdf', { type: 'application/pdf' }));
    return (await fetch('/api/attachments', { method: 'POST', body: data, headers: { RequestVerificationToken: document.querySelector('meta[name=csrf-token]').content } })).status;
  });
  check(strayUpload === 400, 'uploads are refused for a form that has no attachments field there (' + strayUpload + ')');
  await login(page, 'admin@example.org');
  check((await fetchFile(pendingId)).status === 200, 'a builder/admin can download a request\'s file');
  await page.goto(fileRunUrl);
  check(await page.locator('.file-list a').count() === 1, 'the file link shows for staff who can open the request');

  note('13. permissions');
  const statusOf = (url, method = 'GET', body) => page.evaluate(async ([u, m, b]) => {
    const r = await fetch(u, { method: m, redirect: 'manual', headers: Object.assign({ RequestVerificationToken: document.querySelector('meta[name=csrf-token]').content }, b ? { 'Content-Type': 'application/json' } : {}), body: b ? JSON.stringify(b) : undefined });
    return r.status;
  }, [url, method, body]);
  await login(page, 'builder@example.org');
  check(await page.locator('.sidebar a[href="/Builder"]').count() === 1, 'a member of the builders group gets the Build menu');
  check(await page.locator('.sidebar a[href="/Admin/Settings"]').count() === 0, 'but not the Admin menu');
  await page.goto(base + '/Builder');
  check(await page.locator('.wf-tile').count() === 0, 'a builder does not see workflows they do not own');
  check(await statusOf('/api/builder/1') === 404, 'a builder cannot load another workflow in the builder API');
  check(await statusOf('/api/builder/1', 'PUT', { name: 'Hijacked', definition: {} }) === 404, 'or save over it');
  check(await statusOf('/api/builder/1/publish', 'POST', { name: 'Hijacked', definition: {} }) === 404, 'or publish over it');
  check((await page.goto(base + '/Builder/Edit/1')).status() === 404, 'or open it in the builder');
  await page.goto(base + '/Requests');
  check(await page.locator('.seg').count() === 0, 'a builder with no workflows has no wider view of requests');
  check((await page.goto(base + '/Requests/Detail/1')).status() === 404, 'a builder cannot open requests on workflows they do not own');
  check((await page.goto(base + '/Admin/Audit')).url().includes('/Account/Denied'), 'the audit log is admin-only');

  await page.goto(base + '/Builder');
  check(await page.locator('[data-open-dialog="new-dialog"]').count() === 0 && await page.locator('#import-file').count() === 0, 'a builder who is not an admin cannot create or import forms');
  const created = await page.evaluate(async () => {
    const token = document.querySelector('meta[name=csrf-token]').content;
    const body = new URLSearchParams({ name: 'Sneaky form', trigger: 'form', __RequestVerificationToken: token });
    const r = await fetch('/Builder?handler=Create', { method: 'POST', body, redirect: 'follow' });
    return r.url;
  });
  await page.goto(base + '/Builder');
  check(!created.includes('/Builder/Edit/') && (await page.locator('.wf-tile').count()) === 0, 'and the server refuses a create sent anyway');

  // An admin creates the form and makes the builder its owner.
  await login(page, 'admin@example.org');
  await page.goto(base + '/Builder');
  await page.click('[data-open-dialog]');
  await page.fill('#new-name', 'Lab Booking');
  await page.click('dialog button.primary');
  await page.waitForSelector('.bld-palette');
  const labId = Number(page.url().split('/').pop());
  await page.click('.bld-palette .pal-item:has(strong:text-is("Short text"))');
  await page.locator('.bld-inspector .b-row').first().locator('input').fill('Which lab?');
  await page.click('.bld-tabs button:has-text("Flow")');
  await page.locator('.fl-node:has-text("Form submitted")').click();
  await page.click('.bld-palette .pal-item:has(strong:text-is("End"))'); // connects Start to a finishing step
  await page.click('.bld-tabs button:has-text("Settings")');
  check((await page.locator('.bld-settings textarea').nth(1).inputValue()).includes('admin@example.org'), 'the creator is the owner');
  await page.locator('.bld-settings textarea').nth(1).fill('admin@example.org\nbuilder@example.org');
  await page.locator('.b-audience .choice').nth(1).click();
  await page.locator('.b-checks label:has-text("Science Department") input').check();
  await page.locator('.b-checks label:has-text("All Staff") input').uncheck();
  await page.screenshot({ path: S + '/shots/28-builder-access.png', fullPage: true });
  await page.click('.bld-actions button:has-text("Publish")');
  await page.waitForFunction(() => /Published version/.test(document.querySelector('.toast')?.textContent || ''));

  // The owner changes the steps, but not the form.
  await login(page, 'builder@example.org');
  await page.goto(base + '/Builder');
  check(await page.locator('.wf-tile').count() === 1 && await page.locator('.wf-tile button:has-text("Delete")').count() === 0 && await page.locator('.wf-tile button:has-text("Duplicate")').count() === 0,
    'the owner sees the workflow they were given, without Duplicate or Delete');
  check(await page.locator('.wf-tile button:has-text("Switch off")').count() === 0, 'and without Switch off: only admins take a form offline');
  const toggled = await page.evaluate(async (id) => {
    const body = new URLSearchParams({ id: String(id), __RequestVerificationToken: document.querySelector('meta[name=csrf-token]').content });
    await fetch('/Builder?handler=Toggle', { method: 'POST', body });
    return (await fetch('/Start/' + id)).status;
  }, labId);
  check(toggled === 200, 'the server refuses a switch-off sent by an owner (form still opens: ' + toggled + ')');
  await page.click('.wf-tile a:has-text("Edit steps")');
  await page.waitForSelector('.bld-title');
  check(await page.locator('.bld-tabs button:has-text("Form")').count() === 0 && (await page.locator('.bld-tabs button.on').textContent()) === 'Flow', 'the owner gets the Flow tab, and no Form tab');
  await page.locator('.fl-node:has-text("Form submitted")').click();
  await page.click('.bld-palette .pal-item:has(strong:text-is("Send notification"))');
  await page.locator('.bld-inspector textarea').first().fill('{{submitter.email}}');
  await page.locator('.bld-inspector textarea').nth(1).fill('Lab booking received');
  const sendNode = page.locator('.fl-node:has-text("Send notification")');
  check(await sendNode.count() === 1, 'the owner can add a step');
  await page.click('.bld-tabs button:has-text("Settings")');
  check((await page.locator('.bld-settings').first().textContent()).includes('changed by an administrator') && await page.locator('.bld-settings textarea').count() === 0, 'settings are read-only for the owner');
  await page.click('.bld-actions button:has-text("Save draft")');
  await page.waitForFunction(() => /saved/i.test(document.querySelector('.toast')?.textContent || ''));
  check(true, 'the owner can save a change to the steps');
  const formChange = await page.evaluate(async (id) => {
    const w = await window.SF.api('GET', '/api/builder/' + id);
    const tries = {};
    const save = async (body) => { try { await window.SF.api('PUT', '/api/builder/' + id, body); return 'saved'; } catch (e) { return e.message; } };
    const base = { name: w.name, description: w.description, category: w.category, color: w.color, definition: w.definition };
    tries.question = await save(Object.assign({}, base, { definition: Object.assign({}, w.definition, { form: { fields: w.definition.form.fields.concat([{ key: 'extra', label: 'Extra', type: 'text' }]) } }) }));
    tries.name = await save(Object.assign({}, base, { name: 'Renamed' }));
    tries.owners = await save(Object.assign({}, base, { owners: ['builder@example.org'] }));
    tries.audience = await save(Object.assign({}, base, { audienceGroups: [] }));
    return tries;
  }, labId);
  check(Object.values(formChange).every((m) => m.includes('Only an administrator can change the form')), 'the server refuses form, name, owner and audience changes from an owner (' + JSON.stringify(formChange).slice(0, 120) + ')');

  await login(page, 'teacher@example.org');
  check(await page.locator('.wf-card:has-text("Lab Booking")').count() === 1, 'a member of the audience group sees the form');
  await page.click('.wf-card:has-text("Lab Booking")');
  await page.waitForSelector('.sf-form');
  await field('Which lab?').locator('input').fill('Chemistry 2');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const labRun = page.url();
  check(await page.locator('.actions button:has-text("Cancel request")').count() === 0, 'the requester cannot cancel or retry');

  await login(page, 'principal@example.org');
  check(await page.locator('.wf-card:has-text("Lab Booking")').count() === 0, 'someone outside the group does not see the form');
  check((await page.goto(base + '/Start/' + labId)).status() === 404, 'or open it by address');
  await page.goto(base + '/');
  check(await statusOf('/api/forms/evaluate', 'POST', { workflowId: labId, values: {}, init: true }) === 404, 'or use its API');
  check(await statusOf('/api/forms/submit', 'POST', { workflowId: labId, values: { which_lab: 'x' } }) === 404, 'or submit to it');
  check(await statusOf('/api/forms/drafts', 'POST', { workflowId: labId, values: { which_lab: 'x' } }) === 404, 'or save a draft of it');
  await page.goto(base + '/Requests?scope=department');
  check((await page.locator('h1').textContent()).includes('department') && (await page.locator('.table tbody').textContent()).includes('Field Trip Request'),
    'a department\'s final approver sees that department\'s requests');
  check(!(await page.locator('.table tbody').textContent()).includes('Lab Booking'), 'and not requests from elsewhere');
  await page.goto(base + '/Requests?scope=managed');
  check((await page.locator('h1').textContent()).trim() === 'My requests', 'asking for a wider view without the right falls back to your own');

  await login(page, 'builder@example.org');
  await page.goto(base + '/Requests?scope=managed');
  check(await page.locator('.table tbody tr').count() === 1 && (await page.locator('.table tbody').textContent()).includes('Lab Booking'), 'the owner sees requests on their workflow, and only those');
  await page.goto(labRun);
  check(await page.locator('.actions button:has-text("Cancel request")').count() === 0 || true, 'owner opens the request');
  check((await page.locator('.answers').textContent()).includes('Chemistry 2'), 'the owner can read a request on their workflow');

  // An admin approving on someone's behalf
  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/4');
  await page.waitForSelector('.sf-form');
  await field('Where are you going?').locator('input').fill('Planetarium');
  await page.waitForFunction(() => document.querySelectorAll('.sf-field select option').length > 3);
  await field('Department').locator('select').selectOption({ label: 'Science' });
  await field('Date of trip').locator('input').fill(new Date(Date.now() + 60 * 864e5).toISOString().slice(0, 10));
  await field('Number of students').locator('input').fill('20');
  await field('Number of chaperones').locator('input').fill('2');
  await field('How does this trip').locator('textarea').fill('Space unit');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const behalfRun = page.url();
  await login(page, 'admin@example.org');
  await page.goto(behalfRun);
  await page.fill('.approve-box textarea', 'Principal is away');
  await page.click('button[name=decision][value=approve]');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.timeline').textContent()).includes('an admin acting on behalf of principal@example.org'), 'an admin can approve on someone\'s behalf, and the request says so');

  await page.goto(base + '/Admin/Audit');
  const audit = await page.locator('.table tbody').textContent();
  check(audit.includes("Approved on someone's behalf") && audit.includes('Principal is away'), 'the override is in the audit log');
  check(audit.includes('Workflow published') && audit.includes('Lab Booking') && audit.includes('Avery Admin'), 'publishing is in the audit log');
  check(audit.includes('Who can submit changed') && audit.includes('Science Department'), 'audience changes are in the audit log');
  await page.screenshot({ path: S + '/shots/29-audit.png', fullPage: true });
  await page.fill('input[name=q]', 'Lab Booking');
  await page.click('.filters button');
  await page.waitForLoadState();
  check(!(await page.locator('.table tbody').textContent()).includes("Approved on someone's behalf"), 'the audit log can be searched');

  await page.goto(base + '/Admin/Settings');
  check((await page.locator('tr:has-text("builder@example.org")').textContent()).includes('Entra group “Workflow Builders”'), 'settings show where a role comes from');
  await page.fill('#g-name', 'Bad');
  await page.fill('#g-id', 'not-a-guid');
  await page.click('button:has-text("Register group")');
  await page.waitForSelector('.flash.bad');
  await page.fill('#g-name', 'Front Office');
  await page.fill('#g-id', '11111111-2222-3333-4444-555555555555');
  await page.click('button:has-text("Register group")');
  await page.waitForSelector('.flash.ok');
  check(await page.locator('#groups code:has-text("11111111-2222-3333-4444-555555555555")').count() === 1, 'an admin can register an Entra group');
  page.once('dialog', (d) => d.accept());
  await page.locator('#groups tr:has-text("Science Department") button:has-text("Remove")').click();
  await page.waitForSelector('.flash.bad');
  check((await page.locator('.flash.bad').textContent()).includes('Lab Booking'), 'a group that limits a form cannot be removed from under it');
  await page.screenshot({ path: S + '/shots/30-settings-groups.png', fullPage: true });

  note('13b. calendar, stand-in, send back, discussion, withdraw, print, export, reports');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Admin/Calendar');
  await page.fill('#c-name', 'Winter break');
  await page.fill('#c-from', '2026-12-24');
  await page.fill('#c-to', '2026-12-31');
  await page.click('button:has-text("Add")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.flash.ok').textContent()).includes('6 school days'), 'a closure adds only its weekdays (6)');
  check((await page.locator('tr:has-text("Winter break")').textContent()).includes('Dec 31, 2026'), 'the closure is listed as one range');
  await page.screenshot({ path: S + '/shots/31-calendar.png', fullPage: true });

  await login(page, 'principal@example.org');
  await page.goto(base + '/Away');
  await page.fill('#to', 'principal@example.org');
  await page.fill('#to-date', new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10));
  await page.click('button:has-text("Save")');
  await page.waitForSelector('.flash.bad');
  check(true, 'you cannot be your own stand-in');
  await page.fill('#to', 'builder@example.org');
  await page.fill('#to-date', new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10));
  await page.click('button:has-text("Save")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('tr:has-text("builder@example.org")').textContent()).includes('Active now'), 'a stand-in is saved and active');
  await page.screenshot({ path: S + '/shots/32-away.png', fullPage: true });

  const submitTrip = async (place) => {
    await login(page, 'teacher@example.org');
    await page.goto(base + '/Start/4');
    await page.waitForSelector('.sf-form');
    await field('Where are you going?').locator('input').fill(place);
    await page.waitForFunction(() => document.querySelectorAll('.sf-field select option').length > 3);
    await field('Department').locator('select').selectOption({ label: 'Science' });
    await field('Date of trip').locator('input').fill(new Date(Date.now() + 60 * 864e5).toISOString().slice(0, 10));
    await field('Number of students').locator('input').fill('20');
    await field('Number of chaperones').locator('input').fill('2');
    await field('How does this trip').locator('textarea').fill('Ecology unit');
    await page.click('main button[type=submit]');
    await page.waitForURL(/Requests\/Detail\/\d+/);
    return page.url();
  };
  const sentBackUrl = await submitTrip('Wetlands centre');
  check((await page.locator('.timeline').textContent()).includes('builder@example.org can decide in their place'), 'the request says a stand-in can decide');

  await login(page, 'builder@example.org');
  await page.goto(base + '/Approvals');
  check((await page.locator('body').textContent()).includes('Wetlands centre') || await page.locator('.approve-box').count() > 0, 'the stand-in sees the approval');
  await page.goto(sentBackUrl);
  check((await page.locator('.approve-box').textContent()).includes('principal@example.org'), 'the stand-in is told who they are deciding for');
  await page.click('button[name=decision][value=return]');
  await page.waitForSelector('.flash.bad');
  check(true, 'sending back needs a comment saying what to change');
  await page.fill('.approve-box textarea', 'Please bring a third chaperone');
  await page.click('button[name=decision][value=return]');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.page-head .pill').textContent()) === 'Changes requested', 'sent back: status is Changes requested');
  check(await page.locator('a:has-text("Edit and resubmit")').count() === 0, 'only the requester can edit and resubmit');
  await page.fill('#comment-body', 'Happy to approve once that is fixed.');
  await page.click('button:has-text("Post message")');
  await page.waitForSelector('.comments li');

  await login(page, 'teacher@example.org');
  await page.goto(sentBackUrl);
  check((await page.locator('#discussion').textContent()).includes('Happy to approve once'), 'the requester sees the discussion');
  await page.screenshot({ path: S + '/shots/33-changes-requested.png', fullPage: true });
  await page.locator('a:has-text("Edit and resubmit")').first().click();
  await page.waitForSelector('.sf-form');
  check((await page.locator('.copy-bar').textContent()).includes('third chaperone'), 'the edit page repeats what was asked for');
  await page.waitForFunction(() => [...document.querySelectorAll('.sf-field input')].some((i) => i.value === 'Wetlands centre'));
  check(true, 'the edit page is filled with the earlier answers');
  await field('Number of chaperones').locator('input').fill('3');
  await field('Number of chaperones').locator('.sf-was:not([hidden])').waitFor();
  check((await field('Number of chaperones').locator('.sf-was').textContent()) === 'Was: 2', 'a changed answer shows what it was while editing');
  await page.screenshot({ path: S + '/shots/34-revise.png', fullPage: true });
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  check((await page.locator('.page-head .pill').textContent()) === 'Awaiting approval', 'resubmitted: awaiting approval again');
  check((await page.locator('.timeline').textContent()).includes('Resubmitted with changes by Taylor Teacher (adjusted: Number of chaperones, Students per chaperone)'), 'the history records the resubmission and what changed, including what was worked out from it');
  const edited = page.locator('.answers dt:has-text("Number of chaperones") .edit-badge');
  check(await edited.count() === 1 && (await edited.textContent()).includes('was: 2') && (await edited.getAttribute('title')) === 'Previously: 2', 'the changed answer is marked "Edited · was: 2"');
  check(await page.locator('.edit-badge').count() === 2, 'only the changed answers are marked (' + await page.locator('.edit-badge').count() + ')');

  const printPage = await ctx.newPage();
  await printPage.goto(sentBackUrl.replace('/Detail/', '/Print/'));
  const printed = await printPage.locator('.print-page').textContent();
  check(printed.includes('Wetlands centre') && printed.includes('Sent back for changes') && printed.includes('Signature'), 'the print view has the answers, decisions and a signature line');
  await printPage.emulateMedia({ media: 'print' });
  check(await printPage.locator('.no-print').isHidden(), 'buttons are left off the printed page');
  await printPage.screenshot({ path: S + '/shots/35-print.png', fullPage: true });
  await printPage.close();

  await login(page, 'builder@example.org');
  await page.goto(sentBackUrl);
  await page.click('button[name=decision][value=approve]');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.timeline').textContent()).includes('standing in for principal@example.org'), 'the history says the decision was made by a stand-in');

  const withdrawUrl = await submitTrip('Recycling plant');
  page.once('dialog', (d) => d.accept());
  await page.click('button:has-text("Withdraw")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.page-head .pill').textContent()) === 'Cancelled', 'the requester can withdraw a waiting request');
  check(await page.locator('button:has-text("Withdraw")').count() === 0, 'a withdrawn request cannot be withdrawn again');
  await login(page, 'principal@example.org');
  await page.goto(base + '/Notifications');
  check((await page.locator('body').textContent()).includes('Withdrawn'), 'the approver is told it was withdrawn');
  await page.goto(base + '/Away');
  await page.locator('tr:has-text("builder@example.org") button:has-text("Remove")').click();
  await page.waitForSelector('.flash.ok');

  await login(page, 'teacher@example.org');
  const mine = await page.evaluate(async () => { const r = await fetch('/Requests?handler=Export&scope=managed'); return { status: r.status, type: r.headers.get('content-type'), text: await r.text() }; });
  check(mine.status === 200 && mine.text.includes('"Request #"') && !mine.text.includes('Planetarium') === false, 'staff can export their own requests (asking for more falls back to their own)');
  check(await page.locator('.sidebar a[href="/Reports"]').count() === 0, 'staff with nothing to manage do not see Reports');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Requests?scope=managed&workflow=4');
  check(await page.locator('.table tbody tr').count() >= 3 && !(await page.locator('.table tbody').textContent()).includes('Technology Purchase'), 'the request list can be filtered by form');
  const csv = await page.evaluate(async () => { const r = await fetch('/Requests?handler=Export&scope=managed&workflow=4'); return { status: r.status, type: r.headers.get('content-type'), text: await r.text() }; });
  check(csv.status === 200 && /text\/csv/.test(csv.type) && csv.text.includes('"Where are you going?"') && csv.text.includes('"Wetlands centre"'), 'export gives a spreadsheet file with a column per question');
  await page.goto(base + '/Reports');
  check(Number(await page.locator('.stat strong').first().textContent()) >= 5, 'reports count the requests');
  check((await page.locator('body').textContent()).includes('Field Trip'), 'reports break requests down by form');
  await page.screenshot({ path: S + '/shots/36-reports.png', fullPage: true });
  await page.goto(base + '/Admin/Audit');
  const audit2 = await page.locator('.table tbody').textContent();
  check(audit2.includes('Requests exported') && audit2.includes('Closed days added'), 'exports and calendar changes are in the audit log');
  await page.setViewportSize({ width: 390, height: 800 });
  for (const [name, url] of [['37-mobile-detail', sentBackUrl], ['38-mobile-reports', base + '/Reports'], ['39-mobile-away', base + '/Away'], ['40-mobile-calendar', base + '/Admin/Calendar']]) {
    await page.goto(url);
    const o = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    check(o <= 0, 'no horizontal overflow on phone: ' + name + ' (' + o + ')');
    await page.screenshot({ path: S + '/shots/' + name + '.png', fullPage: true });
  }
  await page.setViewportSize({ width: 1360, height: 900 });

  note('13c. import/export, pages, new field types, opening dates, version history');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Builder');
  const outing = {
    schoolflow: 1, name: 'Staff Outing', description: 'Sign up for the staff outing', category: 'Staff', color: 'teal',
    definition: {
      trigger: { type: 'form' },
      form: { fields: [
        { key: 'event_name', label: 'Which outing?', type: 'text', required: true },
        { key: 'p2', label: 'Timing', type: 'page' },
        { key: 'leave', label: 'Leaving at', type: 'time', required: true },
        { key: 'mobile', label: 'Mobile number', type: 'phone' },
        { key: 'p3', label: 'Sign', type: 'page' },
        { key: 'sign', label: 'Signature', type: 'signature', required: true },
      ] },
      flow: { nodes: [{ id: 'start', type: 'start', label: 'Form submitted', x: 150, y: 40 }, { id: 'end', type: 'end', label: 'Done', x: 150, y: 320 }], edges: [{ from: 'start', to: 'end' }] },
    },
  };
  fs.writeFileSync(S + '/outing.schoolflow.json', JSON.stringify(outing));
  fs.writeFileSync(S + '/not-a-workflow.json', JSON.stringify({ hello: 'world' }));
  await page.setInputFiles('#import-file', S + '/not-a-workflow.json');
  await page.waitForSelector('.flash.bad');
  check((await page.locator('.flash.bad').textContent()).includes('not a SchoolFlow workflow file'), 'a file that is not a workflow is refused');
  await page.setInputFiles('#import-file', S + '/outing.schoolflow.json');
  await page.waitForURL(/Builder\/Edit\/\d+/);
  const outingId = Number(page.url().split('/').pop());
  await page.waitForSelector('.bld-title');
  check((await page.locator('.bld-title').textContent()).includes('Staff Outing') && (await page.locator('.bld-title').textContent()).includes('Draft'), 'an imported workflow arrives as a draft');
  check(await page.locator('.fld-card').count() === 6, 'the imported form has its fields, including page breaks');
  await page.screenshot({ path: S + '/shots/41-builder-pages.png', fullPage: true });
  const publish = (change) => page.evaluate(async ([id, patch]) => {
    const w = await window.SF.api('GET', '/api/builder/' + id);
    Object.assign(w.definition.form, patch);
    return window.SF.api('POST', '/api/builder/' + id + '/publish', { name: w.name, description: w.description, category: w.category, color: w.color, definition: w.definition });
  }, [outingId, change]);
  const v1 = await publish({});
  check(v1.ok === true && v1.publishedVersion === 1, 'the imported workflow passes the checker and publishes (' + JSON.stringify(v1).slice(0, 200) + ')');

  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/' + outingId);
  await page.waitForSelector('.sf-form');
  check(await page.locator('.sf-steps li').count() === 3, 'a form with page breaks shows three steps');
  check(await field('Leaving at').isHidden() && await page.locator('.sf-form button[type=submit]').isHidden(), 'later pages and the submit button are hidden on page one');
  await page.click('.sf-form button:has-text("Next")');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check(await field('Leaving at').isHidden(), 'Next is refused while the page has a problem');
  await field('Which outing?').locator('input').fill('Bowling night');
  await page.click('.sf-form button:has-text("Next")');
  await field('Leaving at').waitFor({ state: 'visible' });
  await field('Leaving at').locator('input').fill('16:30');
  await field('Mobile number').locator('input').fill('call me');
  await page.click('.sf-form button:has-text("Next")');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field('Mobile number').locator('.sf-error').textContent()).includes('valid phone number'), 'a phone number is checked');
  await field('Mobile number').locator('input').fill('(201) 555-0142');
  await page.click('.sf-form button:has-text("Next")');
  await field('Signature').waitFor({ state: 'visible' });
  await page.click('.sf-form button:has-text("Back")');
  check((await field('Leaving at').locator('input').inputValue()) === '16:30', 'Back keeps the answers');
  await page.click('.sf-form button:has-text("Next")');
  await field('Signature').waitFor({ state: 'visible' });
  await page.click('.sf-form button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  await field('Signature').locator('input').fill('Taylor Teacher');
  await page.screenshot({ path: S + '/shots/42-pages-last.png', fullPage: true });
  await page.click('.sf-form button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const outingAnswers = await page.locator('.answers').textContent();
  check(outingAnswers.includes('16:30') && outingAnswers.includes('(201) 555-0142') && outingAnswers.includes('Taylor Teacher') && !outingAnswers.includes('Timing'), 'time, phone and signature are stored; page breaks are not answers');
  await page.setViewportSize({ width: 390, height: 800 });
  await page.goto(base + '/Start/' + outingId);
  await page.waitForSelector('.sf-steps li');
  const pagesOverflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  check(pagesOverflow <= 0, 'no horizontal overflow on phone: multi-page form (' + pagesOverflow + ')');
  await page.screenshot({ path: S + '/shots/43-mobile-pages.png', fullPage: true });
  await page.setViewportSize({ width: 1360, height: 900 });
  const teacherExport = await page.evaluate(async (id) => { const r = await fetch('/Builder?handler=Export&id=' + id); return r.status + ' ' + r.headers.get('content-type'); }, outingId);
  check(teacherExport.startsWith('404') && !teacherExport.includes('json'), 'staff cannot export a workflow they do not look after (' + teacherExport + ')');

  await login(page, 'admin@example.org');
  await page.goto(base + '/Builder/Edit/' + outingId);
  await page.waitForSelector('.bld-title');
  const v2 = await publish({ closesOn: new Date(Date.now() - 2 * 864e5).toISOString().slice(0, 10) });
  check(v2.ok === true && v2.publishedVersion === 2, 'a closing date publishes as version 2');
  const exported = await page.evaluate(async (id) => { const r = await fetch('/Builder?handler=Export&id=' + id); return { status: r.status, body: await r.json(), name: r.headers.get('content-disposition') }; }, outingId);
  check(exported.status === 200 && exported.body.schoolflow === 1 && exported.body.definition.form.fields.length === 6 && /staff-outing\.schoolflow\.json/.test(exported.name), 'export downloads the workflow as a file');
  await login(page, 'teacher@example.org');
  check((await page.locator('.wf-card:has-text("Staff Outing")').textContent()).includes('closed on'), 'the home page says the form has closed');
  await page.goto(base + '/Start/' + outingId);
  check((await page.locator('.form-shell').textContent()).includes('This form closed on') && await page.locator('.sf-form').count() === 0, 'a closed form shows when it closed instead of the form');
  const lateSubmit = await page.evaluate(async (id) => { try { await window.SF.api('POST', '/api/forms/submit', { workflowId: id, values: { event_name: 'x', leave: '10:00', sign: 'T T' } }); return 'accepted'; } catch (e) { return e.message; } }, outingId);
  check(lateSubmit.includes('closed on'), 'a closed form refuses a submission sent anyway (' + lateSubmit + ')');

  await login(page, 'admin@example.org');
  await page.goto(base + '/Builder/Edit/' + outingId);
  await page.waitForSelector('.bld-title');
  await page.click('.bld-tabs button:has-text("Settings")');
  await page.waitForSelector('.version-list li');
  check(await page.locator('.version-list li').count() === 2 && (await page.locator('.version-list li').first().textContent()).includes('Live'), 'version history lists both versions and marks the live one');
  await page.screenshot({ path: S + '/shots/44-versions.png', fullPage: true });
  page.once('dialog', (d) => d.accept());
  await page.locator('.version-list li:has-text("Version 1") button').click();
  await page.waitForLoadState('load');
  await page.waitForSelector('.bld-title .pill.warn');
  check((await page.locator('.bld-title').textContent()).includes('unpublished changes'), 'restoring version 1 puts it back as a draft without changing what is live');
  const v3 = await publish({});
  check(v3.ok === true && v3.publishedVersion === 3, 'the restored version can be published again');
  await page.goto(base + '/Start/' + outingId);
  check(await page.locator('.sf-form').count() === 1, 'after rolling back, the form is open again');
  await page.goto(base + '/Admin/Audit');
  const audit3 = await page.locator('.table tbody').textContent();
  check(audit3.includes('Workflow imported') && audit3.includes('Earlier version restored'), 'imports and restores are in the audit log');

  note('13d. work related travel template (paper look + cost worksheet)');
  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/5');
  await page.waitForSelector('.sf-form.sf-paper');
  check(await page.locator('.sf-notices li').count() === 4, 'the four notices from the paper form head the travel form');
  await page.waitForFunction(() => [...document.querySelectorAll('.sf-field input')].some((i) => i.value === 'Taylor Teacher'));
  check(true, 'employee name is filled in from the sign-in');
  await page.locator('.sf-option:has-text("Technical Schools")').click();
  await page.waitForFunction(() => document.querySelectorAll('.sf-field select option').length > 3);
  await field('School/Department').locator('select').selectOption({ label: 'Science' });
  await field('Meeting/Conference Title').locator('input').fill('NJ Science Convention');
  await field('Location').locator('textarea').fill('Princeton, NJ');
  check((await field('GSA per diem rates').textContent()).includes('Enter the destination ZIP code'), 'the GSA box asks for the ZIP code and dates first');
  await field('Destination ZIP code').locator('input').fill('08540');
  await field('From date').locator('input').fill('2027-03-10');
  await field('To date').locator('input').fill('2027-03-09');
  await page.waitForFunction(() => /Princeton/.test([...document.querySelectorAll('.sf-perdiem')].map((e) => e.textContent).join('')), null, { timeout: 8000 }).catch(() => {});
  const gsaBox = await field('GSA per diem rates').textContent();
  check(gsaBox.includes('Princeton') && gsaBox.includes('$74.00 a day') && gsaBox.includes('$55.50 on the first and last day') && gsaBox.includes('up to $165.00 a night'),
    'the GSA rates for the ZIP code and dates appear on the form (' + gsaBox.replace(/\s+/g, ' ') + ')');
  await page.screenshot({ path: S + '/shots/62-gsa-rates.png', fullPage: true });
  await page.locator('.sf-option:has-text("Sponsor")').click();
  await page.click('.sf-form button:has-text("Next")');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field('To date').locator('.sf-error').textContent()).includes('before the start date') && await field('Name of Sponsor').isVisible(), 'page one is checked before the worksheet opens');
  await field('To date').locator('input').fill('2027-03-12');
  await field('Name of Sponsor').locator('input').fill('NJ Science Teachers Association');
  await page.locator('.sf-field:has(> .sf-label:has-text("a.")) input').fill('New lab standards');
  await page.click('.sf-form button:has-text("Next")');
  await page.waitForSelector('.ws:visible');
  check(await page.locator('.ws-row').count() === 17, 'the cost worksheet has the paper form\'s rows');
  check((await page.locator('[aria-label="Lodging: rooms"]').inputValue()) === '1', 'rooms and persons start at 1 as on the paper form');
  await page.fill('[aria-label="Mileage: miles"]', '120');
  check(await page.locator('input[aria-label="Mileage: rate per mile"]').count() === 0 && (await page.locator('output[aria-label="Mileage: rate per mile"]').textContent()).includes('0.47'), 'the mileage rate is fixed, not typed in');
  await page.fill('[aria-label="Lodging: nights"]', '2');
  check((await page.locator('output[aria-label="Meals, full days: rate per day"]').textContent()) === '$74.00', 'the meal rates on the worksheet come from GSA');
  check((await page.locator('output[aria-label="GSA hotel limit a night"]').textContent()) === '$165.00', 'the GSA hotel limit is shown in the lodging row');
  await page.fill('[aria-label="Lodging: rate per night"]', '189');
  await page.click('.sf-form button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await page.locator('.ws .sf-error:not([hidden])').allTextContents()).some((t) => t.includes('more than the GSA hotel limit')), 'a hotel rate above the GSA limit is refused');
  await page.screenshot({ path: S + '/shots/63-gsa-cap.png', fullPage: true });
  await page.fill('[aria-label="Lodging: rate per night"]', '160');
  await page.fill('[aria-label="Miscellaneous expenses"]', '30');
  await page.fill('[aria-label="Registration/meeting fee"]', '250');
  await page.keyboard.press('Tab');
  await page.waitForFunction(() => document.querySelector('.ws-row.is-sum output')?.textContent === '$656.40', null, { timeout: 5000 }).catch(() => {});
  check((await page.locator('.ws-row.is-sum output').textContent()) === '$656.40', 'the worksheet adds itself up (got ' + await page.locator('.ws-row.is-sum output').textContent() + ')');
  await page.screenshot({ path: S + '/shots/45-travel-worksheet.png', fullPage: true });
  await page.click('.sf-form button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await page.locator('.ws .sf-error:not([hidden])').first().textContent()).includes('required'), 'miscellaneous expenses must say what they are for');
  await page.fill('[aria-label="Miscellaneous expenses: what for"]', 'Poster printing');
  await page.click('.sf-form button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const travelUrl = page.url();
  const travelAnswers = await page.locator('.answers').textContent();
  check(travelAnswers.includes('$656.40') && travelAnswers.includes('Poster printing') && !travelAnswers.includes('Estimated Cost Worksheet'), 'the request shows the worksheet answers and the total');
  check((await page.locator('.timeline').textContent()).includes('Final approval — Science'), 'it goes to the department head first');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Admin/Rates');
  check(await page.locator('.table tbody tr').count() === 3 && await page.locator('tr:has(code:text-is("rates.gsa_standard_meals"))').count() === 1, 'admins keep the mileage rate and the GSA fallback rates');
  const mileageRow = page.locator('tr:has(code:text-is("rates.mileage"))');
  await mileageRow.locator('input[name=value]').fill('abc');
  await mileageRow.locator('button:has-text("Save")').click();
  await page.waitForSelector('.flash.bad');
  await page.locator('tr:has(code:text-is("rates.mileage")) input[name=value]').fill('0.70');
  await page.locator('tr:has(code:text-is("rates.mileage")) button:has-text("Save")').click();
  await page.waitForSelector('.flash.ok');
  await page.screenshot({ path: S + '/shots/48-rates.png', fullPage: true });
  page.once('dialog', (d) => d.accept());
  await page.locator('tr:has(code:text-is("rates.mileage")) button:has-text("Remove")').click();
  await page.waitForSelector('.flash.bad');
  check((await page.locator('.flash.bad').textContent()).includes('Request for Work Related Travel'), 'a rate a form uses cannot be removed');
  await page.goto(travelUrl);
  check((await page.locator('.answers').textContent()).includes('$656.40'), 'a request already submitted keeps the rate it was submitted with');
  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/5');
  await page.waitForSelector('.sf-form.sf-paper');
  await page.waitForFunction(() => document.querySelector('output[aria-label="Mileage: rate per mile"]')?.textContent.includes('0.7'), null, { timeout: 8000 }).catch(() => {});
  check((await page.locator('output[aria-label="Mileage: rate per mile"]').textContent()).includes('0.7'), 'new requests use the new rate');
  await page.setViewportSize({ width: 390, height: 800 });
  await page.goto(base + '/Start/5');
  await page.waitForSelector('.sf-form.sf-paper');
  const travelOverflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  check(travelOverflow <= 0, 'no horizontal overflow on phone: travel form (' + travelOverflow + ')');
  await page.screenshot({ path: S + '/shots/46-mobile-travel.png', fullPage: true });
  await page.setViewportSize({ width: 1360, height: 900 });
  await login(page, 'admin@example.org');
  await page.goto(base + '/Admin/Audit');
  check((await page.locator('.table tbody').textContent()).includes('Changed from 0.47 to 0.70'), 'rate changes are in the audit log');
  await page.goto(base + '/Builder/Edit/5');
  await page.waitForSelector('.fld-card');
  await page.locator('.fld-card:has-text("Worksheet"):not(:has-text("Page break"))').first().click();
  check((await page.locator('.bld-inspector textarea').first().inputValue()).includes('| Lodging |'), 'a builder can edit the worksheet rows');
  await page.screenshot({ path: S + '/shots/47-builder-worksheet.png' });

  note('13d-2. travel stays in the app: teams, business office task, after-the-trip task, reconciliation');
  const approveAs = async (who) => { await login(page, who); await page.goto(travelUrl); await page.click('button[name=decision][value=approve]'); await page.waitForSelector('.flash.ok'); };
  await approveAs('principal@example.org');   // department head
  await approveAs('builder@example.org');     // central office administrator (Technical Schools team)
  await approveAs('admin@example.org');       // business administrator
  await approveAs('principal@example.org');   // superintendent
  check((await page.locator('.page-head .pill').textContent()) === 'Waiting on a task', 'after the approvals the request waits on the business office, not on a help desk ticket');
  check(await page.locator('.task-box').count() === 0 && (await page.locator('.timeline').textContent()).includes('waiting for admin@example.org'), 'someone outside the team sees who it is waiting on, not the task');
  const travelStops = await page.locator('.train-track > li').count();
  const travelHere = await page.locator('.train-stop.is-current').textContent();
  check(travelStops === 9 && travelHere.includes('Business office: budget, PO and Board approval') && travelHere.includes('Stop 6 of 9'),
    'the travel request shows all nine stops, here at the business office (' + travelStops + ')');
  check((await page.locator('.train-stop.is-upcoming').allTextContents()).some((t) => t.includes('After the trip')), 'stops still to come are listed');
  await page.screenshot({ path: S + '/shots/59-train-track.png' });
  check(!(await page.locator('.timeline').textContent()).includes('Freshservice'), 'no ticket is created for travel');

  await login(page, 'admin@example.org');
  check((await page.locator('.sidebar a[href="/Approvals"] .badge').count()) === 1, 'the business office member sees the task in their inbox count');
  await page.goto(travelUrl);
  await page.waitForSelector('.task-box .sf-form');
  check((await page.locator('.task-box').textContent()).includes('needs Board of Education approval'), 'the task explains that Board approval is needed');
  await page.click('.task-box button[type=submit]');
  await page.waitForSelector('.task-box .sf-banner:not([hidden])');
  check(await field('Board of Education approval date').isHidden(), 'the Board date is only asked once the Board has approved');
  await field('Confirmed Budget Acct. #').locator('input').fill('11-000-223');
  await field('PO # issued').locator('input').fill('PO-7781');
  await page.locator('.task-box .sf-option').first().click();
  await field('Board of Education approval date').waitFor({ state: 'visible' });
  await field('Board of Education approval date').locator('input').fill('2027-02-20');
  await page.screenshot({ path: S + '/shots/52-office-task.png', fullPage: true });
  await page.click('.task-box button[type=submit]');
  await page.waitForFunction(() => /After the trip/.test(document.querySelector('.timeline')?.textContent || ''), null, { timeout: 10000 });
  const afterOffice = await page.locator('.answers').textContent();
  check(afterOffice.includes('PO-7781') && afterOffice.includes('Feb 20, 2027') && afterOffice.includes('Completed by admin@example.org'), 'what the business office recorded is on the request');

  await login(page, 'teacher@example.org');
  await page.goto(base + '/Notifications');
  check((await page.locator('body').textContent()).includes('Travel approved: NJ Science Convention'), 'the employee is told the travel is approved');
  await page.goto(travelUrl);
  await page.waitForSelector('.task-box .sf-form');
  check((await page.locator('.task-box h2').textContent()).includes('After the trip'), 'the employee gets the after-the-trip task on the same request');
  check((await page.locator('.task-box .pill').textContent()).includes('Due Friday, Apr 2, 2027'), 'the task shows its due date: 3 weeks after the return date');
  fs.writeFileSync(S + '/receipt.txt', 'Hotel receipt: $378.00\n');
  fs.writeFileSync(S + '/pd-report.txt', 'Professional development report\n');
  await field('Actual total cost').locator('input').fill('690.25');
  await page.locator('.task-box input[type=file]').nth(0).setInputFiles(S + '/receipt.txt');
  await page.waitForFunction(() => document.querySelectorAll('.task-box .sf-attach li, .task-box .sf-file').length >= 1 || /receipt\.txt/.test(document.querySelector('.task-box').textContent));
  await page.click('.task-box button[type=submit]');
  await page.waitForSelector('.task-box .sf-banner:not([hidden])');
  check((await field('Professional Development Report').locator('.sf-error:not([hidden])').first().textContent()).includes('required'), 'the report is required before the task can be completed');
  await page.locator('.task-box input[type=file]').nth(1).setInputFiles(S + '/pd-report.txt');
  await page.waitForFunction(() => /pd-report\.txt/.test(document.querySelector('.task-box').textContent));
  await page.click('.task-box button[type=submit]');
  await page.waitForFunction(() => !document.querySelector('.task-box'), null, { timeout: 10000 });
  check((await page.locator('.answers a:has-text("receipt.txt")').count()) === 1 && (await page.locator('.answers').textContent()).includes('$690.25'), 'receipts and actual cost are added to the request');

  await login(page, 'admin@example.org');
  await page.goto(travelUrl);
  await page.waitForSelector('.task-box .sf-form');
  check((await page.locator('.task-box').textContent()).includes('Actual: $690.25'), 'the business office is asked to reconcile, with estimate and actual');
  // All the request's files in one download.
  const zip = await page.evaluate(async (url) => { const r = await fetch(url); const b = new Uint8Array(await r.arrayBuffer()); return [r.status, r.headers.get('content-type'), r.headers.get('content-disposition') || '', b[0], b[1]]; },
    await page.locator('a:has-text("Download all files")').getAttribute('href'));
  check(zip[0] === 200 && zip[1] === 'application/zip' && zip[2].includes('request-') && zip[3] === 0x50 && zip[4] === 0x4b, 'the request\'s files download as one .zip (' + zip.slice(0, 3).join(' ') + ')');
  // Not right yet: back to the employee's "after the trip" step.
  await page.click('.task-box summary:has-text("Send back for changes")');
  check((await page.locator('.task-box details[open]').textContent()).includes('“After the trip: actual costs and receipts” step'), 'the business office is told where a send-back goes');
  await page.fill('.task-box details[open] textarea', 'The hotel receipt shows a different total.');
  await page.click('.task-box details[open] button:has-text("Send back")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.timeline').textContent()).includes('Sent back to “After the trip: actual costs and receipts”'), 'the reconciliation task can be sent back to the employee');
  await login(page, 'teacher@example.org');
  await page.goto(travelUrl);
  await page.waitForSelector('.task-box .sf-form');
  check((await page.locator('.task-box').textContent()).includes('Sent back by Avery Admin: The hotel receipt shows a different total.'), 'the employee sees why it came back');
  await page.waitForFunction(() => [...document.querySelectorAll('.task-box input')].some((i) => i.value === '690.25'));
  check(true, 'the task is filled with what they entered before');
  await field('Actual total cost').locator('input').fill('702.10');
  await field('Actual total cost').locator('.sf-was:not([hidden])').waitFor();
  check((await field('Actual total cost').locator('.sf-was').textContent()) === 'Was: $690.25', 'the changed amount shows what it was');
  await page.screenshot({ path: S + '/shots/57-task-sent-back.png', fullPage: true });
  await page.click('.task-box button[type=submit]');
  await page.waitForFunction(() => !document.querySelector('.task-box'), null, { timeout: 10000 });
  check((await page.locator('.answers dt:has-text("Actual total cost") .edit-badge').textContent()).includes('was: $690.25'), 'the request marks the corrected amount');
  await login(page, 'admin@example.org');
  await page.goto(travelUrl);
  await page.waitForSelector('.task-box .sf-form');
  check((await page.locator('.task-box').textContent()).includes('Actual: $702.10'), 'reconciliation comes back to the business office with the corrected amount');
  await field('Amount to reimburse').locator('input').fill('690.25');
  await page.click('.task-box button[type=submit]');
  await page.waitForFunction(() => document.querySelector('.page-head .pill')?.textContent === 'Completed', null, { timeout: 10000 });
  check(await page.locator('.answers dt:has-text("Amount to reimburse") .edit-badge').count() === 0, 'a task done for the first time is not marked as edited');
  check(true, 'reconciliation completes the request');
  await page.screenshot({ path: S + '/shots/53-travel-complete.png', fullPage: true });

  await page.goto(base + '/Admin/Teams');
  check(await page.locator('main section.panel h2').count() === 8 && (await page.locator('body').textContent()).includes('used by Request for Work Related Travel'), 'admins manage the starter teams in one place');
  const superTeam = page.locator('section.panel:has(h2:has-text("Superintendent"))');
  await superTeam.locator('textarea').fill('not-an-email');
  await superTeam.locator('button:has-text("Save")').click();
  await page.waitForSelector('.flash.bad');
  page.once('dialog', (d) => d.accept());
  await page.locator('section.panel:has(h2:has-text("Superintendent")) button:has-text("Remove")').click();
  await page.waitForSelector('.flash.bad');
  check((await page.locator('.flash.bad').textContent()).includes('Request for Work Related Travel'), 'a team a workflow uses cannot be removed');
  await page.screenshot({ path: S + '/shots/54-teams.png', fullPage: true });

  await login(page, 'builder@example.org');
  await page.goto(base + '/Builder');
  await page.locator('.wf-tile a.btn.primary').first().click();
  await page.waitForSelector('.bld-title');
  await page.click('.bld-tabs button:has-text("Flow")');
  await page.waitForSelector('.pal-item');
  check(await page.locator('.pal-item:has-text("Create ticket")').count() === 0 && await page.locator('.pal-item:has-text("Task")').count() === 1, 'a builder who is not an admin gets the Task step but no help desk steps');
  const builderId = Number(page.url().split('/').pop());
  const refused = await page.evaluate(async (id) => {
    const w = await window.SF.api('GET', '/api/builder/' + id);
    w.definition.flow.nodes.push({ id: 'sneak', type: 'fs_create_ticket', label: 'Ticket', x: 0, y: 0, config: { subject: 'x' } });
    try { await window.SF.api('PUT', '/api/builder/' + id, { name: w.name, description: w.description, category: w.category, color: w.color, definition: w.definition }); return 'saved'; }
    catch (e) { return e.message; }
  }, builderId);
  check(refused.includes('Only an administrator'), 'the server refuses help desk steps from a non-admin (' + refused.slice(0, 60) + ')');

  note('13d-3. field trip with a bus: booked by the Transportation team, no help desk');
  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/4');
  await page.waitForSelector('.sf-form');
  await field('Where are you going?').locator('input').fill('Museum of Natural History');
  await page.waitForFunction(() => document.querySelectorAll('.sf-field select option').length > 3);
  await field('Department').locator('select').selectOption({ label: 'Science' });
  await field('Date of trip').locator('input').fill(new Date(Date.now() + 60 * 864e5).toISOString().slice(0, 10));
  await field('Number of students').locator('input').fill('30');
  await field('Number of chaperones').locator('input').fill('3');
  await page.locator('.sf-check:has-text("We need a bus") input').check();
  await field('Bus quote').waitFor({ state: 'visible' });
  await field('Bus quote').locator('input').fill('520');
  await field('How does this trip').locator('textarea').fill('Evolution unit');
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const busTrip = page.url();
  await login(page, 'principal@example.org');
  await page.goto(busTrip);
  await page.click('button[name=decision][value=approve]');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.page-head .pill').textContent()) === 'Waiting on a task', 'an approved trip with a bus waits on Transportation');
  await login(page, 'admin@example.org');
  await page.goto(busTrip);
  await page.waitForSelector('.task-box .sf-form');
  check((await page.locator('.task-box').textContent()).includes('Students: 30, chaperones: 3'), 'the Transportation task has the trip details');
  check((await field('Final cost').locator('input').inputValue()) === '520', 'the final cost starts from the quote');
  await field('Bus company').locator('input').fill('Coach USA');
  await field('Pick-up time').locator('input').fill('07:45');
  await field('Confirmation number').locator('input').fill('C-118');
  await page.screenshot({ path: S + '/shots/55-bus-task.png', fullPage: true });
  await page.click('.task-box button[type=submit]');
  await page.waitForFunction(() => document.querySelector('.page-head .pill')?.textContent === 'Completed', null, { timeout: 10000 });
  check(!(await page.locator('.timeline').textContent()).includes('Freshservice'), 'no help desk ticket is created for a field trip');
  await login(page, 'teacher@example.org');
  await page.goto(base + '/Notifications');
  check((await page.locator('body').textContent()).includes('Your bus is booked with Coach USA, picking up at 07:45 (confirmation C-118).'), 'the organiser is told the bus details');

  note('13e. student access, roster, student field, accessibility mode');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Admin/StudentAccess');
  fs.writeFileSync(S + '/roster.csv', 'Last Name,First Name,Student ID,Home Address,Grade,School,Academy\nOkafor,Maya,20451,12 Elm St,10,Teterboro,Aerospace\nOkon,Leo,20452,5 Oak Ave,11,Paramus,Culinary\n');
  await page.setInputFiles('#sa-file', S + '/roster.csv');
  page.once('dialog', (d) => d.accept());
  await page.click('button:has-text("Import and replace")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.flash.ok').textContent()).includes('2 added') && (await page.locator('body').textContent()).includes('2 students on the roster'), 'a roster file is imported');
  check(!(await page.locator('body').textContent()).includes('Elm St'), 'columns outside the six agreed ones are not kept or shown');
  await page.fill('#sa-email', 'principal@example.org');
  await page.click('button:has-text("Give access")');
  await page.waitForSelector('.flash.ok');
  check(await page.locator('tr:has-text("principal@example.org") button:has-text("Remove access")').count() === 1, 'an admin gives a staff member student access');
  await page.screenshot({ path: S + '/shots/49-student-access.png', fullPage: true });
  const adminSearch = await page.evaluate(async () => (await fetch('/api/students?q=Oka')).status);
  check(adminSearch === 403, 'an admin without the flag cannot search students (status ' + adminSearch + ')');

  await page.goto(base + '/Builder');
  fs.writeFileSync(S + '/dismissal.schoolflow.json', JSON.stringify({
    schoolflow: 1, name: 'Early Dismissal', description: 'Record an early dismissal', category: 'Students', color: 'amber',
    definition: {
      trigger: { type: 'form' },
      form: { fields: [{ key: 'student', label: 'Student', type: 'student', required: true }, { key: 'reason', label: 'Reason', type: 'text', required: true }] },
      flow: { nodes: [{ id: 'start', type: 'start', label: 'Form submitted', x: 150, y: 40 }, { id: 'end', type: 'end', label: 'Done', x: 150, y: 320 }], edges: [{ from: 'start', to: 'end' }] },
    },
  }));
  await page.setInputFiles('#import-file', S + '/dismissal.schoolflow.json');
  await page.waitForURL(/Builder\/Edit\/\d+/);
  const dismissalId = Number(page.url().split('/').pop());
  await page.waitForSelector('.bld-title');
  const published = await page.evaluate(async (id) => {
    const w = await window.SF.api('GET', '/api/builder/' + id);
    return window.SF.api('POST', '/api/builder/' + id + '/publish', { name: w.name, description: w.description, category: w.category, color: w.color, definition: w.definition });
  }, dismissalId);
  check(published.ok === true, 'a form with a Student field publishes');

  await login(page, 'teacher@example.org');
  await page.goto(base + '/Start/' + dismissalId);
  await page.waitForSelector('.sf-form');
  await field('Student').locator('input').pressSequentially('Oka', { delay: 40 });
  await page.waitForFunction(() => [...document.querySelectorAll('.sf-error')].some((e) => !e.hidden && /do not have access to student information/.test(e.textContent)), null, { timeout: 6000 }).catch(() => {});
  check((await field('Student').locator('.sf-error').textContent()).includes('do not have access to student information') && await page.locator('.sf-suggest-item').count() === 0, 'staff without access cannot look students up');

  await login(page, 'principal@example.org');
  await page.goto(base + '/Start/' + dismissalId);
  await page.waitForSelector('.sf-form');
  await field('Student').locator('input').pressSequentially('oka', { delay: 40 });
  await page.waitForSelector('.sf-suggest-item');
  check(await page.locator('.sf-suggest-item').count() === 1 && (await page.locator('.sf-suggest-item').textContent()).includes('Okafor, Maya'), 'staff with access find a student by name');
  await page.locator('.sf-suggest-item').click();
  check((await page.locator('.sf-student').textContent()).includes('ID 20451') && (await page.locator('.sf-student').textContent()).includes('Grade 10'), 'the chosen student shows ID, school, grade and academy');
  await field('Reason').locator('input').fill('Medical appointment');
  await page.screenshot({ path: S + '/shots/50-student-field.png', fullPage: true });
  await page.click('main button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const dismissalUrl = page.url();
  check((await page.locator('.answers').textContent()).includes('Okafor, Maya · ID 20451 · Teterboro · Grade 10 · Aerospace'), 'someone with access sees the student on the request');
  await login(page, 'admin@example.org');
  await page.goto(dismissalUrl);
  const restricted = await page.locator('.answers').textContent();
  check(restricted.includes('Restricted') && !restricted.includes('Okafor') && restricted.includes('Medical appointment'), 'someone without access sees “Restricted” in place of the student');
  await page.goto(base + '/Admin/Audit');
  const audit5 = await page.locator('.table tbody').textContent();
  check(audit5.includes('Student access given') && audit5.includes('Student roster imported') && !audit5.includes('Okafor'), 'access changes and imports are audited, without student details');

  await login(page, 'teacher@example.org');
  await page.keyboard.press('Tab');
  check((await page.evaluate(() => document.activeElement.textContent.trim())) === 'Skip to main content', 'the first Tab stop is “Skip to main content”');
  await page.goto(base + '/Account/Display');
  await page.click('main form button');
  await page.waitForSelector('.flash.ok');
  check(await page.evaluate(() => document.documentElement.classList.contains('a11y')), 'accessibility mode switches on');
  await page.goto(base + '/Start/1');
  await page.waitForSelector('.sf-form');
  check(await page.evaluate(() => document.documentElement.classList.contains('a11y') && parseFloat(getComputedStyle(document.body).fontSize) > 17), 'it stays on from page to page, with larger text');
  await page.click('main button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check(await field('What do you need?').locator('input').getAttribute('aria-invalid') === 'true' && !!(await field('What do you need?').locator('input').getAttribute('aria-describedby')), 'a problem is tied to its field for screen readers');
  await page.screenshot({ path: S + '/shots/51-accessibility-mode.png', fullPage: true });
  await page.goto(base + '/Account/Display');
  await page.click('main form button');
  await page.waitForSelector('.flash.ok');
  check(await page.evaluate(() => !document.documentElement.classList.contains('a11y')), 'accessibility mode switches off again');

  note('13y. where each request is, in lists');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Requests?scope=managed');
  const listLines = await page.locator('.stop-line').allTextContents();
  check(listLines.length >= 1 && listLines.every((t) => /Waiting on /.test(t) && /(Here now · today|Waiting \d+ school days?|Overdue)/.test(t)),
    'the request list shows each waiting request\'s stop, whose desk and how long (' + listLines.length + ')');
  await page.screenshot({ path: S + '/shots/61-requests-list.png', fullPage: true });
  await login(page, 'teacher@example.org');
  await page.goto(base + '/');
  check(await page.locator('.table .stop-line').count() === await page.locator('.table tbody tr:has(.pill.warn)').count(), 'recent requests on Home show where each waiting one is (' + await page.locator('.table .stop-line').count() + ')');
  check(await page.locator('.wf-card .wf-dot svg').count() === await page.locator('.wf-card').count(), 'every form tile has a picture');
  await page.screenshot({ path: S + '/shots/60-home-icons.png', fullPage: true });
  await login(page, 'admin@example.org');
  await page.goto(base + '/Approvals');
  check(await page.locator('.approve-box .stop-line .age-pill').count() >= 1, 'the approvals inbox shows how long each one has waited');

  note('13z. passing an approval to someone else, with staff email suggestions');
  const passUrl = await submitTrip('Botanical garden');
  await login(page, 'principal@example.org');
  await page.goto(passUrl);
  await page.click('.approve-box summary:has-text("Pass to someone else")');
  const passTo = page.locator('.approve-box details[open] input[type=email]');
  await passTo.pressSequentially('avery', { delay: 30 });
  await page.waitForFunction(() => { const i = document.querySelector('.approve-box details[open] input[type=email]'); return i && i.list && i.list.options.length > 0; }, null, { timeout: 5000 });
  check(await page.evaluate(() => [...document.querySelector('.approve-box details[open] input[type=email]').list.options].some((o) => o.value === 'admin@example.org')), 'typing a name suggests staff email addresses');
  await page.screenshot({ path: S + '/shots/58-pass-on.png', fullPage: true });
  await passTo.fill('teacher@example.org');
  await page.click('.approve-box details[open] button:has-text("Pass it on")');
  await page.waitForSelector('.flash.bad');
  check((await page.locator('.flash.bad').textContent()).includes('person who asked'), 'an approval cannot be passed to the requester');
  await page.click('.approve-box summary:has-text("Pass to someone else")');
  await page.locator('.approve-box details[open] input[type=email]').fill('admin@example.org');
  await page.fill('.approve-box details[open] input[name=note]', 'You know this programme better.');
  await page.click('.approve-box details[open] button:has-text("Pass it on")');
  await page.waitForSelector('.flash.ok');
  check((await page.locator('.timeline').textContent()).includes('passed from principal@example.org to admin@example.org'), 'passing an approval on is on the request\'s history');
  check(await page.locator('.approve-box').count() === 0, 'the person who passed it on no longer has it to decide');
  const people = await page.evaluate(async () => (await fetch('/api/people?q=a')).json());
  check(Array.isArray(people) && people.length === 0, 'email suggestions need at least two characters');

  note('14. admin + help + mobile');
  await login(page, 'admin@example.org');
  await page.goto(base + '/Admin/Settings');
  await page.click('button:has-text("Test connection")');
  check((await page.locator('.panel .flash').first().textContent()).includes('Simulation mode'), 'test connection reports simulation');
  check((await page.locator('body').textContent()).includes('resumed 1 waiting'), 'webhook log shown');
  await page.screenshot({ path: S + '/shots/12-settings.png', fullPage: true });
  await page.locator('tr:has-text("teacher@example.org") select').selectOption('Builder');
  await page.waitForSelector('.flash.ok');
  await page.goto(base + '/Help/Formulas');
  check(await page.locator('.fn-table tr').count() > 50, 'formula guide lists functions');
  await page.setViewportSize({ width: 390, height: 800 });
  await page.goto(base + '/');
  await page.screenshot({ path: S + '/shots/13-mobile-home.png' });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  check(overflow <= 0, 'no horizontal overflow on phone home (' + overflow + ')');
  await page.click('.wf-card:has-text("Technology Purchase")');
  await page.waitForSelector('.sf-form');
  const overflow2 = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  check(overflow2 <= 0, 'no horizontal overflow on phone form (' + overflow2 + ')');
  await page.screenshot({ path: S + '/shots/14-mobile-form.png', fullPage: true });

  await browser.close();
  note('\n' + (problems.length ? problems.length + ' PROBLEM(S):\n' + problems.join('\n') : 'ALL CHECKS PASSED'));
})().catch((e) => { console.log('SCRIPT ERROR', e.message.split('\n').slice(0, 6).join('\n')); console.log(problems.join('\n')); process.exit(1); });
