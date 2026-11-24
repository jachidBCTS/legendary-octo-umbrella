# Change queue

The owner sends requests as they come up; each is added here and NOT worked on yet.
When the owner says **"start making the changes"**, do every open item in one batch
(one round of full tests and screenshots, one push), then move the items to "Done".
Ask clarifying questions for unclear items in ONE message before starting.

## Open

- #14 Uploaded signature image (Nov 24): on their settings page a user can upload a PNG of their
  signature and use it instead of the script-font "Sign with my name" signature (on the form and
  printout). Server-side checks (real PNG, size limit), only the owner can set/remove it.
  Show the recommended size next to the upload box (ⓘ tip): 600 × 200 px (3:1, wide), transparent
  or white background, dark ink, max 500 KB; scale larger/smaller images to fit the signature line.

## Done

- #12 Easter egg (Nov 24): `wwwroot/js/egg.js` + `.egg-*` in `app.css`; the "t" of the sidebar and
  phone-bar logo is a button (`.egg-t`), the rest stays the Home link (`.brand-link`). Sprites in
  `img/egg/` load on first click; skipped with reduced motion; Escape stops; stage ignores clicks.
  Mockup kept in `docs/mockups/easter-egg.html`. Optional later: two more walk frames.
- #13 Friendlier form logic (Nov 24): plain-language rule maker on every logic box and a guided
  maker for Calculated fields; formulas folded under "Advanced" (`builder-rules.js`, see NOTES "Rule maker").
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
