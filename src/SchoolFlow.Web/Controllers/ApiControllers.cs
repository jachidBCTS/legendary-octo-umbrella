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

    public FormsController(AppDbContext db, RunService runs, IFreshserviceClient freshservice, AttachmentService attachments, SchoolCalendar calendar)
    {
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
        return run is not null && email.Length > 0 && run.SubmitterEmail == email && run.Status == RunStatus.ChangesRequested ? run : null;
    }

    [HttpPost("forms/resubmit")]
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
    public async Task<IActionResult> Evaluate([FromBody] EvaluateRequest request, CancellationToken ct)
    {
        FormDefinition form;
        Dictionary<string, AttachmentInfo>? usableFiles = null;
        if (request.RunId is int revisingId)
        {
            var revising = await RevisableAsync(revisingId, ct);
            if (revising is null) return NotFound();
            form = Json.ParseDefinition(revising.DefinitionJson).Form;
            usableFiles = await _attachments.UsableForRunAsync(User.Email(), revising, ct);
        }
        else if (request.Definition is not null)
        {
            if (!User.IsBuilder()) return Forbid();
            form = request.Definition;
            form.Fields ??= new List<FieldDef>();
            form.Fields.RemoveAll(f => f is null);
        }
        else
        {
            var workflow = await LiveFormAsync(request.WorkflowId, ct);
            if (workflow is null) return NotFound();
            form = Json.ParseDefinition(workflow.PublishedJson!).Form;
        }
        var result = FormEvaluator.Evaluate(form, Json.ToValues(request.Values), await AmbientAsync(ct), applyDefaults: request.Init,
            departments: await _runs.ActiveDepartmentsAsync(ct), locations: await _runs.ActiveLocationsAsync(ct),
            // In the builder's preview there is no real form to upload to, so files are taken as given.
            attachments: usableFiles ?? (request.Definition is not null ? null : await _attachments.PendingForAsync(User.Email(), request.WorkflowId, ct)),
            closedDays: await _calendar.ClosedDaysAsync(ct),
            // The builder's preview is not checked against the roster (and cannot search it without access).
            students: request.Definition is not null ? null : await new AccessService(_db).StudentsForAsync(form, Json.ToValues(request.Values), User.Email(), ct));
        return new JsonResult(Shape(result), Json.Options);
    }

    [HttpPost("forms/submit")]
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
    public async Task<IActionResult> EvaluateTask(int id, [FromBody] TaskRequest request, CancellationToken ct)
    {
        var result = await _runs.EvaluateTaskAsync(id, Json.ToValues(request.Values), User.Email(), User.IsAdmin(), request.Init, ct);
        return result is null ? NotFound(new { error = "This task is no longer waiting for you." }) : new JsonResult(Shape(result), Json.Options);
    }

    [HttpPost("tasks/{id:int}/complete")]
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

        var form = Json.ParseDefinition(workflow.PublishedJson!).Form;
        var summary = FormCopy.Summary(form, Json.ToValues(request.Values));
        draft.Summary = summary.Length > 300 ? summary[..300] : summary;
        draft.ValuesJson = raw;
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
            if (q.Length > 0 && !run.SubmitterName.Contains(q, StringComparison.OrdinalIgnoreCase)
                             && !FormCopy.SearchText(form, context).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new
            {
                runId = run.Id,
                submitter = run.SubmitterName,
                mine = run.SubmitterEmail == email,
                date = Ui.Local(run.CreatedAt).ToString("MMM d, yyyy", Values.DisplayCulture),
                summary = FormCopy.Summary(form, context),
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
            .Select(r => new { r.SubmitterName, r.CreatedAt, r.ContextJson }).FirstOrDefaultAsync(ct);
        if (run is null) return NotFound();
        return new JsonResult(new
        {
            values = FormCopy.Extract(form, Json.ToValues(run.ContextJson)),
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
            var items = await _freshservice.LookupAsync(source, q, User.Email(), ct);
            return Ok(new { items = items.Take(200).Select(i => new { id = i.Id, name = i.Name, email = i.Email, detail = i.Detail }) });
        }
        catch (Exception ex) when (ex is FreshserviceException or HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = "Could not load choices from Freshservice. " + (ex is FreshserviceException ? ex.Message : "") });
        }
    }
}

