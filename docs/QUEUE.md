# Change queue

The owner sends requests as they come up; each is added here and NOT worked on yet.
When the owner says **"start making the changes"**, do every open item in one batch
(one round of full tests and screenshots, one push), then move the items to "Done".
Ask clarifying questions for unclear items in ONE message before starting.

## Open

- #15 Address suggestions (Nov 24): as someone types an address (e.g. "540"), show matching full
  addresses under the box (street + town, state) to pick from — like Google Maps' list, screenshot
  sent by the owner. Doesn't have to be Google. To settle when starting: which fields get it (a new
  "Address" field type in the builder vs. a switch on Text fields); which service (free options:
  OpenStreetMap Photon/Nominatim, no key, ~1 search/sec fair-use; or a keyed one such as Geoapify,
  Mapbox, Google). Lookups go through our server (CSP, rate limit, cache, no user details sent),
  NJ/nearby results first, typed text always allowed if the service is down.

- #16 Menu icon hover animation (Nov 24): the small icons in the left sidebar get a playful little
  animation on hover (and keyboard focus) for "a bit more pizzazz". Keep it short and subtle; none
  under `prefers-reduced-motion`; must not clash with the existing bell ring on unread notifications.
- #17 Traffic-light logo (Nov 24): a red → yellow → green light animation on the Greenlight logo
  mark. Ask at batch start: play once on page load, on hover, or both (not a constant loop, which
  distracts)? Must keep working with the #12 easter-egg "t"; none under `prefers-reduced-motion`.

- #18 All requests page (Nov 24): swap the Status and Form filters so the order is Any form, Any
  status, Any department. Subtitle "Every request across the school." becomes "Every request from
  across the district" (`Pages/Requests/Index.cshtml`; check other places with the same wording).
  Ask at batch start: should the workflow-owner and department views keep their own subtitles
  ("Requests submitted on the workflows you own"), since "every request" would not be true there?

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
- #14 Signature picture (Nov 24): menu → My Signature uploads a PNG (600 × 200 recommended, 500 KB);
  "Sign" then signs with it (server attaches only the signer's own picture); request page + printout show it.
