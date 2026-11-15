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
| Form builder | 14 field types including repeating tables, Freshservice lookups and calculated fields; drag to reorder; half-width layout; live preview |
| Field logic | A spreadsheet-style formula language (about 60 functions) used for calculated fields, *show when*, *required when*, default values and validation rules. Formulas are checked as you type |
| Flow builder | Drag-and-connect canvas: Decision, Approval, Create/Update ticket, Add note, Service request, Look up, Wait for ticket, Set value, End |
| Freshservice | Creates and updates tickets, adds notes, places service catalog requests, looks up requesters/agents/departments/groups/locations/assets, and reacts to ticket events through a webhook |
| Start from a previous request | Per form, a builder can let people prefill a new request from an earlier one — their own, or anyone's (for yearly forms such as field trips). Individual fields can be excluded |
| Running requests | Timeline of every step, approvals inbox, retry of failed steps, cancel |
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
(an admin, a teacher, a principal), a local SQLite database (`schoolflow.db`), three example
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

Roles are managed in **Admin → Settings**: *Staff* (default), *Builder* (create workflows, see all
requests, retry/cancel) and *Admin* (settings and roles).

### Freshservice

- **API key**: use a dedicated agent account whose permissions cover what your workflows do
  (create/update tickets, add notes, read requesters, departments, groups, locations and assets).
- **Webhook** (needed for *Wait for ticket* steps and ticket-triggered workflows): in Freshservice,
  Admin → Workflow Automator → add a *Trigger Webhook* action. **Admin → Settings** in SchoolFlow
  shows the exact URL, header and JSON body to use, and a log of recent events.
- A ticket-triggered workflow runs **once per ticket**, so a workflow that updates the ticket it was
  started by cannot re-trigger itself in a loop.

### Deploying to IIS

1. Install the [.NET 8 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/8.0) on the server.
2. `dotnet publish src/SchoolFlow.Web -c Release -o publish` and copy `publish` to the server.
3. Create an IIS site (HTTPS binding) pointing at that folder; app pool "No Managed Code".
4. Set the configuration values above as environment variables on the app pool or in `web.config`.
5. For SQLite, give the app pool identity write access to the database folder and set an absolute
   path in `Database:ConnectionString` (e.g. `Data Source=D:\Data\schoolflow.db`). For more than a
   handful of simultaneous users, use SQL Server.

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
  requester name search and "my devices" asset filter used by lookup fields.
- **Entra sign-in could not be exercised end to end** in the build environment (it needs a real
  tenant). Startup, the redirect to Microsoft and the role handling are covered.
- **No email notifications.** Approvers see requests in their Approvals inbox and the sidebar badge.
- **Database schema** is created automatically on first run (`EnsureCreated`). There are no
  migrations yet, so a future version that changes tables will need a migration step.
- Any signed-in person can use lookup fields, including the people search. If students will sign in,
  consider limiting sign-in to staff (see above).
- Dates and "today" use the server's time zone.
