# Change queue

The owner sends requests as they come up; each is added here and NOT worked on yet.
When the owner says **"start making the changes"**, do every open item in one batch
(one round of full tests and screenshots, one push), then move the items to "Done".
Ask clarifying questions for unclear items in ONE message before starting.

## Open

1. **Show names as "Firstname Lastname"** (Nov 23). Entra display names are "Lastname_Firstname"
   (e.g. "Abas_Ekrem"), which looks odd everywhere (sample: travel summary line
   "Abas_Ekrem (Bergen County Technical Schools): Total estimated expenses"). Show "Ekrem Abas"
   instead, everywhere names appear (pages, emails, PDFs, tickets). Likely: build from Graph
   givenName + surname, fall back to flipping "Last_First", then display name.
2. **Help desk requests say "Completed" too early** (Nov 23). When a request's job is just to open a
   Freshservice ticket, the status tracker shows "Completed", which reads as if the issue is fixed.
   Show "Submitted" (the ticket is open) instead.
3. **Show live ticket status and replies on the request page** (Nov 23, owner asked if possible;
   answer: yes). Pull the ticket's status (Open / Pending / Resolved / Closed) and the agents'
   public replies from the Freshservice API (ticket + conversations) and show them on the request
   page. Never show private agent notes. Refresh when the page is opened (short cache), plus the
   existing 15-minute check / webhook. Bigger feature: confirm details with the owner first.
4. **Table field with "Add row"** (Nov 23). New form field type: a table whose columns the builder
   defines (text, number, date, dropdown, etc.); on the form the requester adds/removes rows as
   needed. Needs: server-side validation per cell, formulas over columns (e.g. sum of a cost
   column), display on request page / print / emails / CSV, phone layout. Bigger feature: confirm
   column types, row limits and totals with the owner first.

## Done
