# SchoolFlow — handoff notes

Read this first. It records what the project is, how to work on it, the decisions already made
(and why), and what is still open. `README.md` covers features, configuration and deployment for
the people running the site; this file is for whoever continues the development.

## What it is

A build-your-own workflow system for a school, integrated with the Freshservice help desk.
Builders design a form, its field logic, and the flow that runs after submission. Rule-based
only: the owner explicitly chose **no AI in the workflows**.

Owner's stated choices (do not revisit without asking):

- ASP.NET Core 8 Razor Pages (they first asked for "aspx", then chose Core over Web Forms).
- Microsoft Entra ID sign-in. Email through Microsoft Graph, not SMTP.
- Freshservice in all four directions: create tickets/requests, update tickets, react to ticket
  events (webhook), look up Freshservice data.
- Approval chains chosen by **either** the workflow builder (fixed template) **or** the requester
  (on the form). The department's final approver(s), set by admins, are always appended.
- The requester picks the department on the form.

## Layout

```
src/SchoolFlow.Core    No web or database dependencies. Formula engine (Expressions/), form logic
                       (Forms/), workflow engine + definition checker + approval chains (Workflows/),
                       Freshservice client and simulator (Freshservice/), Definitions.cs (the JSON model).
src/SchoolFlow.Web     Razor Pages, JSON API controllers, EF Core (Data/), services (Services/),
                       migrations (Migrations/Sqlite, Migrations/SqlServer), front end (wwwroot/js, wwwroot/css).
tests/SchoolFlow.Tests xUnit: unit tests for Core plus database-level tests against in-memory SQLite.
tests/e2e/smoke.js     Playwright browser test of the whole app (needs a fresh database; see its header).
```

## Commands

```
dotnet test                                   # must stay green
dotnet run --project src/SchoolFlow.Web       # http://localhost:5080, dev sign-in, simulated Freshservice
dotnet tool restore                           # once, for dotnet-ef
```

After changing any entity in `Data/AppDbContext.cs`, add a migration for **both** engines:

```
dotnet ef migrations add <Name> --context SqliteAppDbContext    --output-dir Migrations/Sqlite    --project src/SchoolFlow.Web
dotnet ef migrations add <Name> --context SqlServerAppDbContext --output-dir Migrations/SqlServer --project src/SchoolFlow.Web
```

`MigrationTests` fails if the model and migrations disagree, or if the two engines' migration
names differ.

Browser test: start the app in Development with `Freshservice__WebhookSecret=test-secret-1234567890`
and a **new** SQLite file, then `node tests/e2e/smoke.js <output-dir>`. It assumes the seeded
workflow IDs (1 Purchase, 2 Repair, 3 Escalate, 4 Field Trip, 5 Travel) — keep `SeedData` order stable.

CI (`.github/workflows/build.yml`) runs the tests on Windows, publishes the win-x64 package,
starts it under real IIS on the runner and checks it responds. The owner deploys to **Windows IIS
as its own site, SQL Server with Windows sign-in, internet-reachable** — see `docs/DEPLOY-IIS.md`.
Keep that guide and the CI IIS job in step with any change to start-up, configuration or files
written at run time (`App_Data` holds the SQLite file and the data-protection keys).

## How the pieces fit

- **Definition JSON** (`WorkflowDefinition`): `trigger`, `form.fields[]`, `flow.nodes[]/edges[]`.
  Stored as `Workflow.DraftJson` (being edited) and `PublishedJson` (what runs). Each run snapshots
  the definition, so edits never change a request in flight.
- **Formulas are evaluated on the server only.** The browser posts current answers to
  `/api/forms/evaluate` and renders what comes back; submit re-runs the same `FormEvaluator`.
  Do not add a JavaScript copy of the formula engine — one source of truth was a deliberate choice.
- **`WorkflowEngine`** is persistence-free: it advances a `RunState` until it finishes, fails, or
  waits (approval / ticket event). `RunService` (Web) loads, saves, creates approval tasks and
  notifications. All run state changes go through `RunService`.
