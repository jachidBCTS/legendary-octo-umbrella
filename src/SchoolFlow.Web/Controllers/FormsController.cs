using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

public sealed class EvaluateRequest
{
    public int WorkflowId { get; set; }
    /// <summary>Builder preview: evaluate this unsaved form definition instead of the published one.</summary>
    public FormDefinition? Definition { get; set; }
    public JsonElement Values { get; set; }
    /// <summary>True for the first call when a form opens, so defaults are applied.</summary>
    public bool Init { get; set; }
    /// <summary>When editing a request that was sent back: the request, whose own version of the form is used.</summary>
    public int? RunId { get; set; }
}

public sealed class ResubmitRequest
{
    public int RunId { get; set; }
    public JsonElement Values { get; set; }
}

public sealed class TaskRequest
{
    public JsonElement Values { get; set; }
    public bool Init { get; set; }
}

public sealed class SubmitRequest
{
    public int WorkflowId { get; set; }
    public JsonElement Values { get; set; }
    /// <summary>The saved draft this submission came from, if any; it is removed once the request is submitted.</summary>
    public int? DraftId { get; set; }
}

public sealed class SaveDraftRequest
{
    public int WorkflowId { get; set; }
    /// <summary>Null to create a new draft; otherwise the draft to update.</summary>
    public int? DraftId { get; set; }
    public JsonElement Values { get; set; }
}

