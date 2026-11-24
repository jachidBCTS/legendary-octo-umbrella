# SchoolFlow / Greenlight — detailed developer notes

The full design notes moved out of CLAUDE.md (Nov 22, 2026) to keep every session light. Read the section you need with a search (grep) rather than the whole file.

## How the pieces fit

- **Definition JSON** (`WorkflowDefinition`): `trigger`, `form.fields[]`, `flow.nodes[]/edges[]`.
  Stored as `Workflow.DraftJson` (being edited) and `PublishedJson` (what runs). Each run keeps the
  definition it started with, so edits never change a request in flight. **Since Nov 19, 2026 that
  snapshot is shared**: `RunDefinitions` (one row per distinct definition, unique SHA-256 `Hash`) and
  `WorkflowRun.DefinitionId`. `WorkflowRun.DefinitionJson` is **not a column**: `DefinitionStore`
  (singleton, in-memory, definitions are immutable) fills it after loading — call
  `DefinitionStore.FillAsync(db, run(s))` wherever a run is read and its definition used (reading it
  unfilled throws, on purpose). New runs get `DefinitionId` in `RunService.StartAsync` (`IdForAsync`).
  `DefinitionStore.MoveLegacy` runs on every start and moves runs that still have their own copy (old
  column "DefinitionJson", now `LegacyDefinitionJson`, emptied) — a one-time cost after the upgrade
  (6,000 runs ≈ 5 s). Retention deletes definitions no run uses (after a day's grace) and `Forget`s them.
  Migration `ShareRunDefinitions` (also indexes `Runs.CreatedAt`, `ApprovalTasks.DecidedBy`).
- **Formulas are evaluated on the server only.** The browser posts current answers to
  `/api/forms/evaluate` and renders what comes back; submit re-runs the same `FormEvaluator`.
  Do not add a JavaScript copy of the formula engine — one source of truth was a deliberate choice.
- **Rule maker** (queue #13, Nov 24, 2026; owner wanted the logic area less technical): `wwwroot/js/builder-rules.js`.
  Every logic box (show when, required when, validation rules, Decision, Continue when, Only start when) is
  "When [all/any] of: [field] [test] [value]"; Calculated fields get "Result: Add up / Same as / Subtract /
  Multiply / Divide / Days between / School days between" (+ round to cents). It only WRITES formula text in
  fixed shapes and READS those shapes back (no evaluation in JS); anything else shows under the folded
  "Advanced: edit as a formula" box, which is the real formula field (smoke test uses it). Opening a
  field never rewrites its formula; only an edit in the simple editor does. Fields offered come from
  `ruleSubjects()` in builder.js (kind per field type; lookups/people only "filled in / empty").
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
- **Change tracking and approval restarts (Nov 17, 2026)**: `RunService.Changes` diffs answers on
  resubmit (and when a task that was *Done* before is completed again) and stores how changed answers
  read before in the reserved context name `_previous` (key → display text; student data never
  stored); the timeline says "(adjusted: …)", the Detail page shows an `.edit-badge`, and Revise/task
  pages get `previousDisplay` so `form-runtime.js` shows "Was: …". `ResubmitGuard` (Core) decides
  whether earlier approvals still count: a **currency** field (or calculated currency) went up, or a
  name read by a condition or an *earlier* approval step's settings changed (the step being returned
  to is excluded — it re-works its approvers itself). An approver can also tick "ask every approver
  again" (`_restart`). Then `ReenterApprovalAsync(..., restartApprovals: true)` clears
  `approvals/approval/chain` and runs again from Start (or from the first approval node if a ticket
  exists, so no second ticket). Approval records now carry `node`.
- **Task send-back / passing on**: `RunService.ReturnTaskAsync` goes back to the latest *Done* task
  step (engine `ReturnToTaskAsync`; steps between run again; the note is prepended to the task's
  instructions, and the task is prefilled from the context) or, if none, to the requester
  (`ChangesRequested`; resubmit re-asks the task). Completing a task rebuilds the `attachments`
  list from the form and every task (`AllFiles`, as resubmit does); when the task was *Done* before,
  files no longer referenced are deleted (rows, then files after the save). A task's usable files
  include every file already on the request, whoever uploaded it, so a stand-in or admin redoing it
  keeps the original receipts. `ForwardAsync`: an assignee hands over their own place, a stand-in
  the place of the person they cover (others on an "all must approve" step stay); an
  owner/admin/escalation replaces everyone; never the requester; `Auth:AllowedDomains`
  applies; chain record and stand-ins updated; audit `approval.forwarded`/`task.forwarded`.
- **People suggestions**: `/api/people?q=` (`PeopleController`, 2+ characters, 20 results, allowed
  domains only). Any `<input data-people>` gets a list **under the box** from `common.js` (one shared
  `ul.people-pop` combobox listbox, Up/Down/Enter/Escape; not a `<datalist>`, which browsers draw beside
  the box — owner, Nov 19, 2026). `data-people="multi"` completes the last of several addresses;
  `data-people-add="<textarea id>"` appends to a list.
- **Owner's UI preferences (Nov 19, 2026):** fold-out (accordion) sections wherever it makes sense, tight
  spacing, and instructions behind an ⓘ next to their heading or label rather than as paragraphs.
  **Tips**: `SF.tip(content)` / `SF.tipify(root)` in `common.js`; Razor pages mark instruction paragraphs
  with `data-tip` (`data-tip="page"` = beside the page title), scripts' `.b-help`/`.sf-help` are converted
  automatically (a MutationObserver catches screens drawn later; `.no-tip` keeps a live hint as text).
  Status messages, errors and empty states stay visible — only instructions become tips. Home: form groups
  are `details[data-fold]` (folds remembered per device), recent requests in the right column (`.home-grid`).
  Departments are folded cards like Teams.
- **Starting lists**: Admin → Departments "Add departments from the staff list" (`IStaffDirectory.DepartmentsAsync`,
  DISTINCT Department; added **switched off** until an admin sets the final approver — owner's choice).
- **Locations come from the ticket form (owner, Nov 20, 2026):** the owner's Freshservice *Locations table* is
  out of date and must not be used; the list that routing uses is the choices of the ticket form's **Location**
  custom dropdown (~85 names). `LocationSync.RunAsync` reads `TicketFieldsAsync`, picks the field by label/name
  (`Freshservice:LocationField`, default "Location"; a custom field with choices before a default one), adds new
  choices (`Location.TicketChoice`, migration `LocationTicketChoice`; name not editable here), switches off
  **everything** not in the list (hand-added, old-table rows, duplicates such as staff-side "Delta Program-RIH"),
  never deletes, and leaves a listed location an admin switched off alone. The "Add the district's locations"
  button was removed (it re-added staff-side names); `StartingLocations` is kept but unused by pages. Districts
  are set by hand per row (no reliable name match to the staff list).
- **Testing mode (owner, Nov 20, 2026):** the site is not live yet; only the owner tests it, and test data may be
  cleared. At go-live the owner will say so, and existing requests etc. are to be scrapped (help them then).
- **Read-only fields, districts, staff field (Nov 19, 2026):** `FieldDef.ReadOnly` (any answer-holding type
  except calculated/perdiem; `IsReadOnly`, `[JsonIgnore]`): `FormEvaluator` **never uses the browser's value** —
  it takes `locked` (answers already on the request: resubmit, revise evaluate, task re-do, student restore) or
  else the field's `Default`; the checker warns if there is no default; never copied (`FormCopy`). Browser:
  `lockField` (readOnly/disabled, "Filled in for you"), and the server's value is applied on every evaluation.
  `Location.District` (migration `AddLocationDistrict`; Admin → Locations column — the list is edited in one go and saved
  with one button, owner Nov 20, 2026: `form[data-batch]` in `common.js` marks changed rows, counts them in a sticky
  `.save-bar`, warns before leaving; `OnPostSaveAsync` is all-or-nothing and only checks *new* names for clashes). Location and staff fields
  may set `District` (fixed code) or `DistrictField` (another field: a district question — full names map to codes
  via `Students:Match:Districts`, `SchoolCalendar.DistrictAliases` — or a location/staff answer). Checked on the
  server (`PlaceDistricts`, `FormEvaluator.WantedDistrict`); `/api/locations` returns `district` + `districts`
  aliases so `form-runtime.js` narrows the list. `FieldTypes.Staff`: browser sends an email; `StaffLookup.ForAsync`
  (singleton, 2-min cache, also looks up the submitter so `default: submitter.email` works) gives `StaffDirectory`;
  stored `{ email, name, title, location, department, district }` (several places joined with ", "); search
  `/api/staff?q=&district=`. Allowed in tasks too. In text a staff answer reads as the name; in settings that name
  people (approvers, assignees, notify to, escalate to, due notify, ticket requester/CC, requester lookup) the engine
  uses `Template.RenderAddresses` (`Values.ToAddressText`), so `{{requester}}` gives the email. Resubmit compares staff
  answers by email; never copied from someone else's request; the builder preview checks the staff list too.
- **Categories (Nov 19, 2026):** `Categories` table (`WorkflowCategory`, Admin → Categories: add, rename — renames
  every workflow's `Category` —, order, remove → "Other"). `Workflow.Category` stays a name. Filled once from
  existing workflows on start (`Categories.EnsureFromWorkflows`). Builder Settings picks from the list; Home
  orders groups by it.
- **Letterhead (Nov 19, 2026):** `FormImages` (one per workflow, bytes in the database, migration
  `AddCategoriesAndLetterheads`), admins upload in the builder's Settings tab (`POST/DELETE api/builder/{id}/letterhead`,
  PNG/JPEG/GIF/WebP by magic bytes, 2 MB, **no SVG**), served by `/forms/{id}/letterhead?v=` (`LetterheadController`,
  any signed-in person). Shown on Start, Revise, the builder preview and the Print page. Not versioned, not exported.
- **Confirmations (Nov 19, 2026):** `SF.toast` is a large green/red bar at the top; `SF.toastAfterLoad` carries one
  to the next page (submit, resubmit, task done). `.flash.ok/.bad` have an icon and stand out.
- **Zip download**: `GET /files/run/{id}/zip` (`FilesController.Zip`, built in a temp file, names
  de-duplicated); shown on Detail when a request has 2+ files. Builder: `duplicateNode`, `undo()`
  with `remember()` before every structural change (30 snapshots of `state.def`).
- **Journey / "train stop" track (Nov 18, 2026)**: `Services/Journey.cs`. `Journey.Build` walks the
  flow from Start (conditions and set-variables evaluated against the context; after the current
  step a condition reading something not yet answered takes "true"; people steps follow
  "approved"/"next") and returns `JourneyStop`s: Submitted, every approval/task (an
  `approval_chain` expands into its steps from `context.chain`; not reached yet = one stop),
  help-desk steps, then Completed / Not approved / Cancelled. States come from the `ApprovalTasks`
  rows (latest per node). Read-only — it never changes a run. The current stop shows whose desk
  (`Journey.WaitingOn`: team named in the step's setting + people, stand-ins, "all must approve"
  progress). `WaitAge.Of` = school days since the step was reached: fresh 0–1, aging 2–3 or due
  within 2 days, delayed 4+/escalated/overdue. Lists (`/Requests`, Home, `/Approvals`) use
  `Journey.CurrentStopsAsync` + `_CurrentStop.cshtml`. Tile pictures: `Ui.WorkflowIcon` (whole-word
  match on name/category). Animations (track check draw-in, current-stop pulse, bell ring, clock
  tick, tile tilt) are all off under `prefers-reduced-motion` and `html.a11y`. `.table.stack` is
  `display: table` on wide screens (the general `.stack` grid class had broken column alignment).
- **GSA per diem (Nov 18, 2026)**: field type `perdiem` (`FieldDef.PerDiem` = {zip, from, to} field
  keys). Owner's choices: destination by **ZIP code**; hotel rate **capped** at the GSA limit (district
  rule: the conference hotel may exceed it — `conference_hotel` tick box, documents then required);
  first/last day meals **75%**; GSA unreachable → **standard rate, flagged** (timeline warning + "Standard
  rate used" on the answer). The web app pre-fetches (`PerDiemService.DirectoryForAsync`, cached 24 h,
  failures 2 min, 8 s timeout) so `FormEvaluator` stays network-free; `PerDiemDirectory.Quote` builds
  the stored value {zip, city, county, state, meals, first_last_day, lodging = highest monthly limit over
  the nights, fiscal_year (Oct–Sep), standard, source gsa/simulated/fallback, note}. Source:
  `GsaPerDiemClient` (`Gsa:ApiKey`, `GET v2/rates/zip/{zip}/year/{fy}`) when a key is set, else
  `SimulatedPerDiemSource` in Development, else none (fallback with "not set up"). Fallback amounts are
  rates `gsa_standard_meals`/`gsa_standard_lodging` (seeded 68/110, cannot be removed). The fiscal year always comes
  from the travel dates. **A year GSA has not published yet** (rates come out each August; owner noticed Nov 20, 2026):
  `DirectoryForAsync` tries earlier years down to the current one (not after an outage — `LookupAsync` returns
  `Failed`), stores `PerDiemArea.RatesYear` → answer `rates_year` + a "for reference" note; 404/400 from GSA = "none";
  "none" is cached 4 h. `SimulatedPerDiemSource` follows the same publishing schedule. **Business office rule (owner,
  Nov 20, 2026): such a request cannot be submitted when the rates are needed** — `FormEvaluator.CheckPerDiem`: a per
  diem field that is required (`required` / `requiredWhen`) errors when its answer has `rates_year`; the Travel template's
  `gsa` field has `requiredWhen` = meals or lodging nights claimed. An outage still uses the standard rate, flagged. Only mileage is
  district-kept now; the old `meal_*` rates are no longer seeded. **Not verified against the real GSA
  API** (the reply shape in `GsaPerDiemClient.Parse` follows GSA's documentation; this workspace cannot
  reach api.gsa.gov). An existing site keeps its old Travel workflow until the new one is imported.
- **Performance (Nov 19, 2026, measured with ~6,000 runs / 7,500 approvals / 18,000 notifications):** every
  page 4–14 ms, Reports 40 ms, CSV export of 5,000 runs 0.43 s (was 1.2 s), ≤ 9 queries a page. Keep:
  list pages read `WorkflowRun.ListColumns` (no answers); department/location lists are cached 30 s in
  `SchoolCalendar` (`InvalidateLists` from the admin pages' `OnPageHandlerExecuted`); student lookups by
  number are cached 2 min (`CachedStudentRoster`, wraps the SQL roster only; searches are not cached);
  exports key parsed definitions by `DefinitionId`. Install script sets Preload Enabled and a 3 a.m.
  recycle instead of every 29 h.
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
- **Layout fields**: `section`, `page`, `worksheet` and `break` hold no answer — test with
  `FieldTypes.IsLayout`, not `== Section`. `break` (Nov 19, 2026) ends the grid row so the next field
  starts on a new line; `format: "line"` also draws a thin rule (CSS `.sf-break`). The builder's
  "Add a field" list is grouped (`FIELD_GROUPS` in `builder.js`, `<details class="pal-group">`). A `page` field starts a new page; paging is entirely in `form-runtime.js`
  (`pageOf`, `showPage`, `nextPage`): the server still evaluates the whole form, and *Next* just
  asks the server and refuses to move while the current page has errors.
- **Worksheet** (`FieldTypes.Worksheet`, layout-only): `FieldDef.Rows` (`WorksheetRow`: number,
  item, description with `{field_key}` placeholders, cost field key). It only *arranges* ordinary
  fields of the same form — values, formulas and checks stay on those fields, so `FormEvaluator`
  is untouched. `form-runtime.js` builds held fields "compact" (`buildField(field, true)`, named by
  `aria-label`) and `buildWorksheet` places them. `FormDefinition.Style = "paper"` is CSS only
  (`.sf-paper`; answers sit close above their underline — padding 2px, no min-height — while
  accessibility mode restores boxed inputs). Intro lines that all start with `•` render as a notice list.
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
  notification recipients to district domains (outside notifications were requested). Follow-up:
  people who must sign in to act (approvers, task assignees, escalation) must be district addresses
  when `AllowedDomains` is set — `WorkflowEngine.ResolveActingPeople` fails the step otherwise, and
  `DistrictAddresses.Check` flags the form answer that chose them; "Send notification" and
  overdue-notice recipients are deliberately not limited.
- **Server scripts** live in `deploy/` (Install, Update, database, reset). The CI IIS job runs
  `Update-SchoolFlow.ps1` against the live test site, including a locked file (must exit non-zero
  and leave `app_offline.htm`) and a re-run (must keep `appsettings.json` and come back up).
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
  Starter teams: `SeedData.StarterTeams` (Transportation + the six travel teams). Admin → Teams shows
  each team folded (`details.team-card`); the one just saved reopens (`TempData["OpenTeam"]`).
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
  grade, academy; access by a per-person flag an admin sets). **The roster is read live from the
  owner's SQL Server** (owner, Nov 18, 2026; the CSV import and the `Students` table were removed —
  migration `RemoveStudentRoster`): `IStudentRoster` (`Services/Students.cs`) — `SqlServerStudentRoster`
  (`Students:ConnectionString`, `Students:View`, `Students:Columns:{StudentId,FirstName,LastName,School,
  Grade,Academy,District}` — the owner's view is `ThingsOfBergen.dbo.ActiveStudents` with StudentNumber, Location,
  [Academy or Major], District (read only to decide who may see a student — see below; never shown or stored); `deploy/student-roster-view.sql`;
  parameterised queries, LIKE wildcards escaped, view/column names checked, lookups in batches of 100), `SampleStudentRoster` in Development (Okafor 20451,
  Okon 20452, Ruiz 20453), `NoStudentRoster` otherwise. Admin → Student access keeps the per-person flag
  and has "Check the connection". `AppUser.StudentAccess` is the flag — **not** implied by Admin.
  `FieldTypes.Student`: the browser sends only an ID; `AccessService.StudentsForAsync` looks it up in the
  roster and `FormEvaluator.NormalizeStudent` takes every detail from it, refusing people without the
  flag. `/api/students` returns 403 without it and never lists the roster (2+ characters, 15 results).
  **Only the student number is stored** (owner, Nov 18, 2026): `StudentData.Minimize` reduces a student
  answer to `{ id }` and drops everything worked out from student details, in `RunService.Stored` (every
  save of a run) and in drafts. `StudentAnswers.RestoreAsync` puts the details back from the live roster
  (re-evaluating the form for derived values) when a run is loaded for the engine (`RunService.LoadAsync`,
  task evaluation), on the request/print page for viewers with the flag, and in CSV exports (one batched
  lookup). A student no longer on the roster shows as "ID n (not on the current student roster)".
  Limitation: a "Set value" step computed from student details is not recomputed after a reload.
  Viewers without the flag see "Restricted" on the request, print view and CSV export; student answers
  are never copied to a new request; the audit log records access changes, never student details.
  Not covered: text a builder puts into a ticket, email or approval message is seen by whoever
  receives it. **Not verified against the owner's SQL Server yet.**
