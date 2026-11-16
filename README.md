# SchoolFlow

A build-your-own workflow system for schools, integrated with the Freshservice help desk.

Staff design a **form**, the **field logic** behind it (calculations, show/hide rules, conditional
requirements, validation) and the **flow** that runs after it is submitted (decisions, approvals,
Freshservice tickets). Everything is rule-based and deterministic — there is no AI in the loop and
no data leaves your server except the calls to your own Freshservice account.

Built with ASP.NET Core 8 (Razor Pages), Entity Framework Core and plain JavaScript/CSS with no
front-end dependencies.

## What it does

| Area | Details |
| --- | --- |
| Form builder | 18 field types including time, phone number, typed signature, repeating tables, Freshservice lookups and calculated fields; drag to reorder; half-width layout; live preview |
| Field logic | A spreadsheet-style formula language (about 60 functions) used for calculated fields, *show when*, *required when*, default values and validation rules. Formulas are checked as you type |
| Flow builder | Drag-and-connect canvas: Decision, Approval, Create/Update ticket, Add note, Service request, Look up, Wait for ticket, Set value, End |
| Freshservice | Creates and updates tickets, adds notes, places service catalog requests, looks up requesters/agents/departments/groups/locations/assets, and reacts to ticket events through a webhook |
| Start from a previous request | Per form, a builder can let people prefill a new request from an earlier one — their own, or anyone's (for yearly forms such as field trips). Individual fields can be excluded |
| Approval chains | An *Approval chain* step runs several approvers in order. The builder either locks a workflow to a shared chain template or lets the requester pick a template / build their own chain on the form (and save it for reuse). Each step is "any one" or "all must approve" |
| Departments | Admins keep a list of departments, each with its final approver(s). A request is filed under the department chosen on the form, and that department's final approver is always added to the end of the chain — requesters cannot change or skip it |
| Notifications | In the app (bell with unread count) and by email through Microsoft 365. Sent automatically when an approval is needed and when a request is completed, not approved, cancelled or stuck; a *Send notification* step covers anything else, including outside addresses |
| Locations | A locations table filled from Freshservice with one click (Admin → Locations → Sync), plus any you add by hand. Forms use it through a *Location* field |
| Save and finish later | Every form has a *Save for later* button. The draft appears under *Your drafts* on the home page; once saved, further changes are saved automatically. Drafts are private to their owner and are removed when submitted |
| Attachments | An *Attachments* field lets people upload files (PDF, images, Word, Excel, PowerPoint, text). Files are kept in a folder on the web server, shown on the request to the people allowed to see it, and can be attached to the Freshservice ticket a workflow creates |
| Permissions | Roles from Microsoft Entra groups; each workflow has owners (who can edit it and see its requests) and an optional audience (which groups may submit it); department final approvers see their department's requests; an audit log records changes and admin overrides |
| Running requests | Timeline of every step, approvals inbox, retry of failed steps, cancel. The requester can withdraw a request while it is still waiting |
| Send back for changes | An approver can approve, reject, or *send back for changes* with a note. The requester edits and resubmits, and it returns to the same approver — earlier approvals in a chain are kept |
| Discussion | Each request has a message thread for questions and answers; everyone involved is notified |
| Reminders and escalation | Approvers are reminded when a request sits unanswered (default: every 2 school days, at most 5 times). A step can also escalate to someone else after a set number of school days. Both are set per approval step in the builder |
| Away / stand-in | Anyone can name a colleague to approve in their place for a date range. The request history records who decided and for whom |
| School calendar | Admins list the days the school is closed. "School day" formulas (`DATEADD(…, 'weekdays')`, `WEEKDAYS`, `ISSCHOOLDAY`), reminders and escalation all skip them |
| Reports and export | A Reports page (counts, time to finish, approvals waiting longest) for admins, workflow owners and department approvers; any request list can be downloaded as a spreadsheet (CSV) with one column per question |
| Print | A clean printable / save-as-PDF record of a request with its answers, approvals, history and a signature line |
| Missed ticket events | If Freshservice is connected, requests waiting on a ticket are also checked directly every 15 minutes, so a missed webhook does not leave a request stuck |
| Multi-page forms | A *Page break* splits a long form into steps with Next / Back. Each page is checked before moving on |
| Opening and closing dates | A form can be set to open on one date and close after another (for example, field trip requests for a term). Outside those dates it says when it opens or that it has closed |
| Version history | Every published version is kept (the latest 50). Any of them can be restored as the draft and published again |
| Export / import | A workflow can be downloaded as a file and imported as a new draft — as a backup, or to share between schools |
| Safety | Draft vs. published versions; a request always finishes on the version it started with; publishing is blocked while the checker finds problems |

