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
    private readonly ILogger<RunService> _log;

    public RunService(AppDbContext db, IFreshserviceClient freshservice, ILogger<RunService> log)
    {
        _db = db;
        _freshservice = freshservice;
        _engine = new WorkflowEngine(freshservice);
        _log = log;
    }

    public static Dictionary<string, object?> SubmitterRecord(string email, string name)
    {
        var record = Values.NewDict();
        record["email"] = email;
        record["name"] = name;
        return record;
    }

    // ---- starting --------------------------------------------------------------------

    /// <summary>Validates the submitted form against the published definition and, if valid, starts a run.</summary>
    public async Task<(FormResult Form, int? RunId)> SubmitFormAsync(Workflow workflow, Dictionary<string, object?> input,
        string email, string name, CancellationToken ct)
    {
        var def = Json.ParseDefinition(workflow.PublishedJson ?? "{}");
        var ambient = Values.NewDict();
        ambient["submitter"] = SubmitterRecord(email, name);
        var form = FormEvaluator.Evaluate(def.Form, input, ambient);
        if (!form.IsValid) return (form, null);

        var context = Values.NewDict();
        foreach (var kv in form.Values) context[kv.Key] = kv.Value;
        context["submitter"] = SubmitterRecord(email, name);

        var run = NewRun(workflow, email, name);
        var id = await StartAsync(run, def, context, ct);
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

    private async Task<int> StartAsync(WorkflowRun run, WorkflowDefinition def, Dictionary<string, object?> context, CancellationToken ct)
    {
        // Save first so the run has an ID that formulas (run.id) and the timeline can use.
        run.ContextJson = Json.Serialize(context);
        _db.Runs.Add(run);
        await _db.SaveChangesAsync(ct);

        var runInfo = Values.NewDict();
        runInfo["id"] = (decimal)run.Id;
        runInfo["workflow"] = run.WorkflowName;
        context["run"] = runInfo;

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

        task.Status = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        task.DecidedBy = email;
        task.Comment = comment;
        task.DecidedAt = DateTime.UtcNow;

        var def = Json.ParseDefinition(run.DefinitionJson);
        var state = Load(run);
        await _engine.ResumeWithDecisionAsync(def.Flow, state, new ApprovalDecision
        {
            Approved = approved,
            By = string.IsNullOrWhiteSpace(name) ? email : $"{name} ({email})",
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
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        else if (state.Status != RunStatus.WaitingApproval)
        {
            // The run moved on (cancelled, failed elsewhere…): nothing should stay in anyone's inbox.
            foreach (var t in pending) t.Status = ApprovalStatus.Cancelled;
        }

        await _db.SaveChangesAsync(ct);
    }
}
