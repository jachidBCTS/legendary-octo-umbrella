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
    private readonly AccessService _access;
    private readonly AuditService _audit;

    public DetailModel(AppDbContext db, RunService runs, IFreshserviceClient freshservice, IConfiguration config, AccessService access, AuditService audit)
    {
        _access = access;
        _audit = audit;
        _db = db;
        _runs = runs;
        _freshservice = freshservice;
        _config = config;
    }

    public sealed record ChainStepView(string Name, string Approvers, string Status, string Rule);
    /// <summary>Steps of the approval chain this request is going (or went) through, if any.</summary>
    public List<ChainStepView> Chain { get; private set; } = new();

    public sealed record FileLink(string Name, string Size, string Url);
    public sealed record Answer(string Label, string Value, List<string>? Columns = null, List<List<string>>? Rows = null, List<FileLink>? Files = null);

    public WorkflowRun Run { get; private set; } = null!;
    public List<RunEventRow> Events { get; private set; } = new();
    public List<ApprovalTask> MyTasks { get; private set; } = new();
    public List<string> PendingAssignees { get; private set; } = new();
    public List<Answer> Fields { get; private set; } = new();
    public List<RunComment> Comments { get; private set; } = new();
    /// <summary>Approval steps that have been decided, oldest first (for the printable record).</summary>
    public List<ApprovalTask> Decisions { get; private set; } = new();
    /// <summary>The requester may take the request back while it is still waiting for approval or for their own changes.</summary>
    public bool CanWithdraw { get; private set; }
    /// <summary>An approver sent it back and this is the requester: they can edit and resubmit.</summary>
    public bool CanRevise { get; private set; }
    /// <summary>What the approver asked to be changed, when the request has been sent back.</summary>
    public ApprovalTask? ReturnedBy { get; private set; }
    /// <summary>Retry and cancel are for the workflow's owners and admins.</summary>
    public bool CanManage { get; private set; }
    public string? TicketUrl { get; private set; }
    /// <summary>Link to start a new request prefilled from this one, when the form allows it for this person.</summary>
    public string? CopyUrl { get; private set; }

    /// <summary>Loads the run if the current user may see it: the submitter, anyone asked to approve it, builders and admins.</summary>
    protected async Task<bool> LoadAsync(int id)
    {
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return false;
        var email = User.Email();
        var tasks = await _db.ApprovalTasks.AsNoTracking().Where(t => t.RunId == id).ToListAsync();
        if (!await _access.CanViewRunAsync(User, run)) return false;
        CanManage = await _access.CanManageRunAsync(User, run);

        Run = run;
        var pending = tasks.Where(t => t.Status == ApprovalStatus.Pending).ToList();
        // Someone who has already approved an "all must approve" step has nothing left to do.
        MyTasks = pending.Where(t => (t.CanBeDecidedBy(email) && !t.IsDoneFor(email)) || (User.IsAdmin() && !t.CanBeDecidedBy(email))).ToList();
        PendingAssignees = pending.SelectMany(t => t.AssigneeList.Where(a => !t.HasApproved(a)).Concat(t.StandInPairs.Select(p => p.StandIn)).Concat(t.EscalatedList)).Distinct().ToList();
        Comments = await _db.RunComments.AsNoTracking().Where(c => c.RunId == id).OrderBy(c => c.Id).ToListAsync();
        Decisions = tasks.Where(t => t.Status is ApprovalStatus.Approved or ApprovalStatus.Rejected or ApprovalStatus.Returned).OrderBy(t => t.Id).ToList();
        var isSubmitter = email.Length > 0 && run.SubmitterEmail == email;
        CanWithdraw = isSubmitter && run.Status is RunStatus.WaitingApproval or RunStatus.ChangesRequested && isSubmitter;
        CanRevise = isSubmitter && run.Status == RunStatus.ChangesRequested;
        if (run.Status == RunStatus.ChangesRequested)
            ReturnedBy = tasks.Where(t => t.Status == ApprovalStatus.Returned).OrderByDescending(t => t.Id).FirstOrDefault();
        Events = await _db.RunEvents.AsNoTracking().Where(e => e.RunId == id).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync();

        if (run.TicketId is long ticketId && _freshservice.IsLive && !string.IsNullOrWhiteSpace(_config["Freshservice:Domain"]))
            TicketUrl = new Uri(FreshserviceClient.BaseAddressFor(_config["Freshservice:Domain"]!), $"a/tickets/{ticketId}").ToString();

        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == run.WorkflowId);
        if (workflow is { IsLive: true, TriggerType: TriggerTypes.Form } && run.Status != RunStatus.Cancelled && run.SubmitterEmail.Length > 0)
        {
            // Same rule as the picker on the form: the current published setting decides.
            var mode = Json.ParseDefinition(workflow.PublishedJson!).Form.AllowCopy;
            if (mode == CopyModes.Everyone || (mode == CopyModes.Own && run.SubmitterEmail == email))
                CopyUrl = $"/Start/{workflow.Id}?copy={run.Id}";
        }

        var def = Json.ParseDefinition(run.DefinitionJson);
        var context = Json.ToValues(run.ContextJson);
        if (context.GetValueOrDefault("chain") is Dictionary<string, object?> chain && chain.GetValueOrDefault("steps") is List<object?> chainSteps)
        {
            foreach (var step in chainSteps.OfType<Dictionary<string, object?>>())
            {
                var status = Values.ToText(step.GetValueOrDefault("status"));
                // A chain that ended early (rejected, cancelled) leaves later steps unreached.
                if (status is "pending" or "waiting" && RunStatus.IsFinished(run.Status)) status = "skipped";
                var by = Values.ToText(step.GetValueOrDefault("by"));
                Chain.Add(new ChainStepView(Values.ToText(step.GetValueOrDefault("name")),
                    status is "approved" or "rejected" && by.Length > 0 ? by : Values.ToText(step.GetValueOrDefault("approvers")),
                    status, Values.ToText(step.GetValueOrDefault("rule"))));
            }
        }
        foreach (var f in def.Form.Fields.Where(f => !FieldTypes.IsLayout(f.Type)))
        {
            var value = context.GetValueOrDefault(f.Key);
            if (Values.IsBlank(value)) continue; // hidden or unanswered (an unticked box is "No", not blank)
            if (f.Type == FieldTypes.Attachments && value is List<object?> uploaded)
            {
                Fields.Add(new Answer(f.Label, "", Files: uploaded.OfType<Dictionary<string, object?>>().Select(file => new FileLink(
                    Values.ToText(file.GetValueOrDefault("name")),
                    AttachmentService.DisplaySize(Values.TryToNumber(file.GetValueOrDefault("size"), out var bytes) ? (long)bytes : 0),
                    "/files/" + Uri.EscapeDataString(Values.ToText(file.GetValueOrDefault("id"))))).ToList()));
            }
            else if (f.Type == FieldTypes.Table && value is List<object?> rows)
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

    public async Task<IActionResult> OnPostCommentAsync(int id, string? body, CancellationToken ct)
    {
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null || !await _access.CanViewRunAsync(User, run, ct)) return NotFound();
        Flash(await _runs.CommentAsync(run, User.Email(), User.DisplayName(), body, ct), "Message added.");
        return Redirect($"/Requests/Detail/{id}#discussion");
    }

    public async Task<IActionResult> OnPostWithdrawAsync(int id, CancellationToken ct)
    {
        Flash(await _runs.WithdrawAsync(id, User.Email(), User.DisplayName(), ct), "Request withdrawn.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDecideAsync(int id, int taskId, string? decision, string? comment, CancellationToken ct)
    {
        // The task must belong to this request; RunService checks the assignee.
        var belongs = await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.RunId == id, ct);
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (!belongs || run is null || !await _access.CanViewRunAsync(User, run, ct)) return NotFound();
        var action = decision switch { "approve" => ApprovalAction.Approve, "return" => ApprovalAction.Return, "reject" => ApprovalAction.Reject, _ => (ApprovalAction?)null };
        if (action is null) return BadRequest();
        var result = await _runs.DecideAsync(taskId, User.Email(), User.DisplayName(), User.IsAdmin(), action.Value, comment, ct);
        Flash(result, action switch
        {
            ApprovalAction.Approve => "Approved. The request has moved on.",
            ApprovalAction.Return => "Sent back. The requester has been told what to change.",
            _ => "Rejected. The requester will see your decision.",
        });
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRetryAsync(int id, CancellationToken ct)
    {
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null || !await _access.CanManageRunAsync(User, run, ct)) return NotFound();
        _audit.Record(User, "request.retried", $"{run.WorkflowName} #{run.Id}", run.Error ?? "");
        await _db.SaveChangesAsync(ct);
        Flash(await _runs.RetryAsync(id, ct), "Retried.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(int id, CancellationToken ct)
    {
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null || !await _access.CanManageRunAsync(User, run, ct)) return NotFound();
        var result = await _runs.CancelAsync(id, User.DisplayName(), ct);
        if (result.Ok)
        {
            _audit.Record(User, "request.cancelled", $"{run.WorkflowName} #{run.Id}", $"Submitted by {run.SubmitterName}.");
            await _db.SaveChangesAsync(ct);
        }
        Flash(result, "Request cancelled.");
        return RedirectToPage(new { id });
    }

    private void Flash(Services.ActionResult result, string success)
    {
        TempData["Flash"] = result.Ok ? success : result.Error;
        TempData["FlashKind"] = result.Ok ? "ok" : "bad";
    }
}