public sealed class SaveWorkflowRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Color { get; set; }
    public JsonElement Definition { get; set; }
    /// <summary>Emails of the people who may edit this workflow and see its requests. Null leaves them unchanged.</summary>
    public List<string>? Owners { get; set; }
    /// <summary>Entra group IDs allowed to submit the form; empty for everyone. Null leaves them unchanged.</summary>
    public List<string>? AudienceGroups { get; set; }
}

public sealed class CheckFormulaRequest
{
    public string? Source { get; set; }
    public bool Template { get; set; }
}

[ApiController]
[Authorize(Policy = "Builder")]
[AutoValidateAntiforgeryToken]
[Route("api/builder")]
public sealed class BuilderController : ControllerBase
{
    private const int MaxDefinitionBytes = 500_000;
    public static readonly string[] Colors = { "indigo", "blue", "teal", "green", "amber", "rose", "violet", "slate" };

    private readonly AppDbContext _db;
    private readonly GroupDirectory _groups;
    private readonly AuditService _audit;

    public BuilderController(AppDbContext db, GroupDirectory groups, AuditService audit)
    {
        _db = db;
        _groups = groups;
        _audit = audit;
    }

    /// <summary>The workflow, if this person may manage it. One they may not manage looks the same as a missing one.</summary>
    private async Task<Workflow?> ManagedAsync(int id, bool tracked, CancellationToken ct)
    {
        var query = tracked ? _db.Workflows : _db.Workflows.AsNoTracking();
        var workflow = await query.FirstOrDefaultAsync(x => x.Id == id, ct);
        return workflow is not null && AccessService.CanManage(User, workflow) ? workflow : null;
    }

