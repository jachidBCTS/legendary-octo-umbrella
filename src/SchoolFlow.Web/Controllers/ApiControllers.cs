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
}

public sealed class SubmitRequest
{
    public int WorkflowId { get; set; }
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

    public FormsController(AppDbContext db, RunService runs, IFreshserviceClient freshservice)
    {
        _db = db;
        _runs = runs;
        _freshservice = freshservice;
    }

    private Dictionary<string, object?> Ambient()
    {
        var ambient = Values.NewDict();
        ambient["submitter"] = RunService.SubmitterRecord(User.Email(), User.DisplayName());
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
        if (request.Definition is not null)
        {
            if (!User.IsBuilder()) return Forbid();
            form = request.Definition;
            form.Fields ??= new List<FieldDef>();
            form.Fields.RemoveAll(f => f is null);
        }
        else
        {
            var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == request.WorkflowId, ct);
            if (workflow is null || !workflow.IsLive || workflow.TriggerType != TriggerTypes.Form) return NotFound();
            form = Json.ParseDefinition(workflow.PublishedJson!).Form;
        }
        var result = FormEvaluator.Evaluate(form, Json.ToValues(request.Values), Ambient(), applyDefaults: request.Init,
            departments: await _runs.ActiveDepartmentsAsync(ct), locations: await _runs.ActiveLocationsAsync(ct));
        return new JsonResult(Shape(result), Json.Options);
    }

    [HttpPost("forms/submit")]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length == 0) return BadRequest(new { error = "Your account has no email address, so the request cannot be submitted." });
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == request.WorkflowId, ct);
        if (workflow is null || !workflow.IsLive || workflow.TriggerType != TriggerTypes.Form)
            return NotFound(new { error = "This form is no longer available." });

        var (form, runId) = await _runs.SubmitFormAsync(workflow, Json.ToValues(request.Values), email, User.DisplayName(), ct);
        return new JsonResult(new { ok = runId is not null, runId, state = Shape(form) }, Json.Options);
    }

    // ---- start from a previous request ---------------------------------------------------

    /// <summary>
    /// Loads the workflow if "start from a previous request" is switched on for its published form,
    /// and returns the query for the earlier requests this person is allowed to copy from.
    /// </summary>
    private async Task<(FormDefinition Form, IQueryable<WorkflowRun> Runs)?> CopySourceAsync(int workflowId, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null || !workflow.IsLive || workflow.TriggerType != TriggerTypes.Form) return null;
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

    public BuilderController(AppDbContext db) => _db = db;

    private static object IssueShape(IEnumerable<Issue> issues) =>
        issues.Select(i => new { severity = i.Severity, area = i.Area, target = i.Target, message = i.Message });

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var w = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        return new JsonResult(new
        {
            id = w.Id, name = w.Name, description = w.Description, category = w.Category, color = w.Color,
            definition = Json.ParseDefinition(w.DraftJson),
            publishedVersion = w.PublishedVersion, isPublished = w.PublishedJson is not null,
            hasUnpublishedChanges = w.HasUnpublishedChanges, isEnabled = w.IsEnabled,
        }, Json.Options);
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

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Save(int id, [FromBody] SaveWorkflowRequest request, CancellationToken ct)
    {
        var w = await _db.Workflows.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        var error = Apply(w, request, User.Email(), out var def);
        if (error is not null) return BadRequest(new { error });
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true, hasUnpublishedChanges = w.HasUnpublishedChanges, issues = IssueShape(DefinitionValidator.Validate(def!)) });
    }

    [HttpPost("{id:int}/publish")]
    public async Task<IActionResult> Publish(int id, [FromBody] SaveWorkflowRequest request, CancellationToken ct)
    {
        var w = await _db.Workflows.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        var error = Apply(w, request, User.Email(), out var def);
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
            w.PublishedJson = w.DraftJson;
            w.PublishedVersion++;
            w.PublishedAt = DateTime.UtcNow;
        }
        w.TriggerType = def!.Trigger.Type;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true, publishedVersion = w.PublishedVersion, issues = IssueShape(issues) });
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