- **Concurrency**: `WorkflowRun.Version` is an optimistic concurrency token. Two people deciding
  at once → the second gets a "someone else acted" message. Keep `run.Version++` on every save.
- **Approval chain state** lives in the run context under the reserved name `chain`
  (`{ node, index, steps[] }`). "All must approve" is handled in `RunService.DecideAsync`
  (collects `ApprovalTask.ApprovedBy`), not in the engine.
- **Notifications** are rows in `Notifications`; `EmailOutboxWorker` sends pending ones through
  `IEmailSender` (Graph) with retries. Notifications are added to the same `SaveChanges` as the
  change that caused them.
- **Drafts** ("save and finish later"): `FormDraft` rows hold the raw, unvalidated answers per
  owner. Opening one feeds them to the form as `initialValues`, so they are re-normalised by
  `FormEvaluator` like any other input; submit deletes the draft. Autosave only starts after the
  person has pressed *Save for later* once (never create a draft they did not ask for).
- **Attachments**: the `Attachments` table describes each upload; the bytes are a file in
  `App_Data/uploads` (owner chose a folder on the web server over the database) named by a random
  `StoredName`. A file is "pending" (`RunId` null, usable only by its uploader on that workflow) until
  submit claims it for the run *before the first step executes*. `FormEvaluator` only accepts IDs in
  the caller's pending set and takes name/size from the server. The run context gets a reserved
  `attachments` list; a Create ticket step with `attachFiles=true` sends them with a second call
  (`AttachFilesAsync`), and a failure there is a timeline **warning**, never a failed run — keep it
  that way while that Freshservice call is unverified. Downloads go through `FilesController`
  (`/files/{publicId}`), always as attachments.
- **Send back / resubmit**: `ApprovalAction.Return` sets the run to `RunStatus.ChangesRequested`
  (task status `Returned`). `RunService.ResubmitAsync` re-validates through `FormEvaluator`, replaces
  the answers in the run context and calls `WorkflowEngine.ReenterApprovalAsync`, which re-enters the
  *same* node (a chain keeps its `index`, so earlier approvals stand). `WithdrawAsync` (requester
  only, while waiting) and `CommentAsync` (`RunComments`, the discussion thread) also live there.
- **Approval follow-up**: `ApprovalTask` carries `RemindDays`/`EscalateDays`/`EscalateTo` copied
  from the node config, `StandIns` (from `Delegations`, applied when the task is created and when a
  delegation is added) and `Escalated`. Who may decide = `ApprovalTask.CanBeDecidedBy`. A stand-in's
  approval counts as the person they stand in for. `FollowUpWorker` runs
  `RunService.FollowUpApprovalsAsync` hourly (reminders capped at 5) and `CheckWaitingTicketsAsync`
  every 15 minutes (live Freshservice only; the safety net for missed webhooks). All day counts are
  **school days** (`SchoolCalendar`, `ClosedDays` table), and the same closed days reach formulas
  through `Scope.ClosedDays`.
- **Export / reports / print**: `RequestExport.ToCsv` (cells starting with `= + - @` are prefixed
  with `'` — keep that guard), `Pages/Reports`, `Pages/Requests/Print` (inherits `DetailModel`;
  do not redeclare `OnGetAsync` there — two matching handlers crash the page).
- **Layout fields**: `section` and `page` hold no answer — test with `FieldTypes.IsLayout`, not
  `== Section`. A `page` field starts a new page; paging is entirely in `form-runtime.js`
  (`pageOf`, `showPage`, `nextPage`): the server still evaluates the whole form, and *Next* just
  asks the server and refuses to move while the current page has errors.
- **Worksheet** (`FieldTypes.Worksheet`, layout-only): `FieldDef.Rows` (`WorksheetRow`: number,
  item, description with `{field_key}` placeholders, cost field key). It only *arranges* ordinary
  fields of the same form — values, formulas and checks stay on those fields, so `FormEvaluator`
  is untouched. `form-runtime.js` builds held fields "compact" (`buildField(field, true)`, named by
  `aria-label`) and `buildWorksheet` places them. `FormDefinition.Style = "paper"` is CSS only
  (`.sf-paper`). Intro lines that all start with `•` render as a notice list.