    private static object IssueShape(IEnumerable<Issue> issues) =>
        issues.Select(i => new { severity = i.Severity, area = i.Area, target = i.Target, message = i.Message });

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: false, ct);
        if (w is null) return NotFound();
        return new JsonResult(new
        {
            owners = w.OwnerList,
            audienceGroups = w.AudienceList,
            groups = (await _groups.AllAsync()).Select(g => new { id = g.ObjectId, name = g.Name }),
            id = w.Id, name = w.Name, description = w.Description, category = w.Category, color = w.Color,
            definition = Json.ParseDefinition(w.DraftJson),
            publishedVersion = w.PublishedVersion, isPublished = w.PublishedJson is not null,
            hasUnpublishedChanges = w.HasUnpublishedChanges, isEnabled = w.IsEnabled,
            isAdmin = User.IsAdmin(),
            teams = await _db.Teams.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct),
        }, Json.Options);
    }

    /// <summary>Applies the "who can edit" and "who can submit" settings; returns an error message or null.</summary>
    private async Task<string?> ApplyAccessAsync(Workflow w, SaveWorkflowRequest request)
    {
        if (request.Owners is not null)
        {
            var owners = request.Owners.Select(o => (o ?? "").Trim().ToLowerInvariant()).Where(o => o.Length > 0).Distinct().ToList();
            var bad = owners.FirstOrDefault(o => o.Length > 320 || !System.Text.RegularExpressions.Regex.IsMatch(o, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"));
            if (bad is not null) return $"“{(bad.Length > 60 ? bad[..60] : bad)}” is not an email address.";
            if (owners.Count > 20) return "A workflow can have at most 20 owners.";
            // Someone who is not an admin must not be able to lock everyone (including themselves) out.
            if (owners.Count == 0 && !User.IsAdmin()) return "Keep at least one owner, or ask an admin to take the workflow over.";
            if (!User.IsAdmin() && !owners.Contains(User.Email())) return "You cannot remove yourself as an owner. Ask another owner or an admin to do it.";
            var packed = owners.Count == 0 ? "" : ApprovalTask.Pack(owners);
            if (packed != w.Owners)
            {
                _audit.Record(User, "workflow.owners", w.Name, $"Owners changed from [{string.Join(", ", w.OwnerList)}] to [{string.Join(", ", owners)}].");
                w.Owners = packed;
            }
        }
        if (request.AudienceGroups is not null)
        {
            var known = (await _groups.AllAsync()).ToDictionary(g => g.ObjectId, g => g.Name);
            var chosen = request.AudienceGroups.Select(g => (g ?? "").Trim().ToLowerInvariant()).Where(g => g.Length > 0).Distinct().ToList();
            if (chosen.Any(g => !known.ContainsKey(g))) return "One of the chosen groups is no longer registered. Reload the page and choose again.";
            var packed = chosen.Count == 0 ? "" : ApprovalTask.Pack(chosen);
            if (packed != w.AudienceGroups)
            {
                string Names(IEnumerable<string> ids) { var n = ids.Select(i => known.GetValueOrDefault(i, i)).ToList(); return n.Count == 0 ? "everyone" : string.Join(", ", n); }
                _audit.Record(User, "workflow.audience", w.Name, $"Who can submit changed from {Names(w.AudienceList)} to {Names(chosen)}.");
                w.AudienceGroups = packed;
            }
        }
        return null;
    }

    private static string? Apply(Workflow w, SaveWorkflowRequest request, string by, out WorkflowDefinition? def)
    {
        def = null;
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) return "Give the workflow a name.";
        if (name.Length > 200) return "The name is too long (200 characters at most).";
        if (request.Definition.ValueKind != JsonValueKind.Object) return "The workflow definition is missing.";
        var raw = request.Definition.GetRawText();
        if (raw.Length > MaxDefinitionBytes) return "This workflow is too large to save.";
        try
        {
            def = Json.ParseDefinition(raw);
        }
        catch (JsonException)
        {
            return "The workflow definition could not be read.";
        }
        w.Name = name;
        w.Description = Clip(request.Description, 1000);
        w.Category = Clip(request.Category, 100);
        w.Color = Colors.Contains(request.Color) ? request.Color! : "indigo";
        // Re-serialise so stored JSON is canonical; this is what "unpublished changes" compares.
        w.DraftJson = Json.Serialize(def);
        w.UpdatedAt = DateTime.UtcNow;
        w.UpdatedBy = by;
        return null;
    }

    /// <summary>Workflows that reach into the help desk are built by administrators only.</summary>
    private string? HelpDeskRule(WorkflowDefinition def) => !User.IsAdmin() && def.UsesHelpDesk() ? HelpDeskMessage : null;

    public const string HelpDeskMessage = "Only an administrator can save a workflow that uses the help desk (Freshservice steps, a Freshservice lookup field, or starting from a ticket). Ask an administrator to add those parts.";

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Save(int id, [FromBody] SaveWorkflowRequest request, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: true, ct);
        if (w is null) return NotFound();
        var error = Apply(w, request, User.Email(), out var def) ?? HelpDeskRule(def!) ?? await ApplyAccessAsync(w, request);
        if (error is not null) return BadRequest(new { error });
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true, hasUnpublishedChanges = w.HasUnpublishedChanges, issues = IssueShape(DefinitionValidator.Validate(def!)) });
    }

    [HttpPost("{id:int}/publish")]
    public async Task<IActionResult> Publish(int id, [FromBody] SaveWorkflowRequest request, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: true, ct);
        if (w is null) return NotFound();
        var error = Apply(w, request, User.Email(), out var def) ?? HelpDeskRule(def!) ?? await ApplyAccessAsync(w, request);
        if (error is not null) return BadRequest(new { error });

        var issues = DefinitionValidator.Validate(def!);
        if (issues.Any(i => i.IsError))
        {
            // Keep the draft (so no work is lost) but do not publish something broken.
            await _db.SaveChangesAsync(ct);
            return Ok(new { ok = false, issues = IssueShape(issues) });
        }

        if (w.PublishedJson != w.DraftJson)
        {
            // Versions published before history was kept: save the outgoing one so it can still be restored.
            if (w.PublishedJson is not null && !await _db.WorkflowVersions.AnyAsync(v => v.WorkflowId == w.Id && v.Version == w.PublishedVersion, ct))
                _db.WorkflowVersions.Add(new WorkflowVersion { WorkflowId = w.Id, Version = w.PublishedVersion, Json = w.PublishedJson, PublishedAt = w.PublishedAt ?? w.UpdatedAt });
            w.PublishedJson = w.DraftJson;
            w.PublishedVersion++;
            w.PublishedAt = DateTime.UtcNow;
            _db.WorkflowVersions.Add(new WorkflowVersion { WorkflowId = w.Id, Version = w.PublishedVersion, Json = w.DraftJson, PublishedAt = w.PublishedAt.Value, PublishedBy = User.Email() });
            // Keep the 50 most recent versions.
            var old = await _db.WorkflowVersions.Where(v => v.WorkflowId == w.Id).OrderByDescending(v => v.Version).Skip(49).ToListAsync(ct);
            _db.WorkflowVersions.RemoveRange(old);
            _audit.Record(User, "workflow.published", w.Name, $"Published version {w.PublishedVersion}.");
        }
        w.TriggerType = def!.Trigger.Type;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true, publishedVersion = w.PublishedVersion, issues = IssueShape(issues) });
    }

    [HttpGet("{id:int}/versions")]
    public async Task<IActionResult> Versions(int id, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: false, ct);
        if (w is null) return NotFound();
        var rows = await _db.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == id).OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.PublishedAt, v.PublishedBy, v.Json }).ToListAsync(ct);
        return Ok(new
        {
            versions = rows.Select(v => new
            {
                version = v.Version, publishedBy = v.PublishedBy,
                publishedAt = Ui.Local(v.PublishedAt).ToString("MMM d, yyyy h:mm tt"),
                isLive = v.Version == w.PublishedVersion, sameAsDraft = v.Json == w.DraftJson,
            }),
        });
    }

    /// <summary>Puts an earlier published version back as the draft. Nothing changes for staff until it is published.</summary>
    [HttpPost("{id:int}/versions/{version:int}/restore")]
    public async Task<IActionResult> Restore(int id, int version, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: true, ct);
        if (w is null) return NotFound();
        var row = await _db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.WorkflowId == id && v.Version == version, ct);
        if (row is null) return NotFound(new { error = "That version is no longer kept." });
        w.DraftJson = row.Json;
        w.UpdatedAt = DateTime.UtcNow;
        w.UpdatedBy = User.Email();
        _audit.Record(User, "workflow.restored", w.Name, $"Version {version} was put back as the draft.");
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    [HttpPost("validate")]
    public IActionResult Validate([FromBody] JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "The workflow definition is missing." });
        var raw = definition.GetRawText();
        if (raw.Length > MaxDefinitionBytes) return BadRequest(new { error = "This workflow is too large." });
        return Ok(new { issues = IssueShape(DefinitionValidator.Validate(Json.ParseDefinition(raw))) });
    }

    [HttpPost("check-formula")]
    public IActionResult CheckFormula([FromBody] CheckFormulaRequest request)
    {
        var source = request.Source ?? "";
        if (string.IsNullOrWhiteSpace(source)) return Ok(new { ok = true, references = Array.Empty<string>() });
        try
        {
            var refs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request.Template)
                foreach (var expression in Template.GetExpressions(source).ToList()) refs.UnionWith(Formula.Parse(expression).References);
            else
                refs.UnionWith(Formula.Parse(source).References);
            return Ok(new { ok = true, references = refs });
        }
        catch (ExpressionException ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }
}

