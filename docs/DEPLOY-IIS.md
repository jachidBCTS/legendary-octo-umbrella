# Deploying SchoolFlow to Windows IIS

This is the step-by-step for a first deployment and for later upgrades. It assumes what was agreed:

- SchoolFlow is **its own IIS site with its own host name** (for example `https://schoolflow.yourschool.org`).
  It does not work as a sub-folder of another site.
- The database is **your SQL Server, using Windows sign-in** (no database password stored anywhere).
- The server is **reachable from the internet**, so Freshservice can send ticket events to it.

Replace `schoolflow.yourschool.org`, `YOUR-SQL-SERVER` and `YOURDOMAIN` with your own values throughout.

> **What has and has not been tested.** The Windows package is built and its tests run on Windows by
> the automated check in this repository (see *Getting the package*), including starting the site under
> IIS and loading a page. Sign-in against your Microsoft tenant, your SQL Server, your Freshservice
> account and email can only be tested on your server — the *First start* section walks through each.
> Do the first deployment at a quiet time and work through that section before telling staff.

## 1. What you need before you start

| Item | Notes |
| --- | --- |
| Windows Server 2016 or later with IIS | The Web Server (IIS) role installed |
| **.NET 8 Hosting Bundle** | Download "Hosting Bundle" from <https://dotnet.microsoft.com/download/dotnet/8.0> and install it **after** IIS. Then run `iisreset` (or restart the server). This is what lets IIS run the site |
| A host name and DNS record | `schoolflow.yourschool.org` pointing at the server |
| A TLS certificate for that host name | **Required.** Microsoft sign-in will not work over plain http |
| An empty SQL Server database | Created by your DBA (section 3) |
| A Microsoft Entra app registration | Section 5 |
| A Freshservice API key | From a dedicated Freshservice agent account |
| Outbound internet from the server (port 443) | To `login.microsoftonline.com`, `graph.microsoft.com`, `yourschool.freshservice.com` and `api.gsa.gov` (travel per diem rates) |
| Inbound 443 from the internet | For staff off-site and for the Freshservice webhook |

## 2. Getting the package

The simplest way, with nothing to install: open the repository on GitHub → **Actions** → the latest
successful **Build, test and package** run → download the **SchoolFlow-windows** artifact. It is a zip
of exactly the files to put on the server.

Or build it yourself on any machine with the .NET 8 SDK:

```
dotnet publish src/SchoolFlow.Web -c Release -r win-x64 --self-contained false -o publish
```

## 3. Database (for your DBA)

Create an empty database and give the site's Windows account rights to it. SchoolFlow creates and
upgrades its own tables on start-up, so the account needs `db_owner` **on this one database only**.

```sql
CREATE DATABASE SchoolFlow;
GO
CREATE LOGIN [YOURDOMAIN\svc-schoolflow] FROM WINDOWS;   -- see "Which account?" below
GO
USE SchoolFlow;
CREATE USER [YOURDOMAIN\svc-schoolflow] FOR LOGIN [YOURDOMAIN\svc-schoolflow];
ALTER ROLE db_owner ADD MEMBER [YOURDOMAIN\svc-schoolflow];
GO
```

**Which account?** It is whatever identity the IIS app pool runs as (section 4):

| App pool identity | SQL login to create |
| --- | --- |
| A domain service account, e.g. `YOURDOMAIN\svc-schoolflow` (**recommended**) | That account |
| The default `ApplicationPoolIdentity`, SQL Server on a **different** machine | The web server's computer account: `YOURDOMAIN\WEBSERVERNAME$` |
| The default `ApplicationPoolIdentity`, SQL Server on the **same** machine | `IIS APPPOOL\SchoolFlow` |

If your DBA would rather not give the site `db_owner`: they can apply the schema themselves from a
script (see *Database and upgrades* in the README) and grant only `db_datareader` and `db_datawriter`.
They must then re-run the script for every upgrade **before** the new version is started.

Add this database to your normal SQL Server backups.

## 4. IIS site

1. **Folder.** Create `C:\inetpub\SchoolFlow` and unzip the package into it. You should see
   `SchoolFlow.Web.exe`, `web.config` and a `wwwroot` folder directly inside.
2. **Data folder.** Create `C:\inetpub\SchoolFlow\App_Data`. SchoolFlow keeps its sign-in keys here
   (`App_Data\keys`) and the files people upload (`App_Data\uploads`). **Add this folder to the
   server's backups**: uploaded files are not in the database. If you would rather keep uploads on
   another drive, set `Attachments:Path` in the settings file to that folder and give the app pool
   account Modify on it.
