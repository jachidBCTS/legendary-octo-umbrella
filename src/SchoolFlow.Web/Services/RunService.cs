using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

public sealed record ActionResult(bool Ok, string? Error = null, int? RunId = null)
{
    public static ActionResult Fail(string error) => new(false, error);
}

/// <summary>Starts, resumes and persists workflow runs. All state changes to runs go through here.</summary>
public sealed class RunService
{
    private readonly AppDbContext _db;
    private readonly IFreshserviceClient _freshservice;
    private readonly WorkflowEngine _engine;
    private readonly NotificationService _notifications;
    private readonly ILogger<RunService> _log;

    private readonly AttachmentService _attachments;

    public RunService(AppDbContext db, IFreshserviceClient freshservice, NotificationService notifications, AttachmentService attachments, ILogger<RunService> log)
    {
        _db = db;
        _freshservice = freshservice;
        _notifications = notifications;
        _attachments = attachments;
        _engine = new WorkflowEngine(freshservice, directory: new ApprovalDirectory(db), files: attachments);
        _log = log;
    }

    /// <summary>Active departments (id → name), the list a Department field accepts.</summary>
    public async Task<Dictionary<long, string>> ActiveDepartmentsAsync(CancellationToken ct) =>
        (await _db.Departments.AsNoTracking().Where(d => d.IsActive).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
        .ToDictionary(d => (long)d.Id, d => d.Name);

    public static Dictionary<string, object?> SubmitterRecord(string email, string name)
    {
        var record = Values.NewDict();
        record["email"] = email;
        record["name"] = name;
        return record;
    }

    /// <summary>Active locations (id → name), the list a Location field accepts.</summary>
    public async Task<Dictionary<long, string>> ActiveLocationsAsync(CancellationToken ct) =>
        (await _db.Locations.AsNoTracking().Where(l => l.IsActive).Select(l => new { l.Id, l.Name }).ToListAsync(ct))
        .ToDictionary(l => (long)l.Id, l => l.Name);

    // ---- starting --------------------------------------------------------------------

    /// <summary>Validates the submitted form against the published definition and, if valid, starts a run.</summary>
    public async Task<(FormResult Form, int? RunId)> SubmitFormAsync(Workflow workflow, Dictionary<string, object?> input,
        string email, string name, CancellationToken ct)
    {
        var def = Json.ParseDefinition(workflow.PublishedJson ?? "{}");
        var ambient = Values.NewDict();
        ambient["submitter"] = SubmitterRecord(email, name);
        var form = FormEvaluator.Evaluate(def.Form, input, ambient, departments: await ActiveDepartmentsAsync(ct),
            locations: await ActiveLocationsAsync(ct),
            attachments: await _attachments.PendingForAsync(email, workflow.Id, ct));
        if (!form.IsValid) return (form, null);

        var context = Values.NewDict();
        foreach (var kv in form.Values) context[kv.Key] = kv.Value;
        context["submitter"] = SubmitterRecord(email, name);

        var run = NewRun(workflow, email, name);
        // The first Department field on the form categorises the request.
        var departmentField = def.Form.Fields.FirstOrDefault(f => f.Type == FieldTypes.Department && form.Values.GetValueOrDefault(f.Key) is Dictionary<string, object?>);
        if (departmentField is not null && form.Values[departmentField.Key] is Dictionary<string, object?> department
            && Values.TryToNumber(department.GetValueOrDefault("id"), out var departmentId))
        {
            run.DepartmentId = (long)departmentId;
            run.DepartmentName = Values.ToText(department.GetValueOrDefault("name"));
        }
        // Every uploaded file on the form, as one list flow steps can use (e.g. to attach them to a ticket).
        var files = new List<object?>();
        foreach (var field in def.Form.Fields.Where(f => f.Type == FieldTypes.Attachments))
        {
            if (form.Values.GetValueOrDefault(field.Key) is not List<object?> list) continue;
            foreach (var file in list.OfType<Dictionary<string, object?>>())
            {
                var copy = Values.NewDict();
                foreach (var kv in file) copy[kv.Key] = kv.Value;
                copy["field"] = field.Key;
                files.Add(copy);
            }
        }
        context["attachments"] = files;

        // The files now belong to this request: claim them before any step runs, so they can never be
        // deleted or attached to a second request while this one is in progress.
        var fileIds = files.OfType<Dictionary<string, object?>>().Select(f => Values.ToText(f.GetValueOrDefault("id"))).ToList();
        var id = await StartAsync(run, def, context, ct, async runId =>
        {
            if (fileIds.Count == 0) return;
            var claimed = await _db.Attachments.Where(a => fileIds.Contains(a.PublicId) && a.OwnerEmail == email && a.RunId == null).ToListAsync(ct);
            foreach (var attachment in claimed) attachment.RunId = runId;
            await _db.SaveChangesAsync(ct);
        });
        return (form, id);
    }

    private static WorkflowRun NewRun(Workflow workflow, string email, string name) => new()
    {
        WorkflowId = workflow.Id,
        WorkflowName = workflow.Name,
        WorkflowVersion = workflow.PublishedVersion,
        DefinitionJson = workflow.PublishedJson ?? "{}",
        SubmitterEmail = email,
        SubmitterName = name,
        Status = RunStatus.Running,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task<int> StartAsync(WorkflowRun run, WorkflowDefinition def, Dictionary<string, object?> context, CancellationToken ct,
        Func<int, Task>? beforeFirstStep = null)
    {
        // Save first so the run has an ID that formulas (run.id) and the timeline can use.
        run.ContextJson = Json.Serialize(context);
        _db.Runs.Add(run);
        await _db.SaveChangesAsync(ct);

        var runInfo = Values.NewDict();
        runInfo["id"] = (decimal)run.Id;
        runInfo["workflow"] = run.WorkflowName;
        context["run"] = runInfo;

        if (beforeFirstStep is not null) await beforeFirstStep(run.Id);

        var state = new RunState { Context = context };
        state.NewEvents.Add(new RunEvent(DateTime.Now, null, "info",
            string.IsNullOrEmpty(run.SubmitterEmail) ? "Started by a Freshservice ticket event." : $"Submitted by {run.SubmitterName}."));
        await _engine.StartAsync(def.Flow, state, ct);
        await SaveAsync(run, state, ct);
        return run.Id;
    }

    // ---- approvals -------------------------------------------------------------------

    public async Task<ActionResult> DecideAsync(int taskId, string email, string name, bool isAdmin, bool approved, string? comment, CancellationToken ct)
    {
        var task = await _db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return ActionResult.Fail("That approval no longer exists.");
        if (!task.IsAssignedTo(email) && !isAdmin) return ActionResult.Fail("This approval is not assigned to you.");
        if (task.Status != ApprovalStatus.Pending) return ActionResult.Fail($"This was already {task.Status.ToLowerInvariant()} by {task.DecidedBy}.");

        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == task.RunId, ct);
        if (run is null || run.Status != RunStatus.WaitingApproval || run.CurrentNodeId != task.NodeId)
            return ActionResult.Fail("The request is no longer waiting on this approval.");

        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment?.Length > 2000) comment = comment[..2000];
        var who = string.IsNullOrWhiteSpace(name) ? email : $"{name} ({email})";

        // "All must approve": collect approvals until everyone has; a rejection still ends it at once.
        // An admin who is not one of the approvers acts as an override and completes the step.
        if (approved && task.Rule == ApprovalRules.All && task.IsAssignedTo(email))
        {
            if (task.HasApproved(email)) return ActionResult.Fail("You have already approved this. It is waiting for the other approvers.");
            var approvedBy = task.ApprovedByList.Append(email).Distinct().ToList();
            var remaining = task.AssigneeList.Except(approvedBy).ToList();
            task.ApprovedBy = ApprovalTask.Pack(approvedBy);
            if (remaining.Count > 0)
            {
                var note = comment is null ? "" : $" — “{comment}”";
                _db.RunEvents.Add(new RunEventRow
                {
                    RunId = run.Id, At = DateTime.UtcNow, NodeId = task.NodeId, Kind = "approved",
                    Message = $"Approved by {who}{note} ({approvedBy.Count} of {approvedBy.Count + remaining.Count}). Still waiting for {string.Join(", ", remaining)}.",
                });
                run.UpdatedAt = DateTime.UtcNow;
                run.Version++;
                try
                {
                    await _db.SaveChangesAsync(ct);
                    return new ActionResult(true, RunId: run.Id);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _db.ChangeTracker.Clear();
                    return ActionResult.Fail("Someone else acted on this request at the same moment. Refresh to see the latest.");
                }
            }
        }

        task.Status = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        task.DecidedBy = email;
        task.Comment = comment;
        task.DecidedAt = DateTime.UtcNow;

        var def = Json.ParseDefinition(run.DefinitionJson);
        var state = Load(run);
        await _engine.ResumeWithDecisionAsync(def.Flow, state, new ApprovalDecision
        {
            Approved = approved,
            By = who,
            Comment = comment,
        }, ct);

        return await TrySaveAsync(run, state, ct)
            ? new ActionResult(true, RunId: run.Id)
            : ActionResult.Fail("Someone else acted on this request at the same moment. Refresh to see the latest.");
    }

