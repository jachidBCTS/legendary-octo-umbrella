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
  await page.goto(base + '/Account/SignOut');
  await page.goto(base + '/Account/DevLogin');
  await page.click(`button[value="${email}"]`);
  await page.waitForURL(base + '/');
}

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1360, height: 900 } });
  const page = await ctx.newPage();
  page.on('console', (m) => { if (m.type() === 'error' && !/40[134]/.test(m.text())) { problems.push('console: ' + m.text()); note('  CONSOLE ' + m.text()); } });
  page.on('pageerror', (e) => { problems.push('pageerror: ' + e.message); note('  PAGEERROR ' + e.message); });
  page.on('response', (r) => { if (r.status() >= 500) { problems.push('500: ' + r.url()); note('  HTTP ' + r.status() + ' ' + r.url()); } });

  note('1. teacher home');
  await login(page, 'teacher@example.org');
  await page.screenshot({ path: S + '/shots/01-home.png' });
  check(await page.locator('.wf-card').count() === 2, 'two live forms on home (ticket-triggered draft hidden)');
  check(await page.locator('a[href="/Builder"]').count() === 0, 'staff do not see builder nav');
  const denied = await page.goto(base + '/Builder');
  check(page.url().includes('/Account/Denied'), 'staff are refused the builder (' + page.url() + ')');
  const api = await page.evaluate(async () => (await fetch('/api/builder/1')).status);
  check(api === 403, 'staff are refused the builder API (status ' + api + ')');

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

  await page.click('button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field("Approver's email").locator('.sf-error').textContent()).includes('required'), 'submit blocked: approver required');
  check((await field('Justification').locator('.sf-error').textContent()).includes('required'), 'submit blocked: justification required when >= 500');
  await field("Approver's email").locator('input').fill('teacher@example.org');
  await field('Justification').locator('textarea').fill('Needed for <b>algebra</b> & "stats"');
  await page.click('button[type=submit]');
  await page.waitForTimeout(600);
  check((await field("Approver's email").locator('.sf-error').textContent()).includes('own request'), 'custom rule: cannot approve own request');
  await field("Approver's email").locator('input').fill('principal@example.org');
  await page.click('button[type=submit]');
  await page.waitForURL(/Requests\/Detail\/\d+/);
  const runUrl = page.url();
  check((await page.locator('.page-head .pill').textContent()) === 'Awaiting approval', 'run is awaiting approval');
  check(await page.locator('.approve-box').count() === 0, 'submitter cannot approve');
  check((await page.locator('.answers').textContent()).includes('<b>algebra</b>'), 'HTML in answers is shown as text, not rendered');
  await page.screenshot({ path: S + '/shots/03-detail-waiting.png', fullPage: true });

  note('3. approval');
  await login(page, 'principal@example.org');
  check((await page.locator('.sidebar .badge').textContent()).trim() === '1', 'approver sees badge 1');
  await page.goto(base + '/Approvals');
  await page.screenshot({ path: S + '/shots/04-approvals.png' });
  check((await page.locator('.approve-box .pre').textContent()).includes('$503.00'), 'approval instructions rendered with total');
  await page.click('.approve-box a.btn');
  await page.fill('.approve-box textarea', 'Go ahead');
  await page.click('button[name=approved][value=true]');
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
  await page.click('button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field('Details').locator('.sf-error').textContent()).includes('required'), 'details required for lost/stolen');
  await field('Details').locator('textarea').fill('Left on the bus');
  await page.click('button[type=submit]');
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
  await page.click('button[type=submit]');
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
  check(await field("Approver's email").locator('input').inputValue() === 'principal@example.org', 'email prefilled');
  await page.screenshot({ path: S + '/shots/16-copy-prefilled.png', fullPage: true });
  await page.click('button[type=submit]');
  await page.waitForSelector('.sf-banner:not([hidden])');
  check((await field("Approver's email").locator('.sf-error').textContent()).includes('own request'), 'copied answers are still validated for the new submitter');
  await field("Approver's email").locator('input').fill('admin@example.org');
  await page.click('button[type=submit]');
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

  note('9. admin + help + mobile');
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