3. **App pool.** IIS Manager → Application Pools → Add: name `SchoolFlow`, .NET CLR version
   **No Managed Code**. Then in its **Advanced Settings**:
   - *Identity*: your service account (recommended), or leave as `ApplicationPoolIdentity`.
   - *Start Mode*: `AlwaysRunning`
   - *Idle Time-out (minutes)*: `0`

   The last two keep the site awake. If the pool is allowed to go idle, queued notification emails
   wait until the next person opens the site.
4. **Site.** Sites → Add Website: name `SchoolFlow`, application pool `SchoolFlow`, physical path
   `C:\inetpub\SchoolFlow`, binding **https**, port 443, host name `schoolflow.yourschool.org`, your
   certificate, *Require Server Name Indication* ticked if the server hosts other sites.
   Optionally add an http (port 80) binding for the same host name; SchoolFlow redirects it to https.
5. **Permissions.** The app pool's account needs **Read** on `C:\inetpub\SchoolFlow` and **Modify** on
   `C:\inetpub\SchoolFlow\App_Data`. From an administrator PowerShell (use your service account's
   name instead if you set one):

   ```powershell
   icacls C:\inetpub\SchoolFlow /grant "IIS AppPool\SchoolFlow:(OI)(CI)RX"
   icacls C:\inetpub\SchoolFlow\App_Data /grant "IIS AppPool\SchoolFlow:(OI)(CI)M"
   ```

## 5. Microsoft Entra app registration (sign-in)

In the Entra admin center → App registrations → New registration:

1. Name `SchoolFlow`; *Accounts in this organizational directory only*.
2. Redirect URI, platform **Web**: `https://schoolflow.yourschool.org/signin-oidc`
3. After creating it: **Authentication** → Front-channel logout URL:
   `https://schoolflow.yourschool.org/signout-callback-oidc`
   On the same page, under *Implicit grant and hybrid flows*, tick **ID tokens**. Without it
   sign-in can fail with error `AADSTS700054` ("response_type 'id_token' is not enabled").
4. **Certificates & secrets** → New client secret. Copy the secret **Value** now (it is shown once).
   Put a reminder in your calendar before it expires — sign-in stops working when it does.
5. From **Overview**, note the *Application (client) ID* and *Directory (tenant) ID*.
6. **Staff only.** Enterprise applications → SchoolFlow → Properties → *Assignment required* = **Yes**,
   then Users and groups → add your staff group. This is what keeps students and anyone else in your
   tenant out; without it, every account in the tenant can sign in, submit forms and use the people
   lookup. Do this before going live.
7. **Groups.** App registrations → SchoolFlow → **Token configuration → Add groups claim** → tick
   *Security groups*, and under *ID* choose *Group ID*. This lets SchoolFlow see which groups a
   person is in, which it uses for roles and for limiting forms to particular groups.

## 6. Settings file

On the server, copy `appsettings.Production.example.json` (in the package) to
**`appsettings.Production.json`** in the same folder and fill it in:

- `Auth:Admins` — your own email, so you are an admin the first time you sign in.
- `Auth:AllowedDomains` — your district email domain(s), e.g. `["bergen.org"]`. Approvers people type into their own approval chain, and stand-ins, must use these.
- `AzureAd` — the tenant ID, client ID and client secret from section 5.
- `Database:ConnectionString` — replace `YOUR-SQL-SERVER`. Leave `Integrated Security=True`.
  `TrustServerCertificate=True` is needed when the SQL Server uses its default self-issued
  certificate; your DBA can tell you if it has a proper one, in which case remove that part.
- `Freshservice` — your domain, the API key, and a webhook secret you make up (16+ characters).
  Leaving `Domain` or `ApiKey` empty runs the site in simulation mode, which is a safe way to do
  the very first start.
- `Gsa:ApiKey` — the api.data.gov key for GSA's per diem rates (travel form). Without it the travel
  form uses the "GSA fallback" amounts on Admin → Rates and flags each request.
- `Email` — leave `Mode` as `Off` until sign-in works; see the README to turn it on.

This file contains secrets. Restrict it to administrators and the app pool account:

```powershell
# Grant first and look at the output; only then remove the inherited permissions. Doing both in one
# command can leave the file with no permissions at all if a name is not recognised, and the site
# then fails to start with "Access to the path ... appsettings.Production.json is denied".
icacls C:\inetpub\SchoolFlow\appsettings.Production.json /grant "Administrators:F" "SYSTEM:F" "IIS AppPool\SchoolFlow:R"
icacls C:\inetpub\SchoolFlow\appsettings.Production.json /inheritance:r
icacls C:\inetpub\SchoolFlow\appsettings.Production.json      # should list those three and nothing else
```

If the site cannot read the file, `icacls <file> /reset` puts the normal inherited permissions back.

## 7. First start — check each of these in order