Formulas are evaluated **on the server only**. The browser sends the current answers and gets back
what to show, the calculated values and any errors, and the same code runs again on submit — so
what a person sees can never disagree with what is stored.

### Starting from a previous request

When creating a workflow (or later under **Form → Form settings**) choose whether people can copy
a previous request: *No*, *their own previous requests*, or *anyone's previous requests*. On the
form, people then get a "Start from a previous request" button with a searchable list.

- Only answers to fields that still exist on the current form are copied. Calculated fields are
  always worked out fresh, and the copied answers go through the same validation as typed ones.
- Tick **Don't copy from previous requests** on a field to leave it out (dates, sign-off tick
  boxes, anything private). Excluded answers are never sent to the other person's browser.
- With *anyone's*, everybody who can open the form can read what others submitted on it, apart
  from excluded fields. Don't use it on forms that collect private details.
- Cancelled requests are not offered.

## Run it locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run --project src/SchoolFlow.Web
```

Open http://localhost:5080. In Development mode you get a sign-in screen with sample people
(an admin, a teacher, a principal), a local SQLite database (`App_Data/schoolflow.db`), four example
workflows, and a **simulated** Freshservice — tickets get pretend numbers and nothing is sent.

```
dotnet test                      # unit and database tests
node tests/e2e/smoke.js          # browser smoke test (needs Playwright and a fresh database; see the file header)
```

## Configuration

Settings come from `appsettings.json`, `appsettings.Production.json` or environment variables
(`Section__Key`). **Keep secrets out of source control** — use environment variables on the server.

| Setting | Purpose |
| --- | --- |
| `Auth:Mode` | `EntraId` (default) or `Dev`. `Dev` is refused unless `ASPNETCORE_ENVIRONMENT=Development` |
| `Auth:Admins` | Emails that are always Admin, so the first admin can sign in and assign roles |
| `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:ClientSecret` | Microsoft Entra ID app registration |
| `Database:Provider` | `Sqlite` (default) or `SqlServer` |
| `Database:ConnectionString` | Connection string for the chosen provider |
| `Email:Mode` | `Off` (default) or `Graph` to send email through Microsoft 365 |
| `Email:From` | Mailbox to send as, e.g. a shared `no-reply@yourschool.org` mailbox |
| `Email:BaseUrl` | Public address of this site, used for links in emails, e.g. `https://flow.yourschool.org` |
| `Attachments:Path` | Folder for uploaded files. Default: `App_Data/uploads` inside the site folder |
| `Attachments:MaxFileMb` | Largest single file, in MB (default 10, at most 25) |
| `Freshservice:Domain` | e.g. `yourschool` or `yourschool.freshservice.com` |
| `Freshservice:ApiKey` | API key of the Freshservice agent account SchoolFlow acts as |
| `Freshservice:WebhookSecret` | 16+ random characters; enables the ticket-event webhook |

Leaving the Freshservice domain or key empty keeps the site in simulation mode (a banner says so).

### Microsoft Entra ID sign-in

1. Entra admin center → App registrations → New registration. Single tenant.
2. Redirect URI (Web): `https://<your-site>/signin-oidc`. Front-channel logout URL: `https://<your-site>/signout-callback-oidc`.
3. Certificates & secrets → new client secret.
4. Set `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:ClientSecret`, and add your own email to `Auth:Admins`.

Everyone in the tenant can sign in, submit forms and approve requests sent to them. To limit who
can sign in at all (for example staff only), turn on *Assignment required* for the enterprise
application in Entra and assign the right groups.

