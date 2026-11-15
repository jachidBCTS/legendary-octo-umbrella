using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Pages;

public class StartModel : PageModel
{
    private readonly AppDbContext _db;
    public StartModel(AppDbContext db) => _db = db;

    public Workflow Workflow { get; private set; } = null!;
    public string FormJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        if (workflow is null || !workflow.IsLive || workflow.TriggerType != TriggerTypes.Form
            || !SchoolFlow.Web.Services.AccessService.CanSubmit(User, workflow)) return NotFound();
        Workflow = workflow;
        // Only the form part goes to the browser; the flow (approver rules, ticket templates) stays on the server.
        // "<" is escaped so field text can never close the surrounding <script> element.
        FormJson = Json.Serialize(Json.ParseDefinition(workflow.PublishedJson!).Form).Replace("<", "\\u003c");
        return Page();
    }
}
