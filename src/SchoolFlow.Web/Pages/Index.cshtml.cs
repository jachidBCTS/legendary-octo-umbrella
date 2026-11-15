using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<IGrouping<string, Workflow>> Groups { get; private set; } = new();
    public List<WorkflowRun> Recent { get; private set; } = new();
    public List<ApprovalTask> Pending { get; private set; } = new();
    public string Greeting => DateTime.Now.Hour switch { < 12 => "Good morning", < 17 => "Good afternoon", _ => "Good evening" };

    public async Task OnGetAsync()
    {
        var email = User.Email();
        var live = await _db.Workflows.AsNoTracking()
            .Where(w => w.PublishedJson != null && w.IsEnabled && w.TriggerType == TriggerTypes.Form)
            .OrderBy(w => w.Name).ToListAsync();
        Groups = live.GroupBy(w => w.Category).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key).ToList();
        Recent = await _db.Runs.AsNoTracking().Where(r => r.SubmitterEmail == email && email != "")
            .OrderByDescending(r => r.UpdatedAt).Take(6).ToListAsync();
        var needle = ";" + email + ";";
        Pending = email.Length == 0 ? new() : await _db.ApprovalTasks.AsNoTracking()
            .Where(t => t.Status == ApprovalStatus.Pending && t.Assignees.Contains(needle)).OrderBy(t => t.CreatedAt).ToListAsync();
    }
}