    // ---- admin actions ---------------------------------------------------------------

    public async Task<ActionResult> RetryAsync(int runId, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return ActionResult.Fail("Request not found.");
        if (run.Status != RunStatus.Failed) return ActionResult.Fail("Only failed requests can be retried.");
        var def = Json.ParseDefinition(run.DefinitionJson);
        var state = Load(run);
        await _engine.RetryAsync(def.Flow, state, ct);
        return await TrySaveAsync(run, state, ct) ? new ActionResult(true, RunId: run.Id) : ActionResult.Fail("The request changed while retrying. Refresh and try again.");
    }

    public async Task<ActionResult> CancelAsync(int runId, string by, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return ActionResult.Fail("Request not found.");
        if (RunStatus.IsFinished(run.Status)) return ActionResult.Fail("This request has already finished.");

        var state = Load(run);
        state.Status = RunStatus.Cancelled;
        state.PendingApproval = null;
        state.WaitingTicketId = null;
        state.NewEvents.Add(new RunEvent(DateTime.Now, null, "cancelled", $"Cancelled by {by}."));
        return await TrySaveAsync(run, state, ct) ? new ActionResult(true, RunId: run.Id) : ActionResult.Fail("The request changed while cancelling. Refresh and try again.");
    }

    // ---- Freshservice webhook --------------------------------------------------------

