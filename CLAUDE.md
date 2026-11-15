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
workflow IDs (1 Purchase, 2 Repair, 3 Escalate, 4 Field Trip) — keep `SeedData` order stable.

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
- **Freshservice**: `IFreshserviceClient` has a live client and `SimulatedFreshserviceClient`
  (used when no domain/API key is configured; timeline entries are prefixed `[Simulated]`).
- **Roles**: Staff / Builder / Admin in the `Users` table; `Auth:Admins` config emails are always
  Admin. Added to the principal by `RoleClaimsTransformation` (30-second cache).

## Conventions that matter

- **No inline scripts or styles-in-JS-HTML.** The CSP is `script-src 'self'`. Pages pass data via
  `data-` attributes or `<script type="application/json">` (with `<` escaped). JS builds DOM with
  `SF.h(...)` / `textContent`, never `innerHTML` with user data.
- **API controllers** carry `[AutoValidateAntiforgeryToken]`; `SF.api` sends the token header.
  Signed-out `/api` calls get 401 from middleware in `Program.cs` (the webhook is the one anonymous
  API and checks its own secret).
- **Never trust the browser for identity or lists**: department/location names come from the
  server's tables; `submitter` is set server-side; webhook payloads cannot set reserved names.
- **Reserved formula names**: `submitter, ticket, approval, approvals, run, event, chain` plus
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
   catalog `place_request` payload, and the webhook placeholder `{{ticket.id_numeric}}`.
2. **Entra ID sign-in** end to end (needs a real tenant).
3. **Microsoft Graph email** (needs `Mail.Send` application permission; recommend an application
   access policy limiting it to the sender mailbox).
4. **SQL Server migrations** — generated and inspected, never executed on a real SQL Server.

## Open items

Waiting on the owner:

- **SQL Server data.** The owner has existing data on their SQL Server and said they would provide
  it. Unknown: what it contains (staff? departments and heads? budget codes?) and whether
  SchoolFlow should read it live or import it. If it includes departments and their heads, use it
  to fill `Departments` (name + final approvers) instead of manual entry. Ask before designing.
- **Admin templates needing a specific interface.** The owner will add "a few templates" that
  "warrant a specific user interface for best adaptation by staff". Nothing is built for this yet.
  Need from the owner, per template: what it collects and what should look/behave differently from
  the standard form. Likely shape: a per-workflow custom layout/renderer on top of the same
  definition and `FormEvaluator`, so logic and validation stay shared.

Known gaps (not requested yet, worth raising):

- No rate limiting on the webhook or APIs.
- Any signed-in person can use lookups, including the Freshservice people search; if students
  will sign in, restrict sign-in to staff in Entra or add a role check.
- No reminders/escalation for approvals that sit unanswered; no delegation when an approver is away.
- No reporting/export of requests.
- Departments cannot be deleted (only switched off); chain templates in use by a workflow cannot
  be deleted.
- The flow builder works on a phone but is cramped; fine on tablet/desktop.
- Repository default branch should be switched to `main` in GitHub settings (it is still the
  original work branch).

## Working agreements with the owner

- They asked for clarifying questions before big features and for bug checking throughout: run
  the unit tests and the browser smoke test after changes, and look at screenshots (desktop and
  phone) rather than relying on measurements alone.
- They follow along from a phone and are not a .NET developer: explain outcomes in plain language,
  and say clearly what was and was not verified.
- Develop on a branch and open a pull request into `main`.
