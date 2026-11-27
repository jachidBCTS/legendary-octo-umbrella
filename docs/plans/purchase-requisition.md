# Purchase requisition — plan (in progress, nothing built yet)

Planning with the owner started Nov 26, 2026. **Do not start building until the owner says the
planning is complete.** The district's budget/finance system is called "the budget system" in
everything committed here (owner's request: don't name the vendor in GitHub).

## Agreed so far

- **Who can create**: people with the "requisitioner" role — planned as an Entra group used as the
  workflow's audience (existing feature).
- **Vendors**: searched live from the budget system (read-only SQL, same pattern as the student
  roster). A requester may add a **new vendor**; that starts a side flow where the **Purchasing group**
  verifies it. The side flow runs **in parallel** (requisition keeps moving) — owner to confirm with
  the business office. Verified vendors are added to the budget system **by hand** (it has no vendor
  import).
- **Budget accounts**: searched live from the budget system; the picker shows money left.
  Money left = budget system balance **minus requisitions still open in Greenlight**. Too little
  money = **warning for reviewers, not a block** (budget data isn't real time).
- **Split funding**: one requisition can be charged to several accounts, by **dollar amount**, for
  the whole requisition (not per line). The account distribution must equal the requisition total
  (shipping included). Low-funds check per account line.
- **Money left** stops counting a requisition once it is in an exported CSV batch (the budget
  system holds it from then on), or as soon as it is rejected/withdrawn.
- **Line items**: quantity, description, unit price (+ line total, grand total). Optional
  **shipping** line. No tax (district is tax exempt). Exact columns to be checked against the
  owner's PDF of the current PO.
- **Route**: requester picks **up to the first 3 approvers** — anyone at the school; saved
  "personal chains" allowed (existing feature) — (e.g. supervisor, central admin), then
  fixed steps: **Accounts Payable** (group; verifies funds, may correct account numbers) →
  **Purchasing** (group) → **Business Administrator** (one person).
- **Send back**: each approver can send it back **to a chosen earlier step** or reject outright
  (allowed choices differ per step).
- **Editing**: AP, Purchasing, the business office and the Business Administrator may change
  anything on the requisition at their step (every change recorded in the timeline). Earlier
  approvers approve / send back / reject only.
- **Private notes** (new): staff-to-staff notes the requester can't see (e.g. Purchasing → BA).
  Greenlight has only one comment thread today, visible to everyone on the request.
- **New vendor rejected by Purchasing**: the requisition is held, the requester is told, and it is
  sent back to them to choose another vendor. New-vendor form: name, address, contact, phone,
  email, W-9 upload.
- **Requisition number**: Greenlight numbers each one; format may have to follow the budget
  system's import schema (owner checking).
- **Purchase order**: the budget system creates the PO and its number. Greenlight's job ends with
  an approved requisition + export; owner is checking how to get the PO number back.
- **Export**: downloaded by **Accounts Payable**; a **batch CSV** of approved requisitions in the budget system's import layout.
- **Connection**: the IIS server can reach the budget system's cloud SQL; a read-only login is possible.

## Waiting on the owner

- PDF of the current purchase order (fields/layout).
- Budget system import file layout (Monday).
- Business office: parallel vendor check OK?
- After a send-back to an earlier step: do all later steps approve again, or jump back to the
  sender? (owner checking)
- Requisition number format (owner checking with the budget system).
- Dollar thresholds (extra steps / quotes required) — parked.
- Table/view names for vendors, accounts, balances.

## New code needed (rough)

1. Repeating line-item table field (add/remove rows; server-side totals).
2. Budget system connection: vendor search, account search with balance, "Check the connection".
3. Open-requisition amounts subtracted from balances; low-funds warning.
4. Account split (allocation rows that must add up to the grand total).
5. New-vendor side flow for Purchasing.
6. Send back to a chosen earlier approval step (today: back to requester only).
7. AP corrects account numbers at their step (changes shown in the timeline).
8. Private (staff-only) notes on a request.
9. Batch CSV export screen; requisitions marked as exported so they aren't sent twice.
