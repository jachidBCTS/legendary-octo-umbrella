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
    private readonly SchoolCalendar _calendar;
    private readonly IStudentRoster _roster;

    public DetailModel(AppDbContext db, RunService runs, IFreshserviceClient freshservice, IConfiguration config, AccessService access, AuditService audit,
        SchoolCalendar calendar, IStudentRoster roster)
    {
        _calendar = calendar;
        _roster = roster;
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
    /// <summary>Every stop on the request's journey, from submission to the end (the train-stop track).</summary>
    public List<JourneyStop> Stops { get; private set; } = new();
    /// <summary>The student roster could not be reached, so students show by number only.</summary>
    public bool RosterProblem { get; private set; }

    public sealed record FileLink(string Name, string Size, string Url);
    public sealed record Answer(string Label, string Value, List<string>? Columns = null, List<List<string>>? Rows = null, List<FileLink>? Files = null, bool Heading = false,
        string? PreviousValue = null, DateTime? SignedAt = null);
    /// <summary>Tasks (fill-in steps) waiting for this person on this request.</summary>
    public List<ApprovalTask> MyWork { get; private set; } = new();
    public string TaskFormJson { get; private set; } = "{}";
    /// <summary>The task's answers so far (when it was sent back to be done again), to prefill it.</summary>
    public string TaskValuesJson { get; private set; } = "{}";
    /// <summary>How those answers read, for the "Was: …" note shown when one is changed.</summary>
    public string TaskPreviousJson { get; private set; } = "{}";
    /// <summary>Where "Send back for changes" on the task goes: an earlier task's name, or null for the requester.</summary>
    public string? TaskReturnsTo { get; private set; }
    /// <summary>Approvals already given on this request (an approver sending it back may ask for them all again).</summary>
    public bool HasEarlierApprovals { get; private set; }
    /// <summary>Waiting steps this person may pass to someone else that are not already shown as theirs.</summary>
    public List<ApprovalTask> OthersWaiting { get; private set; } = new();
    /// <summary>Waiting steps this person may pass to someone else.</summary>
    public HashSet<int> CanForward { get; private set; } = new();
    /// <summary>Link to every file on the request as one .zip, when there are two or more.</summary>
    public string? ZipUrl { get; private set; }

    public WorkflowRun Run { get; private set; } = null!;
    /// <summary>The form's letterhead (shown on the printout).</summary>
    public string? LetterheadUrl { get; private set; }
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
        await HttpContext.RequestServices.GetRequiredService<DefinitionStore>().FillAsync(_db, run);
        var email = User.Email();
        var tasks = await _db.ApprovalTasks.AsNoTracking().Where(t => t.RunId == id).ToListAsync();
        if (!await _access.CanViewRunAsync(User, run)) return false;
        CanManage = await _access.CanManageRunAsync(User, run);

        Run = run;
        LetterheadUrl = await Letterhead.UrlAsync(_db, run.WorkflowId);
        var pending = tasks.Where(t => t.Status == ApprovalStatus.Pending).ToList();
        // Someone who has already approved an "all must approve" step has nothing left to do.
        var mine = pending.Where(t => (t.CanBeDecidedBy(email) && !t.IsDoneFor(email)) || (User.IsAdmin() && !t.CanBeDecidedBy(email))).ToList();
        MyTasks = mine.Where(t => t.Kind != TaskKinds.Task).ToList();
        MyWork = mine.Where(t => t.Kind == TaskKinds.Task).ToList();
        // Anyone asked can pass their place on; owners and admins can pass any waiting step on.
        foreach (var t in pending.Where(t => CanManage || (t.CanBeDecidedBy(email) && !t.IsDoneFor(email)))) CanForward.Add(t.Id);
        OthersWaiting = pending.Where(t => CanForward.Contains(t.Id) && !mine.Contains(t)).ToList();
        if (await _db.Attachments.AsNoTracking().CountAsync(a => a.RunId == id) >= 2) ZipUrl = $"/files/run/{id}/zip";
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
        HasEarlierApprovals = context.GetValueOrDefault("approvals") is List<object?> { Count: > 0 };
        if (MyWork.FirstOrDefault() is { } work && def.Flow.Nodes.FirstOrDefault(n => n.Id == work.NodeId) is { } workNode)
        {
            TaskFormJson = Json.Serialize(new FormDefinition { Fields = workNode.Fields ?? new List<FieldDef>(), SubmitLabel = "Complete task" }).Replace("<", "\\u003c");
            // Sent back to be done again: start from what was entered last time.
            var again = Values.NewDict();
            var shown = new Dictionary<string, string>();
            foreach (var f in (workNode.Fields ?? new List<FieldDef>()).Where(f => !FieldTypes.IsLayout(f.Type) && f.Type != FieldTypes.Calculated))
            {
                if (!context.TryGetValue(f.Key, out var v) || Values.IsBlank(v)) continue;
                again[f.Key] = v;
                shown[f.Key] = Ui.Display(v, f.Type, f.Format, f.Decimals);
            }
            TaskValuesJson = Json.Serialize(again).Replace("<", "\\u003c");
            TaskPreviousJson = Json.Serialize(shown).Replace("<", "\\u003c");
            TaskReturnsTo = tasks.Where(t => t.Kind == TaskKinds.Task && t.Status == ApprovalStatus.Done && t.NodeId != work.NodeId
                    && def.Flow.Nodes.Any(n => n.Id == t.NodeId && n.Type == NodeTypes.Task))
                .OrderByDescending(t => t.DecidedAt).ThenByDescending(t => t.Id).Select(t => t.Title).FirstOrDefault();
        }

        // Student fields, and anything worked out from them, are hidden from people without student access.
        var studentKeys = SchoolFlow.Core.Forms.StudentData.KeysOf(def);
        // A request keeps only the student number. The details are looked up now, for people who may see
        // this student: anyone asked to act on the request, the requester if they have student access, and
        // otherwise people whose student access covers the student's location or district.
        var seesStudents = studentKeys.Count == 0;
        if (!seesStudents)
        {
            var actsOnIt = tasks.Any(t => StudentVisibility.Involves(t, email));
            var filter = await HttpContext.RequestServices.GetRequiredService<StudentScopeService>().FilterForAsync(email, HttpContext.RequestAborted);
            var answers = new StudentAnswers(_roster, _calendar);
            var ids = StudentAnswers.IdsOf(def, context).ToList();
            IReadOnlyDictionary<string, SchoolFlow.Core.StudentInfo> known = new Dictionary<string, SchoolFlow.Core.StudentInfo>();
            try { known = await answers.LookupAsync(ids, HttpContext.RequestAborted); }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException) { RosterProblem = true; }
            seesStudents = StudentVisibility.MaySee(actsOnIt, isSubmitter, filter, ids, known);
            if (seesStudents && !RosterProblem) await answers.RestoreAsync(def, context, known, HttpContext.RequestAborted);
        }
        // How answers read before the latest change ("Edited · was: …").
        var previous = context.GetValueOrDefault("_previous") as Dictionary<string, object?>;
        // The journey: what has happened, where it is now (and whose desk), and what is still to come.
        var nameOf = await Journey.NamesAsync(_db, tasks.SelectMany(t => t.AssigneeList.Concat(t.StandInPairs.Select(p => p.StandIn)).Concat(t.EscalatedList)
            .Append(t.DecidedBy ?? ""))
            // and the people the request names (chain approvers, the requester) for stops still to come
            .Concat(System.Text.RegularExpressions.Regex.Matches(run.ContextJson, @"[A-Za-z0-9._%+\-']+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}").Select(m => m.Value)));
        Stops = Journey.Build(def, run, context, tasks, DateTime.UtcNow, await _calendar.ClosedDaysAsync(), nameOf,
            seesStudents ? new HashSet<string>() : studentKeys);
        string? Was(string key) => previous is not null && previous.TryGetValue(key, out var was) && !studentKeys.Contains(key) ? Values.ToText(was) : null;
        AddAnswers(def.Form.Fields, run.CreatedAt);
        // What was added along the way by completed tasks (the latest time each task was done).
        foreach (var done in tasks.Where(t => t.Kind == TaskKinds.Task && t.Status == ApprovalStatus.Done).GroupBy(t => t.NodeId).Select(g => g.OrderBy(t => t.Id).Last()).OrderBy(t => t.Id))
        {
            if (def.Flow.Nodes.FirstOrDefault(n => n.Id == done.NodeId)?.Fields is not { Count: > 0 } taskFields) continue;
            Fields.Add(new Answer(done.Title, $"Completed by {done.DecidedBy}{(done.DecidedAt is DateTime at ? " · " + Ui.Local(at).ToString("MMM d, yyyy h:mm tt") : "")}", Heading: true));
            AddAnswers(taskFields, done.DecidedAt ?? run.UpdatedAt);
        }
        return true;

        void AddAnswers(IEnumerable<FieldDef> answered, DateTime signedAt)
        {
        foreach (var f in answered.Where(f => !FieldTypes.IsLayout(f.Type)))
        {
            var value = context.GetValueOrDefault(f.Key);
            if (Values.IsBlank(value)) continue; // hidden or unanswered (an unticked box is "No", not blank)
            if (!seesStudents && studentKeys.Contains(f.Key))
            {
                Fields.Add(new Answer(f.Label, "Restricted — you do not have access to student information."));
                continue;
            }
            if (f.Type == FieldTypes.Attachments && value is List<object?> uploaded)
            {
                Fields.Add(new Answer(f.Label, "", Files: uploaded.OfType<Dictionary<string, object?>>().Select(file => new FileLink(
                    Values.ToText(file.GetValueOrDefault("name")),
                    AttachmentService.DisplaySize(Values.TryToNumber(file.GetValueOrDefault("size"), out var bytes) ? (long)bytes : 0),
                    "/files/" + Uri.EscapeDataString(Values.ToText(file.GetValueOrDefault("id"))))).ToList(), PreviousValue: Was(f.Key)));
            }
            else if (f.Type == FieldTypes.Table && value is List<object?> rows)
            {
                var columns = f.Columns ?? new List<ColumnDef>();
                Fields.Add(new Answer(f.Label, "", columns.Select(c => c.Label).ToList(),
                    rows.OfType<Dictionary<string, object?>>()
                        .Select(r => columns.Select(c => Ui.Display(r.GetValueOrDefault(c.Key), c.Type, c.Format, c.Decimals)).ToList()).ToList(),
                    PreviousValue: Was(f.Key)));
            }
            else if (f.Type == FieldTypes.Signature)
            {
                Fields.Add(new Answer(f.Label, Values.ToText(value).Trim(), PreviousValue: Was(f.Key), SignedAt: signedAt));
            }
            else
            {
                Fields.Add(new Answer(f.Label, Ui.Display(value, f.Type, f.Format, f.Decimals), PreviousValue: Was(f.Key)));
            }
        }
        }
    }

    /// <summary>The live help desk ticket (status and public replies), when the request opened one.</summary>
    public TicketView? Ticket { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (Run.TicketId is long ticketId)
            Ticket = await TicketView.LoadAsync(_freshservice, HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), ticketId, ct);
        return Page();
    }

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

    public async Task<IActionResult> OnPostReturnTaskAsync(int id, int taskId, string? comment, CancellationToken ct)
    {
        var belongs = await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.RunId == id, ct);
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (!belongs || run is null || !await _access.CanViewRunAsync(User, run, ct)) return NotFound();
        Flash(await _runs.ReturnTaskAsync(taskId, User.Email(), User.DisplayName(), User.IsAdmin(), comment, ct), "Sent back. They have been told what to change.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostForwardAsync(int id, int taskId, string? to, string? note, CancellationToken ct)
    {
        var belongs = await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.RunId == id, ct);
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (!belongs || run is null || !await _access.CanViewRunAsync(User, run, ct)) return NotFound();
        var canManage = await _access.CanManageRunAsync(User, run, ct);
        var domains = HttpContext.RequestServices.GetRequiredService<DomainPolicy>().Domains;
        Flash(await _runs.ForwardAsync(taskId, User.Email(), User.DisplayName(), User.IsAdmin(), canManage, to, note, domains, ct),
            $"Passed on. {(to ?? "").Trim()} has been told.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDecideAsync(int id, int taskId, string? decision, string? comment, bool restart, CancellationToken ct)
    {
        // The task must belong to this request; RunService checks the assignee.
        var belongs = await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.Id == taskId && t.RunId == id, ct);
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (!belongs || run is null || !await _access.CanViewRunAsync(User, run, ct)) return NotFound();
        var action = decision switch { "approve" => ApprovalAction.Approve, "return" => ApprovalAction.Return, "reject" => ApprovalAction.Reject, _ => (ApprovalAction?)null };
        if (action is null) return BadRequest();
        var result = await _runs.DecideAsync(taskId, User.Email(), User.DisplayName(), User.IsAdmin(), action.Value, comment, ct, restart);
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
