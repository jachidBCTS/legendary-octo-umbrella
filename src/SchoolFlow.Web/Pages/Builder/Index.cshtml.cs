using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Builder;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public IndexModel(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>The workflow, if this person may manage it (its owners, and admins).</summary>
    private async Task<Workflow?> ManagedAsync(int id)
    {
        var workflow = await _db.Workflows.FirstOrDefaultAsync(w => w.Id == id);
        return workflow is not null && AccessService.CanManage(User, workflow) ? workflow : null;
    }

    public List<Workflow> Workflows { get; private set; } = new();
    public Dictionary<int, int> RunCounts { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync([FromServices] AccessService access)
    {
        // Somebody who neither builds nor looks after any workflow has nothing here.
        if (!User.IsBuilder() && !await access.OwnsAnyWorkflowAsync(User)) return Forbid();
        // Builders see the workflows they own; admins see them all.
        Workflows = (await _db.Workflows.AsNoTracking().OrderBy(w => w.Name).ToListAsync())
            .Where(w => AccessService.CanManage(User, w)).ToList();
        RunCounts = await _db.Runs.GroupBy(r => r.WorkflowId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        return Page();
    }

    private IActionResult AdminsOnly()
    {
        TempData["Flash"] = "Only an administrator can create, copy, import, delete, or switch forms on and off.";
        TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? trigger, string? allowCopy)
    {
        if (!AccessService.CanEditForm(User)) return AdminsOnly();
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200)
        {
            TempData["Flash"] = "Give the workflow a name (up to 200 characters).";
            TempData["FlashKind"] = "bad";
            return RedirectToPage();
        }
        var isTicket = trigger == TriggerTypes.Ticket;
        if (isTicket && !User.IsAdmin())
        {
            TempData["Flash"] = SchoolFlow.Web.Controllers.BuilderController.HelpDeskMessage;
            TempData["FlashKind"] = "bad";
            return RedirectToPage();
        }
        var def = new WorkflowDefinition
        {
            Trigger = { Type = isTicket ? TriggerTypes.Ticket : TriggerTypes.Form },
            Flow =
            {
                Nodes =
                {
                    new NodeDef { Id = "start", Type = NodeTypes.Start, Label = isTicket ? "Ticket event received" : "Form submitted", X = 150, Y = 40 },
                    new NodeDef { Id = "end", Type = NodeTypes.End, Label = "Done", X = 150, Y = 320 },
                },
            },
        };
        if (!isTicket && CopyModes.IsOn(allowCopy)) def.Form.AllowCopy = allowCopy;
        var workflow = new Workflow
        {
            Name = name, DraftJson = Json.Serialize(def), TriggerType = def.Trigger.Type,
            Owners = ApprovalTask.Pack(new[] { User.Email() }), // whoever creates a workflow looks after it
            CreatedBy = User.Email(), UpdatedBy = User.Email(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Workflows.Add(workflow);
        _audit.Record(User, "workflow.created", workflow.Name);
        await _db.SaveChangesAsync();
        return Redirect($"/Builder/Edit/{workflow.Id}");
    }

    public async Task<IActionResult> OnPostDuplicateAsync(int id)
    {
        var source = await ManagedAsync(id);
        if (source is null) return NotFound();
        if (!AccessService.CanEditForm(User)) return AdminsOnly();
        if (!User.IsAdmin() && Json.ParseDefinition(source.DraftJson).UsesHelpDesk())
        {
            TempData["Flash"] = SchoolFlow.Web.Controllers.BuilderController.HelpDeskMessage;
            TempData["FlashKind"] = "bad";
            return RedirectToPage();
        }
        var name = "Copy of " + source.Name;
        var copy = new Workflow
        {
            Name = name.Length > 200 ? name[..200] : name,
            Description = source.Description, Category = source.Category, Color = source.Color,
            DraftJson = source.DraftJson, TriggerType = source.TriggerType,
            Owners = ApprovalTask.Pack(new[] { User.Email() }), AudienceGroups = source.AudienceGroups,
            CreatedBy = User.Email(), UpdatedBy = User.Email(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Workflows.Add(copy);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Created “{copy.Name}” as a draft.";
        return RedirectToPage();
    }

    /// <summary>Downloads a workflow (its current draft) as a file that can be imported here or on another Bergen Forms site.</summary>
    public async Task<IActionResult> OnGetExportAsync(int id)
    {
        var w = await ManagedAsync(id);
        if (w is null) return NotFound();
        var file = Json.Serialize(new Dictionary<string, object?>
        {
            ["schoolflow"] = 1, ["name"] = w.Name, ["description"] = w.Description, ["category"] = w.Category, ["color"] = w.Color,
            ["definition"] = Json.ParseDefinition(w.DraftJson),
        });
        var safe = new string(w.Name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        return File(System.Text.Encoding.UTF8.GetBytes(file), "application/json", $"{(safe.Length == 0 ? "workflow" : safe.Length > 60 ? safe[..60] : safe)}.schoolflow.json");
    }

    /// <summary>Creates a new draft workflow from an exported file. It is never published automatically.</summary>
    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        IActionResult Bad(string message) { TempData["Flash"] = message; TempData["FlashKind"] = "bad"; return RedirectToPage(); }
        if (!AccessService.CanEditForm(User)) return AdminsOnly();
        if (file is null || file.Length == 0) return Bad("Choose a workflow file to import.");
        if (file.Length > 600_000) return Bad("That file is too large to be a workflow.");
        WorkflowDefinition def;
        string name, description, category, color;
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(file.OpenReadStream());
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("schoolflow", out _)
                || !root.TryGetProperty("definition", out var definition) || definition.ValueKind != System.Text.Json.JsonValueKind.Object)
                return Bad("That is not a Bergen Forms workflow file.");
            string Text(string property) => root.TryGetProperty(property, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()!.Trim() : "";
            (name, description, category, color) = (Text("name"), Text("description"), Text("category"), Text("color"));
            def = Json.ParseDefinition(definition.GetRawText());
        }
        catch (System.Text.Json.JsonException)
        {
            return Bad("That file could not be read as a workflow.");
        }
        if (!User.IsAdmin() && def.UsesHelpDesk()) return Bad(SchoolFlow.Web.Controllers.BuilderController.HelpDeskMessage);
        if (name.Length == 0) name = "Imported workflow";
        string Clip(string s, int max) => s.Length > max ? s[..max] : s;
        var workflow = new Workflow
        {
            Name = Clip(name, 200), Description = Clip(description, 1000), Category = Clip(category, 100),
            Color = System.Text.RegularExpressions.Regex.IsMatch(color, "^[a-z]{1,20}$") ? color : "indigo",
            DraftJson = Json.Serialize(def), TriggerType = def.Trigger.Type == TriggerTypes.Ticket ? TriggerTypes.Ticket : TriggerTypes.Form,
            Owners = ApprovalTask.Pack(new[] { User.Email() }),
            CreatedBy = User.Email(), UpdatedBy = User.Email(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Workflows.Add(workflow);
        _audit.Record(User, "workflow.imported", workflow.Name, $"From the file “{Clip(Path.GetFileName(file.FileName), 100)}”.");
        await _db.SaveChangesAsync();
        var problems = SchoolFlow.Core.Workflows.DefinitionValidator.Validate(def).Count(i => i.IsError);
        TempData["Flash"] = $"Imported “{workflow.Name}” as a draft." + (problems > 0
            ? $" The checker found {problems} thing{(problems == 1 ? "" : "s")} to fix before it can be published — often an approval chain, department or group that does not exist on this site."
            : " Check it over, then publish it when you are ready.");
        return Redirect($"/Builder/Edit/{workflow.Id}");
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var w = await ManagedAsync(id);
        if (w is null) return NotFound();
        // Whether a form is available at all is the administrators' decision, like the form itself.
        if (!AccessService.CanEditForm(User)) return AdminsOnly();
        w.IsEnabled = !w.IsEnabled;
        _audit.Record(User, w.IsEnabled ? "workflow.switched-on" : "workflow.switched-off", w.Name);
        await _db.SaveChangesAsync();
        TempData["Flash"] = w.IsEnabled ? $"“{w.Name}” is live again." : $"“{w.Name}” is switched off. Requests already in progress will still finish.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var w = await ManagedAsync(id);
        if (w is null) return NotFound();
        if (!AccessService.CanEditForm(User)) return AdminsOnly();
        _audit.Record(User, "workflow.deleted", w.Name, $"Had {await _db.Runs.CountAsync(r => r.WorkflowId == id)} request(s), which are kept.");
        _db.Workflows.Remove(w);
        // Unfinished drafts of a form that no longer exists cannot be completed.
        _db.FormDrafts.RemoveRange(_db.FormDrafts.Where(d => d.WorkflowId == id));
        _db.WorkflowVersions.RemoveRange(_db.WorkflowVersions.Where(v => v.WorkflowId == id));
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Deleted “{w.Name}”.";
        return RedirectToPage();
    }
}
