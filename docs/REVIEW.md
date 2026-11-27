# Daily review brief (bugs and security)

This brief is for an automated or outside reviewer (e.g. Anti-Gravity) doing a daily check of this
repository. Read it first, then review. **Report only; do not change code, push, or open pull requests.**

## What the app is

Greenlight (code name SchoolFlow) is a school workflow app. Staff fill in forms, the forms go through approvals and tasks,
and some open Freshservice help desk tickets. Built with ASP.NET Core 8 Razor Pages and EF Core (SQL Server
in production, SQLite in tests), with Entra sign-in and Graph email. It runs on IIS inside the district.
It is rule-based on purpose: **no AI in workflows**.

```
src/SchoolFlow.Core    formulas, form logic (FormEvaluator), workflow engine, validator, Freshservice client
src/SchoolFlow.Web     Razor Pages, API controllers, services, EF Core + migrations, wwwroot js/css
tests/                 xUnit tests and tests/e2e/smoke.js (Playwright)
deploy/, docs/         install scripts, server guide, NOTES.md (design decisions), QUEUE.md (change log)
```

## What to review each day

1. The changes since the last review: `git log --since="1 day ago" -p` on branch
   `claude/school-agentic-workflow-5wdvee` (or the last commit you reviewed). Focus there first.
2. Then one area of the existing code in more depth, rotating through: access checks, the API
   controllers, the Freshservice webhook, the form evaluator, file uploads, email, and the browser JS.

## Security rules the code must keep (flag any break)

- **Access:** every page and API checks access through `AccessService` and never trusts the browser for
  identity, lists or read-only values. A thing a person may not see returns **404**, not 403.
- **CSRF:** API controllers carry `[AutoValidateAntiforgeryToken]`. The only anonymous API is the
  Freshservice webhook, which checks its own secret (`X-SchoolFlow-Secret`) and is rate-limited.
- **Content Security Policy** is `script-src 'self'`: there are no inline scripts. The JS builds the page
  with `SF.h` / `textContent` and **never uses `innerHTML` with data**. Razor output stays encoded
  (flag any `Html.Raw` on data).
- **Formulas run on the server only** (`FormEvaluator`). The browser's copy of an answer is never used
  for calculated or read-only fields.
- **Run state** changes only through `RunService`, with `run.Version++` on every save (optimistic
  concurrency).
- **Student data:** only the student ID is stored. Details are looked up per viewer and shown only to
  people granted Student Access. Nothing about students goes into logs, audit text or emails.
- **Help desk:** private Freshservice notes must never be shown on request pages (`PublicRepliesAsync`
  keeps only `private: false`).
- **Files:** uploads are stored under random names and served only after an access check.
- **Exports:** CSV cells starting with `= + - @` are prefixed with `'` (protection against formula
  injection). Keep that guard.
- **Secrets** come from configuration and the server's environment, never from the repository. Flag
  any key, token, password or personal email address committed to the repository.

## Before reporting something as a bug

- Check `docs/NOTES.md` (search for the topic). Many choices that look odd were made on purpose by the
  owner and are dated there, for example:
  - names shown as "First Last" built from "Last_First"
  - help desk requests that read "Submitted"
  - daily summary emails that skip weekends and closed days
  - the Locations list that comes only from the ticket form
  - per diem rules
- The site is in testing mode, so test data and simulation mode (with no Freshservice or email) are
  expected.
- Leave out style preferences, renames and large refactors unless they hide a bug.

## How to report

Send one list, most serious first. For each finding give:

- **Severity:** Security / Bug / Minor
- **Where:** `file:line`
- **What goes wrong:** a concrete case, with the input, who does it, and the wrong result
- **Suggested fix:** one or two lines

If nothing is found, say "No findings" and list what you checked. The owner passes findings to
Claude Code, which checks each one against the code before fixing it.
