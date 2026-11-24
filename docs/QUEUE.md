# Change queue

The owner sends requests as they come up; each is added here and NOT worked on yet.
When the owner says **"start making the changes"**, do every open item in one batch
(one round of full tests and screenshots, one push), then move the items to "Done".
Ask clarifying questions for unclear items in ONE message before starting.

## Open

- #12 Form logic area too technical (Nov 24): make the builder's form logic (show/hide rules,
  required-if, calculations) friendlier for non-technical staff — plain-language "When … then …"
  rule builder with pick-lists for fields/values, formulas only as an "advanced" fold-out. Look at
  the current UI and ask the owner what feels hardest before designing (big feature).

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