/// <summary>Receives ticket events from Freshservice's Workflow Automator ("Trigger Webhook" action).</summary>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/webhooks")]
public sealed class WebhookController : ControllerBase
{
    public const string SecretHeader = "X-SchoolFlow-Secret";

    private readonly AppDbContext _db;
    private readonly RunService _runs;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(AppDbContext db, RunService runs, IConfiguration config, ILogger<WebhookController> log)
    {
        _db = db;
        _runs = runs;
        _config = config;
        _log = log;
    }

    [HttpPost("freshservice")]
    [RequestSizeLimit(256_000)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("webhook")]
    public async Task<IActionResult> Freshservice([FromBody] JsonElement body, CancellationToken ct)
    {
        var secret = _config["Freshservice:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
            return StatusCode(503, new { error = "Webhooks are disabled until Freshservice:WebhookSecret (16+ characters) is configured." });

        var supplied = Request.Headers[SecretHeader].ToString();
        // Hash both sides so the comparison is constant-time regardless of length.
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(secret))))
            return Unauthorized(new { error = "Missing or wrong webhook secret." });

        if (body.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "Send a JSON object." });

        string outcome;
        var payload = Json.ToValues(body);
        try
        {
            outcome = await _runs.HandleTicketEventAsync(payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Webhook handling failed");
            outcome = "Error: " + ex.Message;
        }

        var ticket = payload.GetValueOrDefault("ticket") as Dictionary<string, object?>;
        var summary = $"event={Values.Describe(payload.GetValueOrDefault("event"))} ticket={Values.Describe(ticket?.GetValueOrDefault("id"))}";
        _db.ChangeTracker.Clear();
        _db.WebhookLogs.Add(new WebhookLog { At = DateTime.UtcNow, Summary = summary, Outcome = outcome.Length > 1000 ? outcome[..1000] : outcome });
        await _db.SaveChangesAsync(ct);

        return outcome.StartsWith("Error", StringComparison.Ordinal)
            ? StatusCode(500, new { error = "The event could not be processed." })
            : Ok(new { ok = true, result = outcome });
    }
}