Roles and permissions are described under [Permissions](#permissions) below.

### Permissions

**Roles**

| Role | Can do |
| --- | --- |
| Staff (everyone who signs in) | Submit the forms open to them; see their own requests and drafts; approve what is sent to them; keep their own approval chains |
| Builder | Also create workflows, and manage shared approval chain templates. A builder only sees and edits the workflows they **own** |
| Admin | Everything, everywhere: all workflows and requests, departments, locations, groups, roles, settings, the audit log. Can approve or reject on behalf of the assigned approvers |

A person's role is the **highest** of: the role set by hand under Admin → Settings; any registered
Entra group they belong to; an Entra app role named `Admin` or `Builder`; and the `Auth:Admins`
list in the server settings (a safety net so the first admin can always get in).

**Entra groups.** Under **Admin → Settings → Microsoft Entra groups**, register each security group
SchoolFlow should know about (a name, and the group's Object ID from Entra). A registered group can
grant Builder or Admin to its members, and can be used to limit who may submit a form. For groups
to reach SchoolFlow, switch on the groups claim once in the app registration: **Token configuration →
Add groups claim → Security groups**, with *Group ID* for the ID token. Membership changes take
effect the next time the person signs in. Someone in more than about 200 groups gets no group list
from Entra at all; give those people an Entra app role instead.

**Per workflow** (builder → Settings tab):

- **Owners** — the people who can edit the workflow and see, retry and cancel all of its requests.
  Whoever creates a workflow owns it. Existing workflows with no owners are managed by admins only.
- **Who can submit it** — everyone, or only members of chosen registered groups. People outside
  the audience do not see the form and cannot reach it by address. This applies as soon as it is
  saved; it does not wait for Publish.

**Who can see a request:** the person who submitted it; anyone asked to approve it; the owners of
its workflow; the final approvers of the department it is filed under (they get a *My department*
view on the Requests page); and admins. Attachments follow the same rule.

**Audit log** (Admin → Audit log) records role changes, group registrations, workflow creation,
publishing, deletion, owner and audience changes, final-approver changes, cancelled and retried
requests, and every time an admin approves or rejects on someone's behalf (which is also written on
the request's own timeline). Entries are never edited or removed by the app.

**Staff only.** SchoolFlow lets in anyone your Entra app registration lets in. To keep students and
others out, set *Assignment required* on the enterprise application and assign your staff group —
see the deployment guide.

### Email through Microsoft 365

Email uses Microsoft Graph with the **same app registration as sign-in** (set `Email:TenantId`,
`Email:ClientId`, `Email:ClientSecret` only if you want a separate one).

1. In the app registration: API permissions → Add → Microsoft Graph → **Application** permissions →
   `Mail.Send` → Grant admin consent.
2. Recommended: limit the app to the one sender mailbox with an
   [application access policy](https://learn.microsoft.com/graph/auth-limit-mailbox-access) (or RBAC
   for Applications in Exchange Online). Without it, `Mail.Send` lets the app send as any mailbox.
3. Set `Email:Mode=Graph`, `Email:From` and `Email:BaseUrl`, restart, then use
   **Admin → Settings → Send me a test email**.

Emails are queued in the database and sent by a background worker, so a Microsoft 365 hiccup never
blocks a request; temporary failures are retried (2, 4, 8, 16 minutes) and permanent ones are listed
under Admin → Settings. Everyone can turn their own emails off on the Notifications page and still
see notifications in the app. Addresses outside the sender's domain get the email without a link
(they cannot sign in).

### Departments and approval chains

- **Admin → Departments**: add each department and its final approver(s), and choose whether any
  one of them or all of them must approve. Untick *Active* to stop offering a department on forms.
- **Approval chains** (in everyone's menu): builders and admins create shared templates; anyone can
  save chains of their own.
- In a workflow: add a **Department** field and (optionally) an **Approval chain** field to the
  form, then an **Approval chain** step to the flow. The step runs the chain first — from the form
  field or a fixed template — and then the department's final approver. See the *Field Trip
  Request* starter workflow for a complete example.
- Nobody approves their own request: requesters cannot list themselves, and a requester who is one
  of several final approvers is left out of that step.
- A request keeps the approvers it started with; later edits to a template or department apply to
  new requests only.

### Freshservice

- **API key**: use a dedicated agent account whose permissions cover what your workflows do
  (create/update tickets, add notes, read requesters, departments, groups, locations and assets).
- **Webhook** (needed for *Wait for ticket* steps and ticket-triggered workflows): in Freshservice,
  Admin → Workflow Automator → add a *Trigger Webhook* action. **Admin → Settings** in SchoolFlow
  shows the exact URL, header and JSON body to use, and a log of recent events.
- A ticket-triggered workflow runs **once per ticket**, so a workflow that updates the ticket it was
  started by cannot re-trigger itself in a loop.

### Attachments

- Add an **Attachments** field to a form and set how many files it takes. To send the files on to
  Freshservice, tick **Attach the request's files to the ticket** on a *Create ticket* step.
- **Where files live:** in `App_Data/uploads` on the web server (or `Attachments:Path`), under random
  names. **Back this folder up** along with the database, and keep it across upgrades — the database
  only holds each file's name, size and owner. Windows Defender (or your antivirus) scans the folder
  like any other; a quarantined file simply shows as "no longer available" on its request.
- **What is accepted:** pdf, png, jpg, gif, webp, heic, doc/docx, xls/xlsx, ppt/pptx, txt, csv, rtf,
  up to 10 MB each by default. The first bytes must match the type, so a program renamed to `.pdf`
  is refused. Files are always served as downloads, never displayed inside the site.
- **Who can open a file:** the person who uploaded it; and once submitted, anyone who can open that
  request — the requester, anyone asked to approve it, builders and admins. Everyone else gets
  "not found".
- **Freshservice:** the ticket is created first, then the files are added in a second call. If
  Freshservice refuses them, the ticket and the request still go ahead and the request's timeline
  says which files were left out. Freshservice allows 40 MB of attachments per ticket.
- Files uploaded to a form that is never submitted or saved as a draft are deleted after 30 days.
  Files on submitted requests are kept indefinitely.
- Files are not copied by "start from a previous request".

### Locations

**Admin → Locations → Sync from Freshservice** copies Freshservice's locations into SchoolFlow's own
table, naming nested ones by their full path (`North Campus › Science Wing › Room 101`). Run it
again whenever locations change: it adds new ones, renames changed ones and switches off ones that
were removed in Freshservice. It never touches locations added by hand and never switches one back
on. Forms offer active locations through the **Location** field type; requests that already used a
location keep it.

### Database and upgrades

The database schema is managed with Entity Framework migrations, one set per engine
(`Migrations/Sqlite`, `Migrations/SqlServer`).

- **First start and every upgrade are automatic.** On start-up the site applies any migrations the
  database has not had yet, creating it if it is new. An up-to-date database is left alone.
  **Back up the database before deploying a new version.**
- The account in the connection string therefore needs permission to create and alter tables. If
  your DBA prefers to apply changes by hand, generate a script that is safe to run at any version:

  ```
  dotnet tool restore
  dotnet ef migrations script --idempotent --context SqlServerAppDbContext --project src/SchoolFlow.Web -o upgrade.sql
  ```

- **When the data model changes** (a developer adds a column or table), add a migration for *both*
  engines and commit the generated files:

  ```
  dotnet ef migrations add <Name> --context SqliteAppDbContext    --output-dir Migrations/Sqlite    --project src/SchoolFlow.Web
  dotnet ef migrations add <Name> --context SqlServerAppDbContext --output-dir Migrations/SqlServer --project src/SchoolFlow.Web
  ```

  The test suite fails if the model and the migrations ever disagree, so a forgotten migration is
  caught before it ships.
- A database created by a development build from before migrations existed is refused at start-up
  with instructions (delete it or point at a new one); nothing is changed in it.
- With no connection string, SQLite uses `App_Data/schoolflow.db` inside the site folder. The same
  `App_Data` folder holds the sign-in keys (`App_Data/keys`); keep it across upgrades and never
  publish it.

### Deploying to IIS

**Follow [docs/DEPLOY-IIS.md](docs/DEPLOY-IIS.md)** — the full step-by-step for the first deployment
(IIS site, SQL Server with Windows sign-in, Entra app registration, settings file, first-start
checks), upgrades and troubleshooting. In short:

1. Install the [.NET 8 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0) on the server.
2. `dotnet publish src/SchoolFlow.Web -c Release -o publish` and copy `publish` to the server.
3. Create an IIS site (HTTPS binding) pointing at that folder; app pool "No Managed Code".
4. Set the configuration values above as environment variables on the app pool or in `web.config`.
5. For SQLite, give the app pool identity write access to the database folder and set an absolute
   path in `Database:ConnectionString` (e.g. `Data Source=D:\Data\schoolflow.db`). For more than a
   handful of simultaneous users, use SQL Server.

### Automated checks

`.github/workflows/build.yml` runs on every pull request and on `main`: it runs the tests on Windows,
builds the IIS package, installs IIS on the build machine, starts the package there as a real site
and checks it answers, then attaches the package (**SchoolFlow-windows**) to the run for download.

## Project layout

```
src/SchoolFlow.Core    Formula engine, form logic, workflow engine, definition checker, Freshservice client
src/SchoolFlow.Web     Razor Pages site, JSON APIs, database, builder UI (wwwroot/js, wwwroot/css)
tests/SchoolFlow.Tests Unit tests (formulas, form logic, engine, checker, Freshservice client) and database tests
tests/e2e              Browser smoke test
```

The in-app **Formula guide** (Build → Formula guide) documents the formula language and every function.

## Known limits

- **Verified against a simulator, not a live Freshservice account.** The API client follows the
  Freshservice v2 API and is unit-tested against recorded-style responses, but the first connection
  to your tenant should be tested with a throwaway workflow. The least certain calls are the
  requester name search and "my devices" asset filter used by lookup fields, and the call that adds
  files to a ticket.
- **Entra sign-in could not be exercised end to end** in the build environment (it needs a real
  tenant). Startup, the redirect to Microsoft and the role handling are covered.
- **Email could not be tested against a real Microsoft 365 tenant.** The Graph calls are unit-tested
  against recorded-style responses; use the test-email button after configuring it.
- **The SQL Server migrations have not been run against a real SQL Server** (none was available in
  the build environment). They are generated by the same tooling as the SQLite ones, which are
  exercised by the tests, and the generated script was inspected — but run the first deployment
  against a test database before production.
- Any signed-in person can use lookup fields, including the people search. If students will sign in,
  consider limiting sign-in to staff (see above).
- Dates and "today" use the server's time zone.