1. **Start the site** (IIS Manager → SchoolFlow → Start). Browse to
   `https://schoolflow.yourschool.org/healthz` — it should show `ok`. If you get an error page
   instead, go to *Troubleshooting*.
2. **Database.** Ask your DBA to confirm the `SchoolFlow` database now has tables (`Workflows`,
   `Runs`, …). They are created on that first start.
3. **Sign in.** Browse to the site; you should be sent to Microsoft and back, and see the home page
   with the starter workflows. You should see *Build* and *Admin* in the menu (you are in `Auth:Admins`).
4. **Restart survives.** Recycle the app pool, refresh the page: you should still be signed in.
5. **Freshservice.** Admin → Settings → *Test connection*. Then Admin → Locations → *Sync from
   Freshservice*.
6. **A real request, with a file.** Submit the Device Repair form as yourself with a photo attached.
   Check that a ticket appears in Freshservice **and that the photo is on it** (this upload could
   only be tested against a simulator). If the photo is missing, the request's timeline in
   SchoolFlow says why; send me that message. Close or delete the ticket afterwards.
7. **Groups and roles.** Admin → Settings → *Microsoft Entra groups*: register your groups (name and
   Object ID, from Entra → Groups → the group → Overview). Give your IT/admin group the Admin role and
   your workflow-builders group the Builder role. Sign out and in again, then check *People and
   roles* on the same page shows your role "from Entra group …". Once that works you can remove
   yourself from `Auth:Admins` if you prefer to rely on the group, though keeping one named admin
   there is a sensible safety net.
8. **Departments.** Admin → Departments: add your departments and their final approvers before staff
   use any form with a Department field.
9. **Webhook.** Admin → Settings shows the exact URL, header and body to enter in Freshservice's
   Workflow Automator. Trigger it once and check it appears under *Recent events* on that page.
10. **Email**, when you are ready: follow *Email through Microsoft 365* in the README, then
   Admin → Settings → *Send me a test email*.
11. Review the starter workflows (Builder → Workflows). Switch off or delete the ones you do not want
    staff to see, and add owners to the ones you keep (they start with none, so only admins can
    manage them).
12. **Work Related Travel form.** It does not use Freshservice. It arrives as a draft, because it
    needs your people and amounts:
    - Admin → Rates: enter the mileage rate and the three meal rates (they start at 0, and the form
      refuses to cost mileage or meals until they are set).
    - Admin → Departments: each school/department with its Principal or Department Head as final
      approver (step 8). That person is the first approval on a travel request.
    - Admin → Teams: put people in the six teams the form uses — the two Central Office
      Administrators, the Business Administrator, the Superintendent and the two Business Offices.
      A request that reaches a team with nobody in it stops with an error until you add someone.
    - Workflows → *Request for Work Related Travel* → **Publish**.
    - Submit one request of $150 or more as a test and take it all the way through: the four
      approvals, the business office task (budget account, PO, Board decision), the employee's
      "After the trip" task with a receipt attached, and the business office's reconciliation.
    - The **Field Trip Request** form also arrives as a draft: put people in the *Transportation*
      team (they book buses for approved trips), then publish it.
    - A workflow that names a team cannot be published while that team is missing or empty.

13. **Student information**, if any form will use a *Student* field: Admin → Student access. Import
    the roster as a CSV file with the headings Student ID, First Name, Last Name, School, Grade,
    Academy (other columns are ignored), then add the staff who may look students up. Nobody has
    this access until they are added, administrators included.

### What is deleted automatically

Once a day SchoolFlow deletes requests that finished and have had no activity for 7 years, with
their uploaded files, history, approvals and discussion (`Retention:Years` in the settings file;
`0` switches it off). Requests still in progress are never deleted and the audit log is kept
forever. This does not reach into your SQL Server or file backups: if the 7-year rule must hold
there too, set the backup retention to match.

### Background work — keep the site running

SchoolFlow sends email, approval reminders and escalations, and checks Freshservice for missed
ticket events from inside the web site's own process. IIS stops an idle site after 20 minutes by
default, and nothing is sent while it is stopped. On the application pool set **Start Mode =
AlwaysRunning** and **Idle Time-out = 0**, and on the site set **Preload Enabled = True**
(needs the *Application Initialization* IIS feature). Nothing is lost if the site is stopped —
the work is picked up when it starts again — it is only late.

## 8. Upgrading to a new version

The repository's `deploy/` folder has `Update-SchoolFlow.ps1`, which does the steps below for you
(put it next to a `site` folder holding the new package and run it as administrator with
`-SitePath`). It keeps `App_Data`, `appsettings.Production.json` and any existing
`appsettings.json` (saving the new one as `appsettings.json.new`), and if a file cannot be replaced
it leaves the site offline with `app_offline.htm` rather than running half-updated — re-run it once
the file is free. `Install-SchoolFlow.ps1` (first install), `database.sql` and `reset-database.sql`
are in the same folder. The automated check runs the update script against a real IIS site,
including the locked-file case.


