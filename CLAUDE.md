# Greenlight (code name SchoolFlow) — start here

School workflow app: staff fill in forms, requests go through approvals/tasks, some go to the
Freshservice help desk. ASP.NET Core 8 Razor Pages, EF Core (SQL Server in production, SQLite in
dev/tests), Entra sign-in, Graph email. Rule-based only — **no AI in workflows** (owner's choice).
Shown to people as **Greenlight**; code, namespaces, IIS site/app pool/folder stay "SchoolFlow".

**Detailed notes: `docs/NOTES.md`** (how each feature works, owner decisions with dates, what is
unverified, open items). Search it for the topic at hand (`grep -n "Locations" docs/NOTES.md`)
instead of reading it whole. Add new decisions there, not here. `README.md` is for site admins.

## Layout and commands

```
src/SchoolFlow.Core    formulas, form logic (FormEvaluator), workflow engine, checker, Freshservice client
src/SchoolFlow.Web     Razor Pages, API controllers, EF Core + migrations (Sqlite and SqlServer), wwwroot js/css
tests/SchoolFlow.Tests xUnit      tests/e2e/smoke.js  Playwright browser test (fresh SQLite db)
deploy/                Install/Update scripts, SQL, templates      docs/DEPLOY-IIS.md  server guide
```

- `dotnet test` must stay green. Browser test: run the app in Development with
  `Freshservice__WebhookSecret=test-secret-1234567890` and a new SQLite file, then
  `node tests/e2e/smoke.js <out-dir>` (seeded workflow IDs 1–5 must stay stable).
- Entity change → migration for **both** engines (`--context SqliteAppDbContext` /
  `SqlServerAppDbContext`, output `Migrations/Sqlite|SqlServer`); `MigrationTests` checks they agree.
- CI (`.github/workflows/build.yml`) tests on Windows, starts the package under real IIS, then
  uploads the ready-to-install update package as a run artifact.

## Rules that matter

- CSP `script-src 'self'`: no inline scripts/styles-in-JS; build DOM with `SF.h`, never `innerHTML` with data.
- API controllers: `[AutoValidateAntiforgeryToken]`; all access checks through `AccessService`; things a
  person may not touch return 404. Never trust the browser for identity, lists or read-only values.
- Formulas run **on the server only** (`FormEvaluator`); no JS copy of the engine.
- All run state changes go through `RunService`; keep `run.Version++` on every save.
- Reserved formula names: see `DefinitionValidator.Reserved` (mirrored in `builder.js`).
- Student data: only the ID is stored; access per person; see NOTES before touching it.
- Phones: list tables `class="table stack"` + `data-label`; grid tracks `minmax(0, 1fr)`.
- Owner's UI taste: fold-out sections, tight spacing, instructions behind ⓘ tips, suggestion lists
  under the box. Look: design direction B ("Bold"), see NOTES "Look".

## Working with the owner

- School IT admin, not a .NET developer, follows from a phone: plain language, say what was and was
  not verified. Site is in **testing mode** (data may be cleared; help scrap test data at go-live).
- Ask before big features. Owner's email is private — never send it to outside services.
- **Change queue** (owner, Nov 23, 2026): new requests go into `docs/QUEUE.md` as numbered items —
  reply in one line ("Added as #N"), don't start them, don't run tests. On **"start making the
  changes"**, do all open items as one batch. Questions, quick answers and urgent fixes are done at once.
- **Keep usage low** (owner, Nov 22, 2026): batch work; run the full unit + browser tests and
  screenshots **once per batch** before pushing (not after every small edit); read only the parts of
  files and notes you need; suggest a fresh session when the topic changes.
- Develop on `claude/school-agentic-workflow-5wdvee`; push as usual — every green CI run makes a
  complete update package. Don't watch or report CI runs — the owner checks GitHub Actions
  themselves (Nov 23, 2026). Build a zip only when asked.
