using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Requests;

public class DetailModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly RunService _runs;
    private readonly IFreshserviceClient _freshservice;
    private readonly IConfiguration _config;

    public DetailModel(AppDbContext db, RunService runs, IFreshserviceClient freshservice, IConfiguration config)
    {
        _db = db;
        _runs = runs;
        _freshservice = freshservice;
        _config = config;
    }

    public sealed record Answer(string Label, string Value, List<string>? Columns = null, List<List<string>>? Rows = null);

    public WorkflowRun Run { get; private set; } = null!;
    public List<RunEventRow> Events { get; private set; } = new();
    public List<ApprovalTask> MyTasks { get; private set; } = new();
    public List<string> PendingAssignees { get; private set; } = new();
    public List<Answer> Fields { get; private set; } = new();
    public bool CanManage => User.IsBuilder();
    public string? TicketUrl { get; private set; }

    /// <summary>Loads the run if the current user may see it: the submitter, anyone asked to approve it, builders and admins.</summary>
    private async Task<bool> LoadAsync(int id)
    {
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return false;
        var email = User.Email();
        var tasks = await _db.ApprovalTasks.AsNoTracking().Where(t => t.RunId == id).ToListAsync();
        var allowed = User.IsBuilder() || (email.Length > 0 && (run.SubmitterEmail == email || tasks.Any(t => t.IsAssignedTo(email))));
        if (!allowed) return false;

        Run = run;
        var pending = tasks.Where(t => t.Status == ApprovalStatus.Pending).ToList();
        MyTasks = pending.Where(t => t.IsAssignedTo(email) || User.IsAdmin()).ToList();
        PendingAssignees = pending.SelectMany(t => t.AssigneeList).Distinct().ToList();
        Events = await _db.RunEvents.AsNoTracking().Where(e => e.RunId == id).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync();

        if (run.TicketId is long ticketId && _freshservice.IsLive && !string.IsNullOrWhiteSpace(_config["Freshservice:Domain"]))
            TicketUrl = new Uri(FreshserviceClient.BaseAddressFor(_config["Freshservice:Domain"]!), $"a/tickets/{ticketId}").ToString();

        var def = Json.ParseDefinition(run.DefinitionJson);
        var context = Json.ToValues(run.ContextJson);
        foreach (var f in def.Form.Fields.Where(f => f.Type != FieldTypes.Section))
        {
            var value = context.GetValueOrDefault(f.Key);
            if (Values.IsBlank(value) && f.Type != FieldTypes.Checkbox) continue; // hidden or unanswered
            if (f.Type == FieldTypes.Table && value is List<object?> rows)
            {
                var columns = f.Columns ?? new List<ColumnDef>();
                Fields.Add(new Answer(f.Label, "", columns.Select(c => c.Label).ToList(),
                    rows.OfType<Dictionary<string, object?>>()
                        .Select(r => columns.Select(c => Ui.Display(r.GetValueOrDefault(c.Key), c.Type, c.Format, c.Decimals)).ToList()).ToList()));
            }
            else
            {
                Fields.Add(new Answer(f.Label, Ui.Display(value, f.Type, f.Format, f.Decimals)));
            }
        }
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostDecideAsync(int id, int taskId, bool approved, string? comment, CancellationToken ct)
    {
        // The task must belong to this request; RunService checks the assignee.
        var belongs = await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.RunId == id, ct);
        if (!belongs) return NotFound();
        var result = await _runs.DecideAsync(taskId, User.Email(), User.DisplayName(), User.IsAdmin(), approved, comment, ct);
        Flash(result, approved ? "Approved. The request has moved on." : "Rejected. The requester will see your decision.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRetryAsync(int id, CancellationToken ct)
    {
        if (!User.IsBuilder()) return Forbid();
        Flash(await _runs.RetryAsync(id, ct), "Retried.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(int id, CancellationToken ct)
    {
        if (!User.IsBuilder()) return Forbid();
        Flash(await _runs.CancelAsync(id, User.DisplayName(), ct), "Request cancelled.");
        return RedirectToPage(new { id });
    }

    private void Flash(Services.ActionResult result, string success)
    {
        TempData["Flash"] = result.Ok ? success : result.Error;
        TempData["FlashKind"] = result.Ok ? "ok" : "bad";
    }
}
