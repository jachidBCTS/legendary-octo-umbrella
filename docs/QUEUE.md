# Change queue

The owner sends requests as they come up; each is added here and NOT worked on yet.
When the owner says **"start making the changes"**, do every open item in one batch
(one round of full tests and screenshots, one push), then move the items to "Done".
Ask clarifying questions for unclear items in ONE message before starting.

## Open

(none)

- #16 Database-down banner (Nov 25): today the app has the same problem as the other apps — the
  database is touched at startup (`Program.cs` migrations/seed: the site won't start, IIS 500.30), on
  every signed-in request (`RoleClaimsTransformation`, `_Layout` badge counts) and by the Error page
  itself (uses `_Layout`), so an outage while running gives a bare error. Wanted: the site still loads
  and shows a big red banner at the top, "Database connection failed. Technology has been notified."
  Plan: start even if the database is down (retry the migrations in the background); catch database
  errors and show a plain, database-free page/banner; a cheap health check so normal pages show the
  banner too; "notified" can't use the email outbox (it lives in the database) — send directly via
  Graph to an address in appsettings, at most once per outage, plus a Windows event log entry. Ask:
  which address is "Technology" (help desk mailbox, or open a Freshservice ticket instead?).

## Done

- #21 Custom ticket status names (Nov 24): `TicketStatusNames` (Web service) reads the Status field's
  choices (`IFreshserviceClient.TicketStatusesAsync` → `ticket_form_fields`, `FreshserviceClient.ParseStatuses`),
  cached 1 day (10 min after a failure, standard four meanwhile). Request page ticket box shows the real
  name (custom "…pending…" names get the Pending colour); builder status dropdowns and the rule maker list
  them by name (`ticketStatuses` from `GET /api/builder/{id}`); Settings → "Show ticket fields" also lists
  statuses with numbers and refreshes the cached list. Simulation adds 6 "Pending client info", 7 "Travel Pending".
- #15 Address suggestions (Nov 24): builder → Text field → "Suggest addresses as people type"
  (`FieldDef.Suggest = "address"`; the answer stays plain text). `GET /api/addresses?q=` (3+ characters)
  → `AddressLookup` (1-day cache, about 1 search/second site-wide, failure = "not connected", box keeps
  working) → `PhotonAddressSource` (OpenStreetMap via photon.komoot.io, owner's choice; biased to
  Hackensack; US only; "540 Main Street, Hackensack, NJ 07601"). Development uses `SampleAddressSource`.
  `Addresses:Provider` photon | sample | off. Not tried against the real Photon service (no internet here).
- #16 Menu icons (Nov 24): CSS keyframes per menu item on hover/keyboard focus (hop, drop, wiggle, spin,
  blink, scribble, pop; bell rings only when there is no unread badge). Off under reduced motion and
  accessibility mode.
- #17 Logo (Nov 24): on hover (owner's choice) the mark goes red → yellow → green and the tick draws in
  (`sf-traffic`, `.brand-check`). Easter egg "t" unchanged.
- #18 All requests (Nov 24): filters Form, Status, Department; admin subtitle "Every request from across
  the district." (also Reports). Workflow-owner and department views keep their own (owner's choice).
- #19 Home (Nov 24): `.form-groups` puts the category fold-outs in 2 columns (one card per row inside);
  one column under 640 px.
- #20 Help (Nov 24): `/Guide` (`Pages/Guide.cshtml`), fold-out sections from the approved Claude Doc,
  6 annotated screenshots in `wwwroot/img/help` (WebP, from `docs/help/help-shots.js`), contact
  "Jacob Hidalgo" (owner's choice, name only). Round "?" button bottom right on every signed-in page
  except builder pages; pages got 96 px more room at the bottom so it covers nothing. Update the guide
  when a feature it describes changes.

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
