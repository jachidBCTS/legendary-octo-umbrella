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

/// <summary>
/// The workflow builder's calls: load and save a draft, publish, version history and restore,
/// letterhead image, and checking a definition or a single formula.
/// </summary>
[ApiController]
[Authorize] // owners need not be builders; every workflow call checks AccessService.CanManage
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
            canEditForm = AccessService.CanEditForm(User),
            teams = await _db.Teams.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct),
            categories = await Categories.NamesAsync(_db, ct),
            letterhead = await LetterheadInfoAsync(w.Id, ct),
            // The help desk's statuses, custom ones included, for the ticket-status choices (queue #21).
            ticketStatuses = (await HttpContext.RequestServices.GetRequiredService<TicketStatusNames>().AllAsync(ct))
                .Select(s => new { value = s.Value, label = s.Label }),
        }, Json.Options);
    }

    // ---- letterhead ------------------------------------------------------------------

    public const int MaxLetterheadBytes = 2 * 1024 * 1024;

    private async Task<object?> LetterheadInfoAsync(int workflowId, CancellationToken ct) =>
        await _db.FormImages.AsNoTracking().Where(i => i.WorkflowId == workflowId)
            .Select(i => new { i.FileName, i.UpdatedAt }).FirstOrDefaultAsync(ct) is { } image
            ? new { url = Letterhead.UrlFor(workflowId, image.UpdatedAt), fileName = image.FileName } : null;

    /// <summary>Puts a letterhead image on the form (shown at the top of the form and of its printout). Admins only, like the rest of the form.</summary>
    [HttpPost("{id:int}/letterhead")]
    [RequestSizeLimit(MaxLetterheadBytes + 64 * 1024)]
    public async Task<IActionResult> UploadLetterhead(int id, IFormFile? file, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: false, ct);
        if (w is null) return NotFound();
        if (!AccessService.CanEditForm(User)) return StatusCode(403, new { error = "Only an administrator can change the form's letterhead." });
        if (file is null || file.Length == 0) return BadRequest(new { error = "Choose an image file." });
        if (file.Length > MaxLetterheadBytes) return BadRequest(new { error = "The image is too large (2 MB at most). Save it smaller, e.g. as a JPEG about 1600 pixels wide." });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var type = Letterhead.ContentTypeOf(bytes);
        if (type is null) return BadRequest(new { error = "That is not a PNG, JPEG, GIF or WebP image." });
        var name = Path.GetFileName(file.FileName ?? "letterhead");
        if (name.Length > 200) name = name[^200..];
        var row = await _db.FormImages.FirstOrDefaultAsync(i => i.WorkflowId == id, ct);
        if (row is null) _db.FormImages.Add(row = new FormImage { WorkflowId = id });
        row.ContentType = type;
        row.Data = bytes;
        row.FileName = name;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = User.Email();
        _audit.Record(User, "workflow.letterhead", w.Name, $"Letterhead set to “{name}” ({bytes.Length / 1024} KB).");
        await _db.SaveChangesAsync(ct);
        return Ok(new { letterhead = new { url = Letterhead.UrlFor(id, row.UpdatedAt), fileName = name } });
    }

    [HttpDelete("{id:int}/letterhead")]
    public async Task<IActionResult> RemoveLetterhead(int id, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: false, ct);
        if (w is null) return NotFound();
        if (!AccessService.CanEditForm(User)) return StatusCode(403, new { error = "Only an administrator can change the form's letterhead." });
        var row = await _db.FormImages.FirstOrDefaultAsync(i => i.WorkflowId == id, ct);
        if (row is not null)
        {
            _db.FormImages.Remove(row);
            _audit.Record(User, "workflow.letterhead", w.Name, "Letterhead removed.");
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { letterhead = (object?)null });
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

    /// <summary>What an owner who is not an admin may not change: everything about the form and who looks after it.</summary>
    private sealed record FormSnapshot(string Name, string Description, string Category, string Color, string Form, string Trigger, string Owners, string Audience)
    {
        public static FormSnapshot Of(Workflow w)
        {
            var def = Json.ParseDefinition(w.DraftJson);
            return new(w.Name, w.Description, w.Category, w.Color, Json.Serialize(def.Form), Json.Serialize(def.Trigger), w.Owners, w.AudienceGroups);
        }
    }

    public const string FormMessage = "Only an administrator can change the form (its questions, name, description, colour or how it starts) or who looks after it. You can change the Flow.";

    /// <summary>Owners who are not admins may change the flow only.</summary>
    private string? FormRule(FormSnapshot before, Workflow w, WorkflowDefinition def, SaveWorkflowRequest request)
    {
        if (AccessService.CanEditForm(User)) return null;
        static IEnumerable<string> Norm(IEnumerable<string?> list) => list.Select(o => (o ?? "").Trim().ToLowerInvariant()).Where(o => o.Length > 0).Distinct().OrderBy(o => o);
        var ownersChanged = request.Owners is not null && !Norm(request.Owners).SequenceEqual(Norm(before.Owners.Split(';')));
        var audienceChanged = request.AudienceGroups is not null && !Norm(request.AudienceGroups).SequenceEqual(Norm(before.Audience.Split(';')));
        var changed = w.Name != before.Name || w.Description != before.Description || w.Category != before.Category || w.Color != before.Color
            || Json.Serialize(def.Form) != before.Form || Json.Serialize(def.Trigger) != before.Trigger || ownersChanged || audienceChanged;
        return changed ? FormMessage : null;
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
        var locked = FormSnapshot.Of(w);
        var error = Apply(w, request, User.Email(), out var def) ?? HelpDeskRule(def!) ?? FormRule(locked, w, def!, request) ?? await ApplyAccessAsync(w, request);
        if (error is not null) return BadRequest(new { error });
        await _db.SaveChangesAsync(ct);
        var saved = DefinitionValidator.Validate(def!);
        saved.AddRange(await TeamCheck.IssuesAsync(_db, def!, ct));
        return Ok(new { ok = true, hasUnpublishedChanges = w.HasUnpublishedChanges, issues = IssueShape(saved) });
    }

    [HttpPost("{id:int}/publish")]
    public async Task<IActionResult> Publish(int id, [FromBody] SaveWorkflowRequest request, CancellationToken ct)
    {
        var w = await ManagedAsync(id, tracked: true, ct);
        if (w is null) return NotFound();
        var locked = FormSnapshot.Of(w);
        var error = Apply(w, request, User.Email(), out var def) ?? HelpDeskRule(def!) ?? FormRule(locked, w, def!, request) ?? await ApplyAccessAsync(w, request);
        if (error is not null) return BadRequest(new { error });

        var issues = DefinitionValidator.Validate(def!);
        issues.AddRange(await TeamCheck.IssuesAsync(_db, def!, ct));
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
        var restored = Json.ParseDefinition(row.Json);
        if (!AccessService.CanEditForm(User))
        {
            // An owner rolls back the flow only; the form stays as the admin left it.
            var current = Json.ParseDefinition(w.DraftJson);
            current.Flow = restored.Flow;
            restored = current;
        }
        if (HelpDeskRule(restored) is string helpDesk) return BadRequest(new { error = helpDesk });
        w.DraftJson = Json.Serialize(restored);
        w.UpdatedAt = DateTime.UtcNow;
        w.UpdatedBy = User.Email();
        _audit.Record(User, "workflow.restored", w.Name, AccessService.CanEditForm(User)
            ? $"Version {version} was put back as the draft."
            : $"The flow of version {version} was put back into the draft.");
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
