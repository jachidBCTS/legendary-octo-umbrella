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
    public IndexModel(AppDbContext db) => _db = db;

    public List<Workflow> Workflows { get; private set; } = new();
    public Dictionary<int, int> RunCounts { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Workflows = await _db.Workflows.AsNoTracking().OrderBy(w => w.Name).ToListAsync();
        RunCounts = await _db.Runs.GroupBy(r => r.WorkflowId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? trigger)
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
                    new NodeDef { Id = "start", Type = NodeTypes.Start, Label = isTicket ? "Ticket event received" : "Form submitted", X = 320, Y = 40 },
                    new NodeDef { Id = "end", Type = NodeTypes.End, Label = "Done", X = 320, Y = 320 },
                },
            },
        };
        var workflow = new Workflow
        {
            Name = name, DraftJson = Json.Serialize(def), TriggerType = def.Trigger.Type,
            CreatedBy = User.Email(), UpdatedBy = User.Email(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();
        return Redirect($"/Builder/Edit/{workflow.Id}");
    }

    public async Task<IActionResult> OnPostDuplicateAsync(int id)
    {
        var source = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        if (source is null) return NotFound();
        var name = "Copy of " + source.Name;
        var copy = new Workflow
        {
            Name = name.Length > 200 ? name[..200] : name,
            Description = source.Description, Category = source.Category, Color = source.Color,
            DraftJson = source.DraftJson, TriggerType = source.TriggerType,
            CreatedBy = User.Email(), UpdatedBy = User.Email(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Workflows.Add(copy);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Created “{copy.Name}” as a draft.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var w = await _db.Workflows.FirstOrDefaultAsync(x => x.Id == id);
        if (w is null) return NotFound();
        w.IsEnabled = !w.IsEnabled;
        await _db.SaveChangesAsync();
        TempData["Flash"] = w.IsEnabled ? $"“{w.Name}” is live again." : $"“{w.Name}” is switched off. Requests already in progress will still finish.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var w = await _db.Workflows.FirstOrDefaultAsync(x => x.Id == id);
        if (w is null) return NotFound();
        _db.Workflows.Remove(w);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Deleted “{w.Name}”.";
        return RedirectToPage();
    }
}