- **Who sees which students (owner, Nov 18, 2026):** the on/off flag stays; `AppUser.StudentScope` =
  `location` (default) | `district` | `all` (migration `AddStudentScope`; Admin → Student access).
  Where a person works comes from the district's **staff list** (`IStaffDirectory` in `Services/Staff.cs`:
  `SqlServerStaffDirectory` over `ThingsOfBergen.dbo.ActiveStaff` — StaffID, FirstName, LastName, Title,
  DistrictShort, Location, Department, WorkEmail; `Staff:ConnectionString` defaults to the roster's;
  `SampleStaffDirectory` in Development). Staff and student place names differ, so
  `Students:Match:Districts`/`Locations` map staff values to roster values (unmapped = as is). The
  district's full list (52 locations; BCTS students use codes 20/300/301/302/70/75) is in
  `deploy/student-location-match.json` and goes into the owner's settings file.
  `StudentScopeService.FilterForAsync(email)` → `StudentFilter` (null = no access; empty = not on the
  staff list → nobody). Applied to `/api/students` (in the SQL), to form submission (out-of-reach =
  "not found"), and to viewing (`StudentVisibility.MaySee`): people **asked to act on the request**
  (assignee, stand-in, escalation, decided) see its students even without access (owner's choice); the
  requester with access sees their own; otherwise access must cover every student on it. Exports apply
  it per request. The staff list also feeds email suggestions (`/api/people`; Freshservice only when there
  is no staff list). **Not verified against the owner's SQL Server yet.**
- **Name (owner, Nov 20, 2026; was "Bergen Forms"):** the site is called **Greenlight** everywhere people see it
  (brand mark: a tick on a green badge). Code, namespaces, the `X-SchoolFlow-Secret` webhook header and the workflow
  file marker keep "SchoolFlow"; the host name stays forms.bergen.org.
- **New-notification alerts (owner, Nov 20, 2026):** `common.js` (`SF.alerts`) asks `GET /api/notifications/new?after=`
  (`NotificationsController`; `after=0` = baseline only) every 30 s (60 s in a background tab), updates the bell badge
  (`[data-note-badge]`, always rendered), plays a Web Audio chime and, with browser permission, shows a desktop pop-up
  (`Notification`, tag `greenlight-{id}` so tabs do not double up; the last announced id is shared in localStorage).
  Per-device switches on the Notifications page (`alert-settings.js`, localStorage `sf.alerts.sound/popups/last`).
  Only while a tab is open; real push (service worker + Web Push) not built.
- **Sidebar:** Build and Admin are `<details class="nav-group">`; the group holding the current page is
  open (`data-here="true"`), otherwise `common.js` restores the person's own last click (localStorage).
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
- **Freshservice ticket fields (Nov 19, 2026):** Admin → Settings → "Show ticket fields" lists the ticket form
  (`IFreshserviceClient.TicketFieldsAsync` → `GET api/v2/ticket_form_fields`, `FreshserviceClient.ParseTicketFields`;
  nested choices flattened as "A > B > C") so Create ticket steps can be mapped exactly. Create ticket also sends
  `item_category` (node config `itemCategory`) and `cc_emails` (node config `cc`, split on , ; space).
  **"Report an Issue"** (`deploy/templates/Report an Issue (import in Workflows).json`): the owner's Freshservice
  portal form rebuilt (requester = staff field defaulting to the submitter, CC, subject, location, room, phone,
  the owner's 10 categories, description, files, availability). Its Create ticket step names custom fields by their **labels**. `FreshserviceClient`
  reads the ticket form (cached 30 min) before creating/updating a ticket and `MatchCustomFields` maps each key by
  exact name, simplified name, agent label or **portal label** (`label_for_customers` → `TicketField.CustomerLabel`; the
  owner's "Room #" shows as "Room Number" on the portal, "Extension / Phone #" as "Best number to reach you")
  (`Simple`: lower-case alphanumerics, no `cf_`), matches dropdown values and
  the category levels to the form's choices (`MatchChoice`), turns a location *lookup* field's text into the
  Freshservice location id, and **drops** anything that does not fit with a note (`TicketCreate/TicketUpdate.Notes`
  → timeline warning) rather than letting Freshservice refuse the ticket. Unreadable form = values sent as given.
  The owner's category and location routing lives in Freshservice automators and applies to these tickets
  (source = Portal). "Printer & Copier Supplies" was cut off in their screenshot (choice matching covers spelling). The connection test checks `api/v2/tickets` first (404 = wrong
  `Freshservice:Domain`), then groups (`api/v2/groups`, falling back to `api/v2/agent_groups`).
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
- **Reserved formula names**: `submitter, ticket, approval, approvals, run, event, chain, attachments, rates, task, _previous, _restart` plus
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
- **Look: design direction B ("Bold")** — chosen by the owner Nov 23, 2026 from the Calm / Bold /
  Compact samples. Colours are the `:root` tokens in `app.css` (deep-green sidebar `--side`, header
  band `--band`, emerald `--brand`, bright `--go` for badges and main buttons in the band). Font: Plus
  Jakarta Sans, served from `wwwroot/fonts` (OFL licence file beside it; CSP allows only 'self').
  A page puts its title in the band with `@section Hero { ... }`; the layout renders it first inside
  `<main>`. Used by Home (greeting + three number tiles: waiting on you, in progress, completed this
  month) and Request detail (progress ring, "Now: <stop> · waiting on <who>"). The band keeps
  `.page-head` so the browser test's `.page-head .pill` / `.sub` checks still apply. Form colours
  (`tone-*`, chosen per form in the builder) are unchanged; form icons are now solid tiles of that colour.

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
- **Do not build the server package (the update zip) until the owner asks for it** (owner, Nov 18,
  2026). Build the zip only on request.
- **Push as usual** (owner, Nov 20, 2026): every push makes a new CI package (`Greenlight-update-run-NN`), and
  each package holds *all* changes so far — the owner installs the newest green one. Tell them its run number.

## Nov 23, 2026 batch (queue #1–#6)

- **Names**: Entra display names are "Last_First"; Greenlight shows "First Last" via
  `SchoolFlow.Core.PersonName` (`User.DisplayName()` prefers given_name + family_name claims).
- **Help desk status wording**: a request that finished by opening a ticket (without a Wait-for-ticket
  step) reads "Submitted" (`Ui.HandedToHelpDesk`). `WorkflowRun.WaitingTicketId` is now kept after the
  wait ends, so "did it wait?" is answerable; all lookups also check `Status == WaitingTicket`.
- **Live ticket** on the request page: `TicketView.LoadAsync` → `GetTicketAsync` + `PublicRepliesAsync`
  (`GET /api/v2/tickets/{id}/conversations`, only `private: false`). Unverified against real Freshservice.
- **Signature**: button fills `body[data-my-name]`; print shows the typed name + signed date (form
  fields: request created; task fields: task completed).
- **Signature pictures** (owner, Nov 24, 2026, queue #14): menu → **My Signature**
  (`/Account/Signature`) uploads a PNG (`Signatures.Check`: real PNG magic + IHDR + IEND, 500 KB,
  20–4000 px; recommended 600 × 200 shown on the page). Stored in `SignatureImages` (rows never
  change; `AppUser.SignatureImageId` = current). Owner's choice: if a picture exists, "Sign" signs
  with it automatically. The browser only fills in the name; on submit / task done / resubmit
  `RunService` calls `Signatures.Stamp`, which puts the **signer's own** current picture in the
  run's `_signatures` (field key → picture id, reserved name) only when the answer equals their own
  display name; an unchanged answer keeps its picture (snapshot). Used pictures get `UsedAt` and are
  kept when replaced/removed (unused ones are deleted). Served by `SignaturesController`:
  `/signatures/mine` (own only) and `/signatures/run/{id}/{key}` (`CanViewRunAsync`, else 404).
  Request page and printout show the picture + "signed electronically". Not removed by retention
  when a request is deleted (small; open item if it matters). Not browser-tested yet.

## Nov 23, 2026 batch 2 (queue #7–#11)

- **Daily summary email** (owner): per person "right away" or "once a day at <time>" (6 AM–6 PM,
  30-min steps) on the Notifications page. Non-urgent emails are stored `EmailStatus.Held` and sent as
  one email by `EmailOutboxWorker.ProcessDigestsAsync` once their time has passed, on school days only
  (weekends and `ClosedDays` skipped, owner's choice). `NotifyAsync(..., urgent: true)` skips holding:
  "Changes needed", "Escalated to you", "Overdue". Switching back to right away releases held ones.
- **Chains**: personal → shared template is a move (owner's choice), builders/admins only.
- **Email suggestion list** is re-parented into an open modal `<dialog>` so it shows on top.
- **Review brief** for outside reviewers (Anti-Gravity): `docs/REVIEW.md`.
- **Anti-Gravity review Nov 24** (at 0beb608), all 4 fixed: rule maker saved `field == ""` / empty
  calc when the shown default was never written (now draw, then save); Signature allowed in Task steps;
  staff caches split (`staff-info:` / `staff-places:`); request track names go through `PersonName.Tidy`.
