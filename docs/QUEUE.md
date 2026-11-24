# Change queue

The owner sends requests as they come up; each is added here and NOT worked on yet.
When the owner says **"start making the changes"**, do every open item in one batch
(one round of full tests and screenshots, one push), then move the items to "Done".
Ask clarifying questions for unclear items in ONE message before starting.

## Open

- #12 **Easter egg on the "t" in Greenlight** (Nov 24; owner approved the mockup:
  https://claude.ai/artifact/BhD3YztxxrHGB8NkTqDXM2; copy in `docs/mockups/easter-egg.html`).
  Clicking the last letter of the sidebar / phone-bar logo plays a ~12 s pixel-art scene along the
  bottom of the page: he walks out from behind the menu, waves with an "I'm out!" bubble, sunglasses
  drop on, he walks to the convertible parked just beside him, hops in, faces the viewer and waves,
  revs and drives off right with white smoke. Sprites (checkerboard removed, sunglasses drawn in,
  car mirrored to face right) are already in `wwwroot/img/egg/` (~200 KB, unused so far). To build:
  move the mockup's script to `wwwroot/js/egg.js` (CSP: no inline script; the smoke-puff SVG can
  stay a data: URI in `app.css`, since `img-src` allows data:), split "Greenligh" link + a `<button>`
  "t" in `_Layout.cshtml` (logo link must still go Home), load sprites only on click, skip when
  `prefers-reduced-motion`, Escape stops it, stage `pointer-events: none`. Walk = alternating
  legs-apart / legs-passing poses with a 6 px lift (owner: "good enough"). Optional later: two
  frames with the other leg leading would make the walk more natural.
- #13 Form logic area too technical (Nov 24): make the builder's form logic (show/hide rules,
  required-if, calculations) friendlier for non-technical staff — plain-language "When … then …"
  rule builder with pick-lists for fields/values, formulas only as an "advanced" fold-out. Look at
  the current UI and ask the owner what feels hardest before designing (big feature).
- #14 Uploaded signature image (Nov 24): on their settings page a user can upload a PNG of their
  signature and use it instead of the script-font "Sign with my name" signature (on the form and
  printout). Server-side checks (real PNG, size limit), only the owner can set/remove it.
  Show the recommended size next to the upload box (ⓘ tip): 600 × 200 px (3:1, wide), transparent
  or white background, dark ink, max 500 KB; scale larger/smaller images to fit the signature line.

## Done

- #4 Table field with "Add row" (Nov 23): already exists — builder field type **Table**
  (columns, "+ Add row", optional max rows). Told the owner; reopen if something is missing.
- #1 Names as "First Last" (Nov 23): `Core/PersonName` (Tidy "Last_First"/"Last, First"; given+family
  name claims win); used by `User.DisplayName()` and the people suggestions. Old test requests keep
  the name they were saved with; users' stored names update at their next sign-in.
- #2 "Submitted" (Nov 23): `Ui.HandedToHelpDesk` — finished, opened a ticket, never waited on it.
- #3 Live ticket on the request page (Nov 23): `Services/TicketView` (status + public replies,
  2-min cache, 6-s limit, private notes skipped); everyone who can open the request sees it.
- #5 Signature (Nov 23): "Sign with my name" button (`body[data-my-name]`), printout shows the signed
  name in script with "Signed electronically · date"; the blank signature line only without one.
- #6 Preview scrolled past the form (Nov 23): hidden file input escaped its field; anchored it.
- #7 Suggestions behind pop-up windows (Nov 23): the list moves into the open `<dialog>` (top layer).
- #8 Personal chain → shared template (Nov 23): "Make shared template" (builders/admins), moves it;
  `POST /api/chains/{id}/share`, refused if a shared one has the same name; audited.
- #9/#10 Menu (Nov 23): Teams = people icon, Rates = dollar, Student Access = caution; Title Case labels.
- Red count badges in the menu (Nov 23, asked during the batch).
- #11 Daily summary email (Nov 23): `AppUser.DigestMinute`/`DigestSentOn`, `EmailStatus.Held`,
  `EmailOutboxWorker.ProcessDigestsAsync`; school days only; urgent = sent back, escalated, overdue.