- **Who builds what (owner, Nov 17, 2026): forms are admin-only; flows can be changed by staff.**
  `AccessService.CanEditForm` (= admin) gates creating, duplicating, importing, deleting and
  switching on/off workflows and any change to the form, trigger, name/description/category/colour, owners or
  audience. `CanManage` = admin **or listed owner** (the Builder role is no longer required, and on
  its own grants nothing). An owner who is not an admin gets the Flow, Preview and a read-only
  Settings tab; `BuilderController.FormRule` compares the saved form/meta against a snapshot taken
  before the save and refuses any difference; restoring a version as an owner copies only its flow.
  `/Builder` and `BuilderController` are `[Authorize]` (any signed-in person) — ownership is
  checked per workflow, and things a person may not touch are 404.
- **External code review (Nov 17, 2026)** — fixed: team check reads only "who" settings
  (`WorkflowEngine.PeopleSettings`/`TeamsNamedBy`); resubmit re-routes from Start when nothing with
  an effect has happened yet (`RouteFromStartIfUntouched`) and rebuilds an approval chain, keeping
  earlier approvals only if their steps are unchanged; overdue-notice recipients can open the
  request; follow-up reads only actionable items, in pages; resubmit deletes files taken off the
  form; defaults ordered after the fields they read; student data **and anything computed from it**
  (`StudentData.KeysOf`) hidden from people without access and refused by the checker in any
  message/instruction/ticket text; others' requests offered for copying only when Completed and
  without email/phone/approval-chain answers; `Auth:AllowedDomains` (`DomainPolicy`) for
  requester-typed approvers and stand-ins; Freshservice list lookups cached 15 min; per-person
  limit of 1200 `/api` calls a minute (limiter runs **after** authentication — school networks share
  one address); 1 MB cap on answer bodies; 250-field cap; sign-out is a POST page; `/Builder` refused
  to people who neither build nor own a workflow. Not done, by decision: limiting builders'
  notification recipients to district domains (outside notifications were requested).
- **Owner's direction (Nov 17, 2026): everything stays in house.** Business office staff have no
  Freshservice accounts and will not get them. Only technology-type workflows go to the help desk,
  and those are built **only by administrators**: `WorkflowDefinition.UsesHelpDesk()` (ticket
  trigger, `fs_*`/`wait_ticket` steps, `lookup` fields) is refused for non-admins on save, publish,
  create, duplicate and import (`BuilderController.HelpDeskRule`), and the builder hides those
  parts from them. Do not route a non-IT process through Freshservice.
- **Task step** (`NodeTypes.Task`): someone fills in fields part-way through. `NodeDef.Fields` are
  ordinary `FieldDef`s (types in `FieldTypes.InTask`) evaluated by `FormEvaluator` with the run
  context as ambient values, so their formulas can use the request's answers. The run waits in
  `RunStatus.WaitingTask` (use `RunStatus.IsWaitingOnPeople` wherever approvals and tasks are
  treated alike) with an `ApprovalTask` of `Kind = "task"`, so inbox, reminders, escalation,
  stand-ins and admin-on-behalf (audited) all apply. `RunService.CompleteTaskAsync` →
  `WorkflowEngine.ResumeWithTaskAsync` copies **only the step's own field keys** into the context
  (keys must be unique across the form and all tasks; `task` is reserved). Files uploaded in a task
  go through `/api/attachments` with `taskId`. Browser: `task.js` mounts the same `form-runtime.js`.
