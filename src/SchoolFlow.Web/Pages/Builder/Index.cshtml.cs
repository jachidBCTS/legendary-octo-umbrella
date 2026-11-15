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

    public async Task OnGetAsync()
    {
        // Builders see the workflows they own; admins see them all.
        Workflows = (await _db.Workflows.AsNoTracking().OrderBy(w => w.Name).ToListAsync())
            .Where(w => AccessService.CanManage(User, w)).ToList();
        RunCounts = await _db.Runs.GroupBy(r => r.WorkflowId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? trigger, string? allowCopy)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200)
        {
            TempData["Flash"] = "Give the workflow a name (up to 200 characters).";
            TempData["FlashKind"] = "bad";
            return RedirectToPage();
        }
        var isTicket = trigger == TriggerTypes.Ticket;
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

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var w = await ManagedAsync(id);
        if (w is null) return NotFound();
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
        _audit.Record(User, "workflow.deleted", w.Name, $"Had {await _db.Runs.CountAsync(r => r.WorkflowId == id)} request(s), which are kept.");
        _db.Workflows.Remove(w);
        // Unfinished drafts of a form that no longer exists cannot be completed.
        _db.FormDrafts.RemoveRange(_db.FormDrafts.Where(d => d.WorkflowId == id));
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Deleted “{w.Name}”.";
        return RedirectToPage();
    }
}