    /// <summary>
    /// Handles a ticket event: resumes runs waiting on that ticket and starts ticket-triggered workflows.
    /// Returns a short description of what happened, for the webhook log.
    /// </summary>
    public async Task<string> HandleTicketEventAsync(Dictionary<string, object?> payload, CancellationToken ct)
    {
        var ticket = payload.GetValueOrDefault("ticket") as Dictionary<string, object?> ?? Values.NewDict();
        var ticketId = ParseTicketId(ticket.GetValueOrDefault("id"));
        if (ticketId is null) return "Ignored: the payload has no ticket.id.";

        // Placeholders in Freshservice webhooks give display values ("Closed", "INC-12"); the API gives
        // the numeric fields formulas expect. Prefer the API copy and keep any extra payload fields.
        var merged = Values.NewDict();
        foreach (var kv in ticket) merged[kv.Key] = kv.Value;
        if (_freshservice.IsLive)
        {
            try
            {
                var fresh = await _freshservice.GetTicketAsync(ticketId.Value, ct);
                if (fresh is not null) foreach (var kv in fresh) merged[kv.Key] = kv.Value;
            }
            catch (Exception ex) when (ex is FreshserviceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Could not refresh ticket {TicketId} from Freshservice; using webhook payload only", ticketId);
            }
        }
        merged["id"] = (decimal)ticketId.Value;

        var resumed = 0;
        var waiting = await _db.Runs.Where(r => r.Status == RunStatus.WaitingTicket && r.WaitingTicketId == ticketId).ToListAsync(ct);
        foreach (var run in waiting)
        {
            var def = Json.ParseDefinition(run.DefinitionJson);
            var state = Load(run);
            if (payload.TryGetValue("event", out var ev)) state.Context["event"] = ev;
            var moved = await _engine.ResumeWithTicketEventAsync(def.Flow, state, merged, ct);
            if (await TrySaveAsync(run, state, ct) && moved) resumed++;
        }

        var started = 0;
        var candidates = await _db.Workflows
            .Where(w => w.PublishedJson != null && w.IsEnabled && w.TriggerType == TriggerTypes.Ticket).ToListAsync(ct);
        foreach (var workflow in candidates)
        {
            var def = Json.ParseDefinition(workflow.PublishedJson!);
            var context = Values.NewDict();
            foreach (var kv in payload) if (!DefinitionValidator.Reserved.Contains(kv.Key) || kv.Key.Equals("event", StringComparison.OrdinalIgnoreCase)) context[kv.Key] = kv.Value;
            context["ticket"] = merged;

            if (!string.IsNullOrWhiteSpace(def.Trigger.Condition))
            {
                try
                {
                    if (!Values.ToBool(Formula.Evaluate(def.Trigger.Condition, new Scope(context)))) continue;
                }
                catch (ExpressionException ex)
                {
                    _log.LogWarning("Trigger condition of workflow {Id} failed: {Message}", workflow.Id, ex.Message);
                    continue;
                }
            }

            // One run per ticket per workflow: without this, a workflow that updates the ticket it was
            // triggered by would re-trigger itself through the webhook forever.
            if (await _db.Runs.AnyAsync(r => r.WorkflowId == workflow.Id && r.TriggerTicketId == ticketId, ct)) continue;

            var run = NewRun(workflow, "", "Freshservice");
            run.TriggerTicketId = ticketId;
            run.TicketId = ticketId;
            await StartAsync(run, def, context, ct);
            started++;
        }

        return $"Ticket #{ticketId}: resumed {resumed} waiting request(s), started {started} workflow(s).";
    }