- **Due dates**: any approval/task/chain step may set `dueDate` (a formula giving a date, worked
  out when the step is reached; a bad result is a timeline warning, never a failed run) and
  `dueNotify` (people/teams told if it is late). Stored on `ApprovalTask.DueOn/DueNotify`.
  `FollowUpApprovalsAsync` sends one "due soon" heads-up `DueSoonDays` (3) calendar days before to
  whoever owes it, and one "overdue" notice on the day to them and to `DueNotify`. Calendar days,
  not school days (the owner's rule is "within 3 weeks of the travel").
- **Teams** (`Teams` table, Admin → Teams): steps name a team as `[Team name]` in any "who"
  setting (approvers, task assignees, notify to, escalate to); `WorkflowEngine.ResolvePeople`
  expands it when the step is reached, so waiting items keep the people they were sent to. A
  missing or empty team fails the run with a message that says where to fix it (retry afterwards).
  To prevent that, `TeamCheck.IssuesAsync` adds an error for every `[Team]` that is missing or
  empty when a workflow is saved or published, so it cannot be published until the team has people.
  Starter teams: `SeedData.StarterTeams` (Transportation + the six travel teams).
- **Field Trip template** (workflow 4): no help desk (owner, Nov 17). After the approval chain, if
  `bus_needed`, a task for `[Transportation]` (bus company, pick-up time, confirmation, final cost
  defaulting to the quote); then the organiser is told. Seeds as a draft on a real site.
- **Travel template** (`SeedData.Travel`, workflow 5): the owner's paper "Request for Work Related
  Travel" form, the first of the "templates needing a specific interface". No Freshservice.
  Route: department head (approval chain) → central office administrator (team chosen by district)
  → Business Administrator → Superintendent → business office task (budget account, PO, and for
  $150 or more the Board's decision and date; team chosen by district) → employee told → employee's
  "After the trip" task (actual cost, receipts, PD report; no recurring reminders, due 21 days after
  `to_date`, the district's business office told if late) → business office
  reconciles → done. Board "Not approved" ends it as rejected. On a real site the six teams
  (`SeedData.StarterTeams`) are empty and the rates are 0, so it seeds as an unpublished draft; in
  Development they hold the example people. Defaults (today, name, "1 room") are applied when the
  form opens, not on submit.
- **Rates** (`Rates` table, Admin → Rates): admin-kept amounts exposed to *form* formulas as the
  reserved name `rates` (`rates.mileage`), supplied with the ambient values by
  `SchoolCalendar.RatesAsync` (cached 60 s; `InvalidateRates` on change). A fixed rate on a form is
  a calculated field (`"formula": "rates.mileage"`), so the browser cannot set it, and a submitted
  request keeps the amounts it was submitted with. A new site seeds the travel rates at 0 and the
  form's rules refuse to cost anything with a 0 rate. Changes are audited.
- **Student information** (owner's rule: at most student ID, first name, last name, school,
  grade, academy; access by a per-person flag an admin sets). `Students` table, loaded by CSV on
  Admin → Student access (replace-all import; only those six columns are read). `AppUser.StudentAccess`
  is the flag — **not** implied by Admin. `FieldTypes.Student`: the browser sends only an ID;
  `AccessService.StudentsForAsync` looks it up and `FormEvaluator.NormalizeStudent` takes every
  detail from the roster, refusing people without the flag. `/api/students` returns 403 without it
  and never lists the roster (2+ characters, 15 results). Viewers without the flag see
  "Restricted" on the request, print view and CSV export; student answers are never copied to a
  new request; the audit log records access changes and import counts, never student details.
  Not covered: text a builder puts into a ticket, email or approval message is seen by whoever
  receives it. Open: whether the roster should come from the owner's SQL Server instead of CSV.
- **Accessibility**: `tests`-independent check with axe-core (WCAG 2.1 A/AA) over the main screens
  found only contrast, unlabeled builder inputs and non-underlined text links; all fixed, zero
  violations now. Keep: `--muted` at #5a607a or darker, `row()` in `builder.js` ties labels to
  controls, the skip link and `main#main`, `aria-invalid`/`aria-describedby` set in
  `form-runtime.js apply()`. Accessibility mode = cookie `sf_a11y` → `html.a11y` (CSS only),
  toggled on `/Account/Display`. The flow canvas is still mouse/touch only.
- **Waiting on a ticket**: use `IN(ticket.status, 4, 5)`, not `ticket.status >= 4` — custom
  Freshservice statuses have numbers above 5 and would end the wait. Create/Update ticket accept
  custom status numbers (6–999). In the travel template Resolved = Board approved, Closed without
  resolving = not approved (owner keeps such tickets in a custom "Travel Pending" status).
- **Form opening dates** (`FormDefinition.OpensOn/ClosesOn`, `ClosedMessage`) are part of the
  definition, so they are versioned and take effect on publish. Enforced on the Start page and in
  `forms/submit`; resubmitting a sent-back request is deliberately still allowed after closing.
- **Version history**: `WorkflowVersions` gets a row on every publish (latest 50 kept);
  `POST api/builder/{id}/versions/{n}/restore` copies it into `DraftJson` only. Export/import are
  page handlers on `Pages/Builder/Index` (`?handler=Export`, `Import`); an import is always a new
  draft owned by the importer, never published.
- **Retention** (owner's decision: requests and files 7 years, audit log forever):
  `RetentionService.PurgeAsync`, run daily by `RetentionWorker`. Deletes runs that are
  Completed/Rejected/Cancelled/Failed and untouched (`UpdatedAt`) for `Retention:Years` (default 7,
  0 = off) with their events, approval tasks, comments, attachment rows and files; also
  notifications and drafts of that age. Never deletes a run still in progress, never touches
  `AuditEntries`, and writes a `retention.purged` audit entry. Database rows go before files.
- **Freshservice**: `IFreshserviceClient` has a live client and `SimulatedFreshserviceClient`
  (used when no domain/API key is configured; timeline entries are prefixed `[Simulated]`).
- **Permissions** (owner: staff only sign in; admins keep the ability to approve on someone's
  behalf). Role = highest of hand-set (`Users.Role`), a registered Entra group (`DirectoryGroups`,
  matched against the token's `groups` claim), an Entra app role (`roles` claim) and `Auth:Admins`
  — see `RoleResolver`, applied by `RoleClaimsTransformation`. **All workflow/request access goes
  through `AccessService`** (`CanSubmit`, `CanManage`, `CanViewRunAsync`, `CanManageRunAsync`); do
  not add `User.IsBuilder()` checks for data access — Builder alone grants nothing over a workflow
  the person does not own. `Workflow.Owners` and `Workflow.AudienceGroups` live on the row, not in
  the definition JSON, so they apply immediately and are not versioned. Things a person may not
  touch return 404, not 403. Anything that changes who can do what, or overrides the normal flow,
  is recorded with `AuditService`.

## Conventions that matter

- **No inline scripts or styles-in-JS-HTML.** The CSP is `script-src 'self'`. Pages pass data via
  `data-` attributes or `<script type="application/json">` (with `<` escaped). JS builds DOM with
  `SF.h(...)` / `textContent`, never `innerHTML` with user data.
- **API controllers** carry `[AutoValidateAntiforgeryToken]`; `SF.api` sends the token header.
  Signed-out `/api` calls get 401 from middleware in `Program.cs` (the webhook is the one anonymous
  API and checks its own secret).
- **Never trust the browser for identity or lists**: department/location names come from the
  server's tables; `submitter` is set server-side; webhook payloads cannot set reserved names.
- **Reserved formula names**: `submitter, ticket, approval, approvals, run, event, chain, attachments, rates` plus
  keywords — see `DefinitionValidator.Reserved` (mirrored in `builder.js` `RESERVED`).
- **Adding a field type**: `FieldTypes` → `FormEvaluator` normalisation → `form-runtime.js`
  control → `builder.js` palette/inspector → `Ui.Display` if it needs special display →
  `FormCopy` if it should be skipped in summaries. **Adding a flow step**: `NodeTypes` (+ `Ports`)
  → `WorkflowEngine.ExecuteAsync` → `DefinitionValidator` → `builder.js` (`NODE_TYPES`,
  `nodeSummary`, inspector).
- **Mobile**: list tables use `class="table stack"` with `data-label` on cells so they stack on
  phones. Use `minmax(0, 1fr)` for grid tracks; plain `1fr` caused real overflow bugs.
- Dates/"today" use the server's time zone; database timestamps are UTC (`Ui.Local` for display).
- Money/number display is fixed to en-US (`Values.DisplayCulture`).

## Not verified against real systems

Everything below works against simulators/fakes only. Treat first contact as a test, not a formality.

1. **Freshservice live API** — least certain: requester name search filter syntax, the
   `user_id` asset filter for "my devices", `parent_location_id` on locations, the service
   catalog `place_request` payload, the webhook placeholder `{{ticket.id_numeric}}`, and adding
   files to a ticket (multipart `PUT /api/v2/tickets/{id}` with `attachments[]` parts).
2. **Entra ID sign-in** end to end (needs a real tenant).
3. **Microsoft Graph email** (needs `Mail.Send` application permission; recommend an application
   access policy limiting it to the sender mailbox).
4. **SQL Server migrations** — the owner's first start on their SQL Server (Nov 16, 2026, site
   `forms.bergen.org`, folder `X:\wwwroot\SchoolFlow`) applied all seven migrations through
   `AddRates` without error, run from a console as their own account. Not yet confirmed: the same
   under the IIS app pool's account, and an upgrade of a database that already holds data.

## Open items

Waiting on the owner:

- **SQL Server data.** The owner has existing data on their SQL Server and said they would provide
  it. Unknown: what it contains (staff? departments and heads? budget codes?) and whether
  SchoolFlow should read it live or import it. If it includes departments and their heads, use it
  to fill `Departments` (name + final approvers) instead of manual entry. Ask before designing.
- **Admin templates needing a specific interface.** The owner will add "a few templates" that
  "warrant a specific user interface for best adaptation by staff". The travel form is done (paper
  look + worksheet, fixed rates, per-district route, fully in-app); the owner fills in its
  teams and rates themselves. More templates may follow.
  Need from the owner, per template: what it collects and what should look/behave differently from
  the standard form. Likely shape: a per-workflow custom layout/renderer on top of the same
  definition and `FormEvaluator`, so logic and validation stay shared.

Known gaps (not requested yet, worth raising):

- The webhook is rate-limited (300/min per address); the signed-in APIs are not.
- The app is written for the root of a host name (absolute `/...` links in pages and scripts). It
  does not support running as a sub-folder application; the owner chose its own site.
- Staff-only sign-in is enforced in Entra ("Assignment required"), not in the app. Any signed-in
  person can use lookups, including the Freshservice people search.
- Group membership is read from the sign-in token, so it is as fresh as the person's last sign-in,
  and people in more than ~200 groups get none (no Graph fallback is implemented).
- The owner has Entra groups but did not have their IDs to hand; none are registered yet.
- Reminders, escalation and the missed-ticket check run inside the web process: the IIS app pool
  must be AlwaysRunning (see the deploy guide) or they are late.
- No student/staff picker field — waiting on the SQL Server data.
- Departments cannot be deleted (only switched off); chain templates in use by a workflow cannot
  be deleted.
- The flow builder works on a phone but is cramped; fine on tablet/desktop.
- Repository default branch should be switched to `main` in GitHub settings (it is still the
  original work branch).

## Working agreements with the owner

- The school has a .NET developer on staff who can maintain this; the app is used alongside
  Freshservice and Microsoft 365, not instead of them. Seven-year retention matches state rules.

- They asked for clarifying questions before big features and for bug checking throughout: run
  the unit tests and the browser smoke test after changes, and look at screenshots (desktop and
  phone) rather than relying on measurements alone.
- They follow along from a phone and are not a .NET developer: explain outcomes in plain language,
  and say clearly what was and was not verified.
- Develop on a branch and open a pull request into `main`.