/// <summary>
/// What the form pages call while someone fills in a form: live checks (evaluate), submit and resubmit,
/// task forms, file uploads, drafts, "start from a previous request", and lookup lists.
/// </summary>
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api")]
public sealed class FormsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly RunService _runs;
    private readonly IFreshserviceClient _freshservice;
    private readonly AttachmentService _attachments;
    private readonly SchoolCalendar _calendar;
    private readonly DomainPolicy _domains;
    /// <summary>A form's answers are small; anything larger than this is refused before it is read.</summary>
    private const int MaxAnswersBytes = 1_000_000;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public FormsController(AppDbContext db, RunService runs, IFreshserviceClient freshservice, AttachmentService attachments, SchoolCalendar calendar,
        DomainPolicy domains, Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _domains = domains;
        _cache = cache;
        _calendar = calendar;
        _attachments = attachments;
        _db = db;
        _runs = runs;
        _freshservice = freshservice;
    }

    /// <summary>
    /// The published form with this ID, if it exists, is switched on and this person is allowed to
    /// submit it. A form limited to other groups looks the same as one that does not exist.
    /// </summary>
    private async Task<Workflow?> LiveFormAsync(int workflowId, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        return workflow is not null && workflow.IsLive && workflow.TriggerType == TriggerTypes.Form && AccessService.CanSubmit(User, workflow)
            ? workflow : null;
    }

    /// <summary>A request the current person submitted that an approver has sent back for changes.</summary>
    private async Task<WorkflowRun?> RevisableAsync(int runId, CancellationToken ct)
    {
        var email = User.Email();
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        await HttpContext.RequestServices.GetRequiredService<DefinitionStore>().FillAsync(_db, run, ct);
        return run is not null && email.Length > 0 && run.SubmitterEmail == email && run.Status == RunStatus.ChangesRequested ? run : null;
    }

    [HttpPost("forms/resubmit")]
    [RequestSizeLimit(MaxAnswersBytes)]
    public async Task<IActionResult> Resubmit([FromBody] ResubmitRequest request, CancellationToken ct)
    {
        var (form, result) = await _runs.ResubmitAsync(request.RunId, Json.ToValues(request.Values), User.Email(), User.DisplayName(), ct);
        if (form is null) return NotFound(new { error = result.Error });
        return new JsonResult(new { ok = result.Ok, runId = result.Ok ? request.RunId : (int?)null, error = result.Ok ? null : result.Error, state = Shape(form) }, Json.Options);
    }

    private async Task<Dictionary<string, object?>> AmbientAsync(CancellationToken ct)
    {
        var ambient = Values.NewDict();
        ambient["submitter"] = RunService.SubmitterRecord(User.Email(), User.DisplayName());
        ambient["rates"] = await _calendar.RatesAsync(ct);
        return ambient;
    }

    private static object Shape(FormResult result) => new
    {
        valid = result.IsValid,
        values = result.Values,
        fields = result.Fields.ToDictionary(kv => kv.Key, kv => new
        {
            visible = kv.Value.Visible,
            required = kv.Value.Required,
            errors = kv.Value.Errors,
            cellErrors = kv.Value.CellErrors,
        }),
    };

    [HttpPost("forms/evaluate")]
    [RequestSizeLimit(MaxAnswersBytes)]
    public async Task<IActionResult> Evaluate([FromBody] EvaluateRequest request, CancellationToken ct)
    {
        FormDefinition form;
        WorkflowDefinition? whole = null;   // for the district-address check; not for the builder's preview
        Dictionary<string, AttachmentInfo>? usableFiles = null;
        Dictionary<string, object?>? locked = null;
        if (request.RunId is int revisingId)
        {
            var revising = await RevisableAsync(revisingId, ct);
            if (revising is null) return NotFound();
            locked = Json.ToValues(revising.ContextJson);
            whole = Json.ParseDefinition(revising.DefinitionJson);
            form = whole.Form;
            usableFiles = await _attachments.UsableForRunAsync(User.Email(), revising, ct);
        }
        else if (request.Definition is not null)
        {
            if (!User.IsBuilder() && !await new AccessService(_db).OwnsAnyWorkflowAsync(User, ct)) return Forbid();
            form = request.Definition;
            form.Fields ??= new List<FieldDef>();
            form.Fields.RemoveAll(f => f is null);
            if (form.Fields.Count > DefinitionValidator.MaxFields)
                return BadRequest(new { error = $"A form can have at most {DefinitionValidator.MaxFields} fields." });
        }
        else
        {
            var workflow = await LiveFormAsync(request.WorkflowId, ct);
            if (workflow is null) return NotFound();
            whole = Json.ParseDefinition(workflow.PublishedJson!);
            form = whole.Form;
        }
        var result = FormEvaluator.Evaluate(form, Json.ToValues(request.Values), await AmbientAsync(ct), applyDefaults: request.Init,
            departments: await _runs.ActiveDepartmentsAsync(ct), locations: await _runs.ActiveLocationsAsync(ct),
            // In the builder's preview there is no real form to upload to, so files are taken as given.
            attachments: usableFiles ?? (request.Definition is not null ? null : await _attachments.PendingForAsync(User.Email(), request.WorkflowId, ct)),
            closedDays: await _calendar.ClosedDaysAsync(ct),
            // The builder's preview is not checked against the roster (and cannot search it without access).
            students: request.Definition is not null ? null : await new AccessService(_db, HttpContext.RequestServices.GetService<IStudentRoster>()).StudentsForAsync(form, Json.ToValues(request.Values), User.Email(), ct,
                HttpContext.RequestServices.GetService<StudentScopeService>()),
            allowedDomains: _domains.Domains,
            perDiem: await HttpContext.RequestServices.GetRequiredService<PerDiemService>().DirectoryForAsync(form, Json.ToValues(request.Values), ct),
            // The staff list is open to anyone signed in, so the builder's preview checks it too (and fills in names).
            staff: await HttpContext.RequestServices.GetRequiredService<StaffLookup>().ForAsync(form.Fields, Json.ToValues(request.Values), ct, User.Email()),
            places: await _calendar.PlacesAsync(ct), locked: locked);
        if (whole is not null) DistrictAddresses.Check(whole, result, _domains.Domains);
        return new JsonResult(Shape(result), Json.Options);
    }

    [HttpPost("forms/submit")]
    [RequestSizeLimit(MaxAnswersBytes)]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length == 0) return BadRequest(new { error = "Your account has no email address, so the request cannot be submitted." });
        var workflow = await LiveFormAsync(request.WorkflowId, ct);
        if (workflow is null) return NotFound(new { error = "This form is no longer available." });
        if (Json.ParseDefinition(workflow.PublishedJson!).Form.ClosedMessage(DateTime.Today) is string closed)
            return BadRequest(new { error = closed + " Your answers have not been submitted." });

        var (form, runId) = await _runs.SubmitFormAsync(workflow, Json.ToValues(request.Values), email, User.DisplayName(), ct);
        if (runId is not null && request.DraftId is int draftId)
        {
            // The draft has become a real request, so it no longer belongs in the drafts list.
            var draft = await _db.FormDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.OwnerEmail == email && d.WorkflowId == workflow.Id, ct);
            if (draft is not null)
            {
                _db.FormDrafts.Remove(draft);
                await _db.SaveChangesAsync(ct);
            }
        }
        return new JsonResult(new { ok = runId is not null, runId, state = Shape(form) }, Json.Options);
    }

    // ---- task steps -----------------------------------------------------------------------

    /// <summary>Live checking of a task's fields while its assignee fills them in.</summary>
    [HttpPost("tasks/{id:int}/evaluate")]
    [RequestSizeLimit(MaxAnswersBytes)]
    public async Task<IActionResult> EvaluateTask(int id, [FromBody] TaskRequest request, CancellationToken ct)
    {
        var result = await _runs.EvaluateTaskAsync(id, Json.ToValues(request.Values), User.Email(), User.IsAdmin(), request.Init, ct);
        return result is null ? NotFound(new { error = "This task is no longer waiting for you." }) : new JsonResult(Shape(result), Json.Options);
    }

    [HttpPost("tasks/{id:int}/complete")]
    [RequestSizeLimit(MaxAnswersBytes)]
    public async Task<IActionResult> CompleteTask(int id, [FromBody] TaskRequest request, CancellationToken ct)
    {
        var (form, result) = await _runs.CompleteTaskAsync(id, Json.ToValues(request.Values), User.Email(), User.DisplayName(), User.IsAdmin(), ct);
        if (form is null) return NotFound(new { error = result.Error });
        if (!result.Ok && form.IsValid) return Conflict(new { error = result.Error });
        return new JsonResult(new { ok = result.Ok, runId = result.RunId, state = Shape(form) }, Json.Options);
    }

    // ---- uploaded files --------------------------------------------------------------------

    /// <summary>Uploads one file for an Attachments field of a live form. The file stays private to the uploader until submitted.</summary>
    [HttpPost("attachments")]
    [RequestSizeLimit(27_000_000)] // above the largest configurable file size, so our own message is shown rather than a bare 413
    [RequestFormLimits(MultipartBodyLengthLimit = 27_000_000)]
    public async Task<IActionResult> Upload([FromForm] int workflowId, [FromForm] string? field, [FromForm] int? runId, [FromForm] int? taskId, IFormFile? file, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length == 0) return BadRequest(new { error = "Your account has no email address, so files cannot be uploaded." });
        FormDefinition form;
        if (taskId is int workingOn)
        {
            // Adding a file while completing a task on a request: the task's own fields decide.
            var target = await _runs.TaskUploadTargetAsync(workingOn, email, User.IsAdmin(), ct);
            if (target is null) return NotFound(new { error = "This task is no longer waiting for you." });
            form = target.Value.Form;
            workflowId = target.Value.Run.WorkflowId;
        }
        else if (runId is int revisingId)
        {
            // Adding a file while editing a request that was sent back: use that request's own form.
            var revising = await RevisableAsync(revisingId, ct);
            if (revising is null) return NotFound(new { error = "This request is not waiting for changes." });
            form = Json.ParseDefinition(revising.DefinitionJson).Form;
            workflowId = revising.WorkflowId;
        }
        else
        {
            var workflow = await LiveFormAsync(workflowId, ct);
            if (workflow is null) return NotFound(new { error = "This form is no longer available." });
            form = Json.ParseDefinition(workflow.PublishedJson!).Form;
        }
        // Uploads are only accepted for a form that actually asks for files.
        if (!form.Fields.Any(f => f.Type == FieldTypes.Attachments && string.Equals(f.Key, field, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { error = "This form does not accept files here." });

        var result = await _attachments.SaveAsync(file, workflowId, email, ct);
        if (result.Attachment is null) return BadRequest(new { error = result.Error });
        return Ok(new { id = result.Attachment.PublicId, name = result.Attachment.FileName, size = result.Attachment.Size });
    }

    [HttpDelete("attachments/{id}")]
    public async Task<IActionResult> DeleteUpload(string id, CancellationToken ct) =>
        await _attachments.DeletePendingAsync(id, User.Email(), ct) ? Ok(new { ok = true }) : NotFound();

    // ---- save and finish later -------------------------------------------------------------

    private const int MaxDraftsPerPerson = 50;
    private const int MaxDraftBytes = 300_000;

    [HttpPost("forms/drafts")]
    [RequestSizeLimit(MaxAnswersBytes)]
    public async Task<IActionResult> SaveDraft([FromBody] SaveDraftRequest request, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length == 0) return BadRequest(new { error = "Your account has no email address, so a draft cannot be saved." });
        var workflow = await LiveFormAsync(request.WorkflowId, ct);
        if (workflow is null) return NotFound(new { error = "This form is no longer available." });
        if (request.Values.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "There is nothing to save." });
        var raw = request.Values.GetRawText();
        if (raw.Length > MaxDraftBytes) return BadRequest(new { error = "This form holds too much to save as a draft." });

        FormDraft? draft;
        if (request.DraftId is int id)
        {
            // Someone else's draft looks exactly like a missing one.
            draft = await _db.FormDrafts.FirstOrDefaultAsync(d => d.Id == id && d.OwnerEmail == email && d.WorkflowId == workflow.Id, ct);
            if (draft is null) return NotFound(new { error = "That draft no longer exists. Save again to keep your answers as a new draft." });
        }
        else
        {
            if (await _db.FormDrafts.CountAsync(d => d.OwnerEmail == email, ct) >= MaxDraftsPerPerson)
                return BadRequest(new { error = $"You already have {MaxDraftsPerPerson} saved drafts. Submit or delete some first." });
            draft = new FormDraft { WorkflowId = workflow.Id, OwnerEmail = email, CreatedAt = DateTime.UtcNow };
            _db.FormDrafts.Add(draft);
        }

        var whole = Json.ParseDefinition(workflow.PublishedJson!);
        var form = whole.Form;
        var answers = Json.ToValues(request.Values);
        // A draft, like a request, keeps only a student's number; the form looks the rest up again when reopened.
        // Done first, so the draft's summary cannot carry anything worked out from student details either.
        SchoolFlow.Core.Forms.StudentData.Minimize(whole, answers);
        var summary = FormCopy.Summary(form, answers);
        draft.Summary = summary.Length > 300 ? summary[..300] : summary;
        draft.ValuesJson = Json.Serialize(answers);
        draft.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { draftId = draft.Id, savedAt = Ui.Local(draft.UpdatedAt).ToString("h:mm tt", Values.DisplayCulture) });
    }

    [HttpGet("forms/drafts/{id:int}")]
    public async Task<IActionResult> GetDraft(int id, CancellationToken ct)
    {
        var email = User.Email();
        var draft = await _db.FormDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.OwnerEmail == email && email != "", ct);
        if (draft is null) return NotFound(new { error = "That draft no longer exists." });
        return new JsonResult(new
        {
            draftId = draft.Id,
            workflowId = draft.WorkflowId,
            values = Json.ToValues(draft.ValuesJson),
            savedAt = Ui.When(draft.UpdatedAt),
        }, Json.Options);
    }

    [HttpDelete("forms/drafts/{id:int}")]
    public async Task<IActionResult> DeleteDraft(int id, CancellationToken ct)
    {
        var email = User.Email();
        var draft = await _db.FormDrafts.FirstOrDefaultAsync(d => d.Id == id && d.OwnerEmail == email && email != "", ct);
        if (draft is null) return NotFound();
        _db.FormDrafts.Remove(draft);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    // ---- start from a previous request ---------------------------------------------------

    /// <summary>
    /// Loads the workflow if "start from a previous request" is switched on for its published form,
    /// and returns the query for the earlier requests this person is allowed to copy from.
    /// </summary>
    private async Task<(FormDefinition Form, IQueryable<WorkflowRun> Runs)?> CopySourceAsync(int workflowId, CancellationToken ct)
    {
        var workflow = await LiveFormAsync(workflowId, ct);
        if (workflow is null) return null;
        var form = Json.ParseDefinition(workflow.PublishedJson!).Form;
        if (!CopyModes.IsOn(form.AllowCopy)) return null;

        var email = User.Email();
        var runs = _db.Runs.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId && r.SubmitterEmail != "" && r.Status != RunStatus.Cancelled);
        // "own" (and anyone without an email) only ever sees their own requests.
        if (form.AllowCopy != CopyModes.Everyone || email.Length == 0) runs = runs.Where(r => r.SubmitterEmail == email && email != "");
        // Other people's requests are offered only once they have been completed (approved and done),
        // never while they are in progress, sent back, rejected or failed.
        else runs = runs.Where(r => r.SubmitterEmail == email || r.Status == RunStatus.Completed);
        return (form, runs);
    }

    [HttpGet("forms/{workflowId:int}/previous")]
    public async Task<IActionResult> Previous(int workflowId, string? q, CancellationToken ct)
    {
        var source = await CopySourceAsync(workflowId, ct);
        if (source is null) return NotFound();
        var (form, runs) = source.Value;
        var email = User.Email();
        q = (q ?? "").Trim();
        if (q.Length > 100) q = q[..100];

        // Search runs in memory over the copyable answers only, never over the raw stored record.
        var recent = await runs.OrderByDescending(r => r.Id).Take(300)
            .Select(r => new { r.Id, r.SubmitterName, r.SubmitterEmail, r.CreatedAt, r.ContextJson }).ToListAsync(ct);
        var items = new List<object>();
        foreach (var run in recent)
        {
            var context = Json.ToValues(run.ContextJson);
            var someoneElse = run.SubmitterEmail != email;
            if (q.Length > 0 && !run.SubmitterName.Contains(q, StringComparison.OrdinalIgnoreCase)
                             && !FormCopy.SearchText(form, context, someoneElse).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new
            {
                runId = run.Id,
                submitter = run.SubmitterName,
                mine = run.SubmitterEmail == email,
                date = Ui.Local(run.CreatedAt).ToString("MMM d, yyyy", Values.DisplayCulture),
                summary = FormCopy.Summary(form, context, fromSomeoneElse: someoneElse),
            });
            if (items.Count >= 50) break;
        }
        return Ok(new { items, everyone = form.AllowCopy == CopyModes.Everyone });
    }

    [HttpGet("forms/{workflowId:int}/previous/{runId:int}")]
    public async Task<IActionResult> PreviousValues(int workflowId, int runId, CancellationToken ct)
    {
        var source = await CopySourceAsync(workflowId, ct);
        if (source is null) return NotFound();
        var (form, runs) = source.Value;
        var run = await runs.Where(r => r.Id == runId)
            .Select(r => new { r.SubmitterName, r.SubmitterEmail, r.CreatedAt, r.ContextJson }).FirstOrDefaultAsync(ct);
        if (run is null) return NotFound();
        return new JsonResult(new
        {
            values = FormCopy.Extract(form, Json.ToValues(run.ContextJson), fromSomeoneElse: run.SubmitterEmail != User.Email()),
            submitter = run.SubmitterName,
            date = Ui.Local(run.CreatedAt).ToString("MMM d, yyyy", Values.DisplayCulture),
        }, Json.Options);
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup(string source, string? q, CancellationToken ct)
    {
        var allowed = User.IsBuilder()
            ? LookupSources.FormSources.Append(LookupSources.Agents)
            : LookupSources.FormSources;
        if (!allowed.Contains(source)) return BadRequest(new { error = "Unknown lookup source." });
        if (q?.Length > 100) q = q[..100];
        try
        {
            IReadOnlyList<LookupItem> items;
            if (source is LookupSources.Departments or LookupSources.Groups or LookupSources.Locations or LookupSources.Agents)
            {
                // Whole lists that are the same for everyone: fetched from Freshservice at most every 15
                // minutes (each fetch can be several pages), then filtered here.
                var all = (await _cache.GetOrCreateAsync("fs-lookup:" + source, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                    return await _freshservice.LookupAsync(source, null, null, ct);
                }))!;
                var term = (q ?? "").Trim();
                items = term.Length == 0 ? all : all.Where(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                items = await _freshservice.LookupAsync(source, q, User.Email(), ct);
            }
            return Ok(new { items = items.Take(200).Select(i => new { id = i.Id, name = i.Name, email = i.Email, detail = i.Detail }) });
        }
        catch (Exception ex) when (ex is FreshserviceException or HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = "Could not load choices from Freshservice. " + (ex is FreshserviceException ? ex.Message : "") });
        }
    }
}
