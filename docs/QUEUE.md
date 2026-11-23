# Change queue

The owner sends requests as they come up; each is added here and NOT worked on yet.
When the owner says **"start making the changes"**, do every open item in one batch
(one round of full tests and screenshots, one push), then move the items to "Done".
Ask clarifying questions for unclear items in ONE message before starting.

## Open

7. **Email suggestions show behind the pop-up window** (Nov 23, bug). Approval chains page →
   "New chain of my own" dialog: typing in a step's email box shows the suggestion list
   (`ul.people-pop`) behind/below the dialog instead of on top. Likely z-index / the list being
   placed outside the `<dialog>` (top layer). Check every dialog with email boxes (chains, teams,
   departments, stand-ins, forward).
8. **Turn a personal approval chain into a shared template** (Nov 23). On the Approval chains page,
   a "Make shared template" action on a chain under "My chains" (builders/admins only, since only
   they create shared templates). Copy or move? Default: move it (it leaves "My chains"); ask if unclear.
9. **Menu icons** (Nov 23). Left menu: Teams → a group of people; Rates → a dollar sign;
   Student access → a caution (warning triangle) sign. Icons live in `Pages/Shared/_Icons`.
10. **Menu labels in Title Case** (Nov 23). Every left-menu item capitalised: "Approval Chains",
   "Approvals & Tasks", "My Requests", "Away / Stand-In", "Formula Guide", "School Calendar",
   "Student Access", "Audit Log", etc. (menu only; page headings can follow if they match the menu).

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