    public static long? ParseTicketId(object? value)
    {
        if (value is decimal d && d > 0 && d == Math.Floor(d) && d < long.MaxValue) return (long)d;
        // Display IDs look like "INC-123" or "SR-45".
        var digits = new string(Values.ToText(value).Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 18 && long.TryParse(digits, out var id) && id > 0 ? id : null;
    }

    // ---- persistence -----------------------------------------------------------------

    private static RunState Load(WorkflowRun run) => new()
    {
        Status = run.Status,
        CurrentNodeId = run.CurrentNodeId,
        Context = Json.ToValues(run.ContextJson),
        WaitingTicketId = run.WaitingTicketId,
        Error = run.Error,
    };

    private async Task<bool> TrySaveAsync(WorkflowRun run, RunState state, CancellationToken ct)
    {
        try
        {
            await SaveAsync(run, state, ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _log.LogWarning("Run {RunId} was changed concurrently; discarding this update", run.Id);
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task SaveAsync(WorkflowRun run, RunState state, CancellationToken ct)
    {
        var statusBefore = run.Status;
        var link = $"/Requests/Detail/{run.Id}";
        var label = $"{run.WorkflowName} #{run.Id}";
        run.Status = state.Status;
        run.CurrentNodeId = state.CurrentNodeId;
        run.ContextJson = Json.Serialize(state.Context);
        run.WaitingTicketId = state.Status == RunStatus.WaitingTicket ? state.WaitingTicketId : null;
        run.Error = state.Status == RunStatus.Failed ? state.Error : null;
        run.UpdatedAt = DateTime.UtcNow;
        run.Version++;
        if (state.Context.GetValueOrDefault("ticket") is Dictionary<string, object?> ticket && ParseTicketId(ticket.GetValueOrDefault("id")) is long ticketId)
            run.TicketId = ticketId;

        var at = DateTime.UtcNow;
        foreach (var e in state.NewEvents)
        {
            // Keep insertion order stable even when several events share a timestamp.
            at = at.AddTicks(1);
            _db.RunEvents.Add(new RunEventRow { RunId = run.Id, At = at, NodeId = e.NodeId, Kind = e.Kind, Message = e.Message });
        }
        state.NewEvents.Clear();

        // The database filter can return a tracked task that was just decided in memory, so filter again.
        var pending = (await _db.ApprovalTasks.Where(t => t.RunId == run.Id && t.Status == ApprovalStatus.Pending).ToListAsync(ct))
            .Where(t => t.Status == ApprovalStatus.Pending).ToList();
        if (state.Status == RunStatus.WaitingApproval && state.PendingApproval is { } approval)
        {
            if (!pending.Any(t => t.NodeId == approval.NodeId))
            {
                _db.ApprovalTasks.Add(new ApprovalTask
                {
                    RunId = run.Id,
                    NodeId = approval.NodeId,
                    Title = approval.Title.Length > 300 ? approval.Title[..300] : approval.Title,
                    Instructions = approval.Instructions,
                    Assignees = ApprovalTask.Pack(approval.Assignees),
                    Rule = ApprovalRules.Normalize(approval.Rule),
                    Position = approval.Position,
                    CreatedAt = DateTime.UtcNow,
                });
                var from = string.IsNullOrEmpty(run.SubmitterName) ? "A request" : run.SubmitterName + "’s request";
                await _notifications.NotifyAsync(approval.Assignees, $"Approval needed: {label}",
                    $"{from} is waiting for your decision: {approval.Title}{(approval.Position.Length > 0 ? $" ({approval.Position.ToLowerInvariant()})" : "")}." +
                    (approval.Instructions.Length > 0 ? "\n\n" + approval.Instructions : ""), link, ct);
            }
        }
        else if (state.Status != RunStatus.WaitingApproval)
        {
            // The run moved on (cancelled, failed elsewhere…): nothing should stay in anyone's inbox.
            foreach (var t in pending) t.Status = ApprovalStatus.Cancelled;
        }

        // Messages from "Send notification" steps.
        foreach (var group in state.NewNotifications.GroupBy(n => (n.Title, n.Body)))
            await _notifications.NotifyAsync(group.Select(n => n.Email), group.Key.Title, group.Key.Body, link, ct);
        state.NewNotifications.Clear();

        // Tell the people who are waiting on the outcome.
        if (state.Status != statusBefore)
        {
            var submitter = new[] { run.SubmitterEmail };
            var decision = state.Context.GetValueOrDefault("approval") as Dictionary<string, object?>;
            var comment = Values.ToText(decision?.GetValueOrDefault("comment"));
            switch (state.Status)
            {
                case RunStatus.Completed:
                    await _notifications.NotifyAsync(submitter, $"Completed: {label}", "Your request has been completed.", link, ct);
                    break;
                case RunStatus.Rejected:
                    await _notifications.NotifyAsync(submitter, $"Not approved: {label}",
                        $"Your request was not approved{(decision is null ? "" : " by " + Values.ToText(decision.GetValueOrDefault("by")))}." +
                        (comment.Length > 0 ? $"\n\nComment: {comment}" : ""), link, ct);
                    break;
                case RunStatus.Cancelled:
                    await _notifications.NotifyAsync(submitter, $"Cancelled: {label}", "Your request was cancelled.", link, ct);
                    break;
                case RunStatus.Failed:
                    await _notifications.NotifyAsync(await _notifications.AdminEmailsAsync(ct), $"Needs attention: {label}",
                        $"This request stopped because of a problem and needs someone to look at it.\n\n{state.Error}", link, ct);
                    break;
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