1. **Back up the database and the `App_Data` folder** (and keep a copy of the current site folder).
2. Put a file named `app_offline.htm` in the site folder. IIS stops the site and shows that file's
   contents to visitors, which releases the locked files.
3. Copy the new package over the old files. **Do not delete or overwrite** `App_Data` or
   `appsettings.Production.json` — neither is in the package, so a normal copy leaves them alone.
4. Delete `app_offline.htm`. The site starts, upgrades the database if needed, and is back.
5. Check `/healthz` and sign in.

To go back: stop the site, restore the database backup and the old folder (including `App_Data`),
start it.

## 9. Troubleshooting

Errors and warnings are written to the Windows **Event Viewer → Windows Logs → Application**
(sources *IIS AspNetCore Module V2* and *.NET Runtime*). For a start-up failure you can also turn on
a log file: in `web.config` set `stdoutLogEnabled="true"`, create a `logs` folder in the site folder
and give the app pool account Modify on it, restart the site, read the file, then turn it off again.

| What you see | Likely cause |
| --- | --- |
| **HTTP 500.19** | The Hosting Bundle is not installed, or was installed before IIS. Install/repair it and run `iisreset` |
| **HTTP 500.31 / 500.32** | The .NET 8 runtime is missing or the wrong bitness. Install the Hosting Bundle |
| **HTTP 500.30** (app failed to start) | Read the Event Viewer entry — it names the cause. The usual ones are the next three rows |
| "Microsoft Entra ID sign-in is not configured" | `appsettings.Production.json` is missing, misnamed (check for `.json.txt`) or has no tenant/client ID |
| "Login failed for user …" | The app pool account has no SQL login/user. The name in the message is the account to grant (section 3) |
| "certificate chain was issued by an authority that is not trusted" | Add `TrustServerCertificate=True` to the connection string |
| "Access to the path … App_Data … is denied" | The app pool account lacks Modify on `App_Data` (section 4, step 5) |
| "This database was created by an early development build…" | The database already has SchoolFlow tables from a pre-release build. Point at a new empty database |
| Microsoft shows **AADSTS50011** (redirect URI mismatch) | The redirect URI in the app registration must match the site's address exactly, including `https` |
| Microsoft shows **AADSTS7000215 / 7000222** | The client secret is wrong or has expired. Create a new one and update the settings file |
| Sign-in loops, or "Correlation failed" | The site is being reached over http, or through a proxy that strips https. Use the https address |
| Everyone is signed out whenever the site restarts | `App_Data\keys` is not writable, so keys cannot be saved |
| Uploading a file says it "could not be saved on the server" | The app pool account lacks Modify on `App_Data\uploads` (or the folder in `Attachments:Path`), or the disk is full |
| A file on a request says it is "no longer available" | The file was deleted from the uploads folder, restored without it, or quarantined by antivirus |
| Large uploads fail with **413** | The file is over IIS's 30 MB request limit. SchoolFlow's own limit is 10 MB per file (`Attachments:MaxFileMb`, at most 25) |
| A person in an Entra group does not get its role, or cannot see a form limited to their group | The groups claim is not switched on (section 5, step 7), the group's Object ID was mistyped when registering it, or the person has not signed out and in since being added. People in more than ~200 groups get no group list from Entra: give them an Entra app role named `Admin` or `Builder` instead |
| Pages load but have no styling | The `wwwroot` folder was not copied |
| Webhook from Freshservice gets **401** | The `X-SchoolFlow-Secret` header does not match `Freshservice:WebhookSecret` |
| Webhook gets **503** | `Freshservice:WebhookSecret` is empty or shorter than 16 characters |
| Webhook gets **429** | More than 300 calls a minute from one address; Freshservice retries on its own |

## 10. Because this server faces the internet

- Keep Windows, IIS and the .NET Hosting Bundle patched. .NET security updates arrive monthly.
- Only ports 443 (and 80 for the redirect) need to be open inbound.
- Sign-in is entirely through Microsoft, so your tenant's MFA and conditional access apply.
  SchoolFlow stores no passwords.
- The only address that works without signing in is the Freshservice webhook
  (`/api/webhooks/freshservice`), which requires the shared secret and is rate-limited, plus
  `/healthz`, which reveals nothing.
- Signed-in people can upload files. Only listed document and image types are accepted, each file's
  content is checked against its type, files are stored under random names outside the web root and
  are only ever sent back as downloads. Keep antivirus scanning enabled on the uploads folder.
- Never set `ASPNETCORE_ENVIRONMENT` to `Development` on this server.
