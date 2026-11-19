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
    private readonly SchoolCalendar _calendar;
    private WorkflowEngine? _engine;
    private readonly NotificationService _notifications;
    private readonly ILogger<RunService> _log;

    private readonly AttachmentService _attachments;

    public RunService(AppDbContext db, IFreshserviceClient freshservice, NotificationService notifications, AttachmentService attachments,
        SchoolCalendar calendar, ILogger<RunService> log, DomainPolicy? domains = null, PerDiemService? perDiem = null,
        IStudentRoster? roster = null, StudentScopeService? scope = null, DefinitionStore? definitions = null)
    {
        _defs = definitions ?? new DefinitionStore();
        _scope = scope;
        _roster = roster;
        _perDiem = perDiem;
        _domains = domains?.Domains;
        _calendar = calendar;
        _db = db;
        _freshservice = freshservice;
        _notifications = notifications;
        _attachments = attachments;
        _log = log;
    }

    private readonly PerDiemService? _perDiem;
    private readonly IStudentRoster? _roster;
    private readonly StudentScopeService? _scope;
    private readonly DefinitionStore _defs;

    /// <summary>District email domains approvers chosen on a form must use; null or empty for any.</summary>
    private readonly IReadOnlyList<string>? _domains;

    /// <summary>The engine, built once per request with the school calendar as it is now.</summary>
    private async Task<WorkflowEngine> EngineAsync(CancellationToken ct) =>
        _engine ??= new WorkflowEngine(_freshservice, directory: new ApprovalDirectory(_db), files: _attachments,
            closedDays: await _calendar.ClosedDaysAsync(ct)) { Teams = await TeamsAsync(ct), AllowedDomains = _domains };

    /// <summary>Every team by name, for steps that name a team as [Team name].</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> TeamsAsync(CancellationToken ct) =>
        (await _db.Teams.AsNoTracking().ToListAsync(ct))
        .ToDictionary(t => t.Name, t => (IReadOnlyList<string>)t.MemberList.ToList(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Active departments (id → name), the list a Department field accepts.</summary>
    public Task<Dictionary<long, string>> ActiveDepartmentsAsync(CancellationToken ct) => _calendar.ActiveDepartmentsAsync(ct);

    public static Dictionary<string, object?> SubmitterRecord(string email, string name)
    {
        var record = Values.NewDict();
        record["email"] = email;
        record["name"] = name;
        return record;
    }

    /// <summary>Active locations (id → name), the list a Location field accepts.</summary>
    public Task<Dictionary<long, string>> ActiveLocationsAsync(CancellationToken ct) => _calendar.ActiveLocationsAsync(ct);

    // ---- starting --------------------------------------------------------------------

    /// <summary>Validates the submitted form against the published definition and, if valid, starts a run.</summary>
    public async Task<(FormResult Form, int? RunId)> SubmitFormAsync(Workflow workflow, Dictionary<string, object?> input,
        string email, string name, CancellationToken ct)
    {
        var def = Json.ParseDefinition(workflow.PublishedJson ?? "{}");
        var ambient = Values.NewDict();
        ambient["submitter"] = SubmitterRecord(email, name);
        ambient["rates"] = await _calendar.RatesAsync(ct);
        var form = FormEvaluator.Evaluate(def.Form, input, ambient, departments: await ActiveDepartmentsAsync(ct),
            locations: await ActiveLocationsAsync(ct),
            attachments: await _attachments.PendingForAsync(email, workflow.Id, ct),
            closedDays: await _calendar.ClosedDaysAsync(ct),
            students: await new AccessService(_db, _roster).StudentsForAsync(def.Form, input, email, ct, _scope), allowedDomains: _domains,
            perDiem: _perDiem is null ? null : await _perDiem.DirectoryForAsync(def.Form, input, ct));
        DistrictAddresses.Check(def, form, _domains);
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
        var files = CollectFiles(def.Form, form.Values);
        context["attachments"] = files;

        // The files now belong to this request: claim them before any step runs, so they can never be
        // deleted or attached to a second request while this one is in progress.
        var fileIds = files.OfType<Dictionary<string, object?>>().Select(f => Values.ToText(f.GetValueOrDefault("id"))).ToList();
        var warning = PerDiemService.UsedFallback(def.Form, form.Values)
            ? "GSA per diem rates could not be looked up, so the standard rate was used. Please check the meal and hotel amounts." : null;
        var id = await StartAsync(run, def, context, ct, warning: warning, beforeFirstStep: async runId =>
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
        Func<int, Task>? beforeFirstStep = null, string? warning = null)
    {
        // Save first so the run has an ID that formulas (run.id) and the timeline can use. The definition is
        // stored once and shared by every run on this version.
        run.DefinitionId ??= await _defs.IdForAsync(_db, run.DefinitionJson, ct);
        run.ContextJson = Stored(run, context);
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
        if (warning is not null) state.NewEvents.Add(new RunEvent(DateTime.Now, null, "warning", warning));
        await (await EngineAsync(ct)).StartAsync(def.Flow, state, ct);
        await SaveAsync(run, state, ct);
        return run.Id;
    }

    // ---- approvals -------------------------------------------------------------------

    public Task<ActionResult> DecideAsync(int taskId, string email, string name, bool isAdmin, bool approved, string? comment, CancellationToken ct) =>
        DecideAsync(taskId, email, name, isAdmin, approved ? ApprovalAction.Approve : ApprovalAction.Reject, comment, ct);

    // ---- task steps ------------------------------------------------------------------------

    /// <summary>The pending task, its request and its fields, if this person may work on it.</summary>
    private async Task<(ApprovalTask Task, WorkflowRun Run, NodeDef Node, bool OnBehalf)?> OpenTaskAsync(int taskId, string email, bool isAdmin, CancellationToken ct)
    {
        var task = await _db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.Kind == TaskKinds.Task, ct);
        if (task is null || task.Status != ApprovalStatus.Pending) return null;
        if (!task.CanBeDecidedBy(email) && !isAdmin) return null;
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == task.RunId, ct);
        await _defs.FillAsync(_db, run, ct);
        if (run is null || run.Status != RunStatus.WaitingTask || run.CurrentNodeId != task.NodeId) return null;
        var node = Json.ParseDefinition(run.DefinitionJson).Flow.Nodes.FirstOrDefault(n => n.Id == task.NodeId && n.Type == NodeTypes.Task);
        return node is null ? null : (task, run, node, !task.CanBeDecidedBy(email));
    }

    /// <summary>The fields a task asks for, as a form. Null when the task is not this person's to do.</summary>
    public async Task<FormDefinition?> TaskFormAsync(int taskId, string email, bool isAdmin, CancellationToken ct) =>
        await OpenTaskAsync(taskId, email, isAdmin, ct) is { } open ? TaskForm(open.Node) : null;

    private static FormDefinition TaskForm(NodeDef node) => new() { Fields = node.Fields ?? new List<FieldDef>(), SubmitLabel = "Complete task" };

    private async Task<FormResult> EvaluateTaskAsync(WorkflowRun run, NodeDef node, IDictionary<string, object?> input, string email, bool applyDefaults, CancellationToken ct)
    {
        // Formulas in the task's fields can use everything already on the request (the form's answers,
        // earlier steps), so "required when total >= 150" works here too.
        var ambient = Json.ToValues(run.ContextJson);
        await Students.RestoreAsync(Json.ParseDefinition(run.DefinitionJson), ambient, ct);
        ambient["rates"] = await _calendar.RatesAsync(ct);
        // Files already on the request stay usable whoever uploaded them: a task done again by a stand-in
        // or an admin must be able to keep the receipts the first person attached.
        var usable = await _attachments.UsableForRunAsync(email, run, ct);
        foreach (var a in await _db.Attachments.AsNoTracking().Where(a => a.RunId == run.Id)
                     .Select(a => new { a.PublicId, a.FileName, a.Size }).ToListAsync(ct))
            usable.TryAdd(a.PublicId, new AttachmentInfo(a.PublicId, a.FileName, a.Size));
        var result = FormEvaluator.Evaluate(TaskForm(node), input, ambient, applyDefaults: applyDefaults,
            attachments: usable, closedDays: await _calendar.ClosedDaysAsync(ct));
        DistrictAddresses.Check(Json.ParseDefinition(run.DefinitionJson), result, _domains);
        return result;
    }

    public async Task<FormResult?> EvaluateTaskAsync(int taskId, IDictionary<string, object?> input, string email, bool isAdmin, bool applyDefaults, CancellationToken ct) =>
        await OpenTaskAsync(taskId, email, isAdmin, ct) is { } open ? await EvaluateTaskAsync(open.Run, open.Node, input, email, applyDefaults, ct) : null;

    /// <summary>The request a pending task belongs to, if this person may upload a file to it.</summary>
    public async Task<(WorkflowRun Run, FormDefinition Form)?> TaskUploadTargetAsync(int taskId, string email, bool isAdmin, CancellationToken ct) =>
        await OpenTaskAsync(taskId, email, isAdmin, ct) is { } open ? (open.Run, TaskForm(open.Node)) : null;

    /// <summary>Completes a task: checks its fields, adds the answers (and any files) to the request, and moves on.</summary>
    public async Task<(FormResult? Form, ActionResult Result)> CompleteTaskAsync(int taskId, IDictionary<string, object?> input, string email, string name, bool isAdmin, CancellationToken ct)
    {
        if (await OpenTaskAsync(taskId, email, isAdmin, ct) is not { } open)
            return (null, ActionResult.Fail("This task is no longer waiting for you. Refresh to see the latest."));
        var (task, run, node, onBehalf) = open;
        var form = await EvaluateTaskAsync(run, node, input, email, applyDefaults: false, ct);
        if (!form.IsValid) return (form, ActionResult.Fail("Please fix the highlighted fields."));

        var who = string.IsNullOrWhiteSpace(name) ? email : $"{name} ({email})";
        if (task.StandsInFor(email) is { } original) who += $", standing in for {original}";
        else if (task.IsEscalatedTo(email)) who += ", after the step was escalated";
        else if (onBehalf)
        {
            who += ", an admin acting on behalf of " + string.Join(", ", task.AssigneeList);
            _db.AuditEntries.Add(new AuditEntry
            {
                At = DateTime.UtcNow, ActorEmail = email, ActorName = name, Action = "task.override-completed",
                Target = $"{run.WorkflowName} #{run.Id}", Detail = $"Task “{task.Title}”, on behalf of {string.Join(", ", task.AssigneeList)}.",
            });
        }
        task.Status = ApprovalStatus.Done;
        task.DecidedBy = email;
        task.DecidedAt = DateTime.UtcNow;

        var def = Json.ParseDefinition(run.DefinitionJson);
        var state = await LoadAsync(run, ct);
        // A task done before (and sent back since) shows what changed, like a resubmitted form.
        var doneBefore = await _db.ApprovalTasks.AnyAsync(t => t.RunId == run.Id && t.NodeId == node.Id && t.Id != task.Id && t.Kind == TaskKinds.Task
            && t.Status == ApprovalStatus.Done, ct);
        if (doneBefore)
        {
            var fields = node.Fields ?? new List<FieldDef>();
            var (adjusted, previous) = Changes(fields, state.Context, form.Values, StudentData.KeysOf(def));
            RememberPrevious(state.Context, fields, previous);
            if (adjusted.Count > 0)
                state.NewEvents.Add(new RunEvent(DateTime.Now, node.Id, "info", $"{task.Title}: answers updated{Adjusted(adjusted)}."));
        }
        // The request's files, worked out again from the form and every task (with this task's new
        // answers), so a task done twice never lists a file twice. New uploads now belong to the request.
        var answers = new Dictionary<string, object?>(state.Context, StringComparer.OrdinalIgnoreCase);
        foreach (var field in (node.Fields ?? new List<FieldDef>()).Where(f => !FieldTypes.IsLayout(f.Type) && !string.IsNullOrWhiteSpace(f.Key)))
            answers[field.Key] = form.Values.TryGetValue(field.Key, out var v) ? v : null;
        var files = AllFiles(def, answers);
        state.Context["attachments"] = files;
        var fileIds = files.OfType<Dictionary<string, object?>>().Select(f => Values.ToText(f.GetValueOrDefault("id"))).ToList();
        foreach (var attachment in await _db.Attachments.Where(a => fileIds.Contains(a.PublicId) && a.OwnerEmail == email && a.RunId == null).ToListAsync(ct))
            attachment.RunId = run.Id;
        // Done again after being sent back: files taken off the task are deleted, not left downloadable.
        var removedFiles = doneBefore
            ? await _db.Attachments.Where(a => a.RunId == run.Id && !fileIds.Contains(a.PublicId)).ToListAsync(ct)
            : new List<Attachment>();
        if (removedFiles.Count > 0)
        {
            _db.Attachments.RemoveRange(removedFiles);
            state.NewEvents.Add(new RunEvent(DateTime.Now, node.Id, "info",
                $"Removed {removedFiles.Count} file{(removedFiles.Count == 1 ? "" : "s")} taken off the task: {string.Join(", ", removedFiles.Select(f => f.FileName))}."));
        }
        await (await EngineAsync(ct)).ResumeWithTaskAsync(def.Flow, state, who, form.Values, ct);
        if (!await TrySaveAsync(run, state, ct))
            return (form, ActionResult.Fail("Someone else acted on this request at the same moment. Refresh to see the latest."));
        _attachments.DeleteFiles(removedFiles);   // only once the database agrees they are gone
        return (form, new ActionResult(true, RunId: run.Id));
    }

    /// <summary>
    /// Records an approver's decision. The person may be one of the step's approvers, someone standing
    /// in for one, someone the step was escalated to, or an admin acting on their behalf.
    /// </summary>
    public async Task<ActionResult> DecideAsync(int taskId, string email, string name, bool isAdmin, ApprovalAction action, string? comment, CancellationToken ct,
        bool restartApprovals = false)
    {
        const string Raced = "Someone else acted on this request at the same moment. Refresh to see the latest.";
        var task = await _db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return ActionResult.Fail("That approval no longer exists.");
        if (task.Kind == TaskKinds.Task) return ActionResult.Fail("This is a task to complete, not an approval.");
        if (!task.CanBeDecidedBy(email) && !isAdmin) return ActionResult.Fail("This approval is not assigned to you.");
        if (task.Status != ApprovalStatus.Pending) return ActionResult.Fail($"This was already {task.Status.ToLowerInvariant()} by {task.DecidedBy}.");

        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == task.RunId, ct);
        await _defs.FillAsync(_db, run, ct);
        if (run is null || run.Status != RunStatus.WaitingApproval || run.CurrentNodeId != task.NodeId)
            return ActionResult.Fail("The request is no longer waiting on this approval.");

        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment?.Length > 2000) comment = comment[..2000];
        if (action == ApprovalAction.Return && comment is null)
            return ActionResult.Fail("Say what needs changing, so the requester knows what to fix.");

        var approved = action == ApprovalAction.Approve;
        var who = string.IsNullOrWhiteSpace(name) ? email : $"{name} ({email})";
        // Whose approval this counts as when every approver must sign: their own, or the person they stand in for.
        var countsAs = email;
        var completesStep = false; // escalation and admin override decide the whole step, whatever its rule
        if (task.IsAssignedTo(email))
        {
        }
        else if (task.StandsInFor(email) is { } original)
        {
            who += $", standing in for {original}";
            countsAs = original;
        }
        else if (task.IsEscalatedTo(email))
        {
            who += ", after the step was escalated";
            completesStep = true;
        }
        else
        {
            // An admin deciding a step they were not asked to approve is allowed, but never invisible:
            // the request's timeline says so and it goes in the audit log.
            who += ", an admin acting on behalf of " + string.Join(", ", task.AssigneeList);
            completesStep = true;
            _db.AuditEntries.Add(new AuditEntry
            {
                At = DateTime.UtcNow, ActorEmail = email, ActorName = name,
                Action = action switch { ApprovalAction.Approve => "approval.override-approved", ApprovalAction.Reject => "approval.override-rejected", _ => "approval.override-returned" },
                Target = $"{run.WorkflowName} #{run.Id}",
                Detail = $"Step “{task.Title}”, on behalf of {string.Join(", ", task.AssigneeList)}." + (comment is null ? "" : $" Comment: {comment}"),
            });
        }

        if (action == ApprovalAction.Return)
        {
            // Back to the requester. The step is not decided; it will be asked again after they resubmit.
            task.Status = ApprovalStatus.Returned;
            task.DecidedBy = email;
            task.Comment = comment;
            task.DecidedAt = DateTime.UtcNow;
            var state = await LoadAsync(run, ct);
            state.Status = RunStatus.ChangesRequested;
            // The approver can ask for every approval to be given again once the request comes back.
            var restart = restartApprovals && state.Context.GetValueOrDefault("approvals") is List<object?> { Count: > 0 };
            if (restart) state.Context["_restart"] = true;
            state.NewEvents.Add(new RunEvent(DateTime.Now, task.NodeId, "returned", $"Sent back for changes by {who} — “{comment}”" +
                (restart ? " Approvals will start again from the beginning when it is resubmitted." : "")));
            await _notifications.NotifyAsync(new[] { run.SubmitterEmail }, $"Changes needed: {run.WorkflowName} #{run.Id}",
                $"{(string.IsNullOrWhiteSpace(name) ? email : name)} sent your request back for changes:\n\n{comment}\n\nOpen the request, choose “Edit and resubmit”, and it will go back to " +
                (restart ? "the first approver." : "the same approver."),
                $"/Requests/Detail/{run.Id}", ct);
            return await TrySaveAsync(run, state, ct) ? new ActionResult(true, RunId: run.Id) : ActionResult.Fail(Raced);
        }

        // "All must approve": collect approvals until everyone has; a rejection still ends it at once.
        if (approved && task.Rule == ApprovalRules.All && !completesStep)
        {
            if (task.HasApproved(countsAs))
                return ActionResult.Fail(countsAs == email ? "You have already approved this. It is waiting for the other approvers."
                    : $"{countsAs} has already approved this. It is waiting for the other approvers.");
            var approvedBy = task.ApprovedByList.Append(countsAs).Distinct().ToList();
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
                    return ActionResult.Fail(Raced);
                }
            }
        }

        task.Status = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        task.DecidedBy = email;
        task.Comment = comment;
        task.DecidedAt = DateTime.UtcNow;

        var def = Json.ParseDefinition(run.DefinitionJson);
        var resumed = await LoadAsync(run, ct);
        await (await EngineAsync(ct)).ResumeWithDecisionAsync(def.Flow, resumed, new ApprovalDecision { Approved = approved, By = who, Comment = comment }, ct);
        return await TrySaveAsync(run, resumed, ct) ? new ActionResult(true, RunId: run.Id) : ActionResult.Fail(Raced);
    }

    /// <summary>
    /// Sends a task back. When an earlier task step was done on this request (for example the traveller's
    /// "after the trip" details, now being reconciled), it goes back to that step and its assignee is told
    /// why; otherwise it goes back to the requester to edit and resubmit, like an approver's "send back".
    /// </summary>
    public async Task<ActionResult> ReturnTaskAsync(int taskId, string email, string name, bool isAdmin, string? comment, CancellationToken ct)
    {
        if (await OpenTaskAsync(taskId, email, isAdmin, ct) is not { } open)
            return ActionResult.Fail("This task is no longer waiting for you. Refresh to see the latest.");
        var (task, run, node, onBehalf) = open;
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment is null) return ActionResult.Fail("Say what needs changing, so the person knows what to fix.");
        if (comment.Length > 2000) comment = comment[..2000];

        var who = string.IsNullOrWhiteSpace(name) ? email : $"{name} ({email})";
        if (task.StandsInFor(email) is { } original) who += $", standing in for {original}";
        else if (task.IsEscalatedTo(email)) who += ", after the step was escalated";
        else if (onBehalf)
        {
            who += ", an admin acting on behalf of " + string.Join(", ", task.AssigneeList);
            _db.AuditEntries.Add(new AuditEntry
            {
                At = DateTime.UtcNow, ActorEmail = email, ActorName = name, Action = "task.override-returned",
                Target = $"{run.WorkflowName} #{run.Id}", Detail = $"Task “{task.Title}”, on behalf of {string.Join(", ", task.AssigneeList)}. Comment: {comment}",
            });
        }
        task.Status = ApprovalStatus.Returned;
        task.DecidedBy = email;
        task.Comment = comment;
        task.DecidedAt = DateTime.UtcNow;

        var def = Json.ParseDefinition(run.DefinitionJson);
        var state = await LoadAsync(run, ct);
        var sender = string.IsNullOrWhiteSpace(name) ? email : name;
        var earlier = (await _db.ApprovalTasks.AsNoTracking()
                .Where(t => t.RunId == run.Id && t.Kind == TaskKinds.Task && t.Status == ApprovalStatus.Done && t.NodeId != node.Id)
                .OrderByDescending(t => t.DecidedAt).ThenByDescending(t => t.Id).ToListAsync(ct))
            .FirstOrDefault(t => def.Flow.Nodes.Any(n => n.Id == t.NodeId && n.Type == NodeTypes.Task));
        if (earlier is not null)
        {
            state.NewEvents.Add(new RunEvent(DateTime.Now, node.Id, "returned", $"Sent back to “{earlier.Title}” by {who} — “{comment}”"));
            await (await EngineAsync(ct)).ReturnToTaskAsync(def.Flow, state, earlier.NodeId, ct);
            // The comment travels with the task, so it is in the email and on the task itself.
            if (state.PendingApproval is { } again)
                again.Instructions = $"Sent back by {sender}: {comment}" + (again.Instructions.Length > 0 ? "\n\n" + again.Instructions : "");
        }
        else
        {
            state.Status = RunStatus.ChangesRequested;
            state.NewEvents.Add(new RunEvent(DateTime.Now, node.Id, "returned", $"Sent back for changes by {who} — “{comment}”"));
            await _notifications.NotifyAsync(new[] { run.SubmitterEmail }, $"Changes needed: {run.WorkflowName} #{run.Id}",
                $"{sender} sent your request back for changes:\n\n{comment}\n\nOpen the request, choose “Edit and resubmit”, and it will go back to the same step.",
                $"/Requests/Detail/{run.Id}", ct);
        }
        return await TrySaveAsync(run, state, ct) ? new ActionResult(true, RunId: run.Id)
            : ActionResult.Fail("Someone else acted on this request at the same moment. Refresh to see the latest.");
    }

    private static readonly System.Text.RegularExpressions.Regex EmailShape = new(@"^[^@\s;,<>]+@[^@\s;,<>]+\.[^@\s;,<>]+$");

    /// <summary>
    /// Hands a waiting approval or task to someone else. A person it is assigned to hands over their own
    /// place; a workflow owner or an admin (or a stand-in, or someone it was escalated to) replaces
    /// everyone it is waiting for. Recorded on the timeline and in the audit log.
    /// </summary>
    public async Task<ActionResult> ForwardAsync(int taskId, string email, string name, bool isAdmin, bool canManage, string? to, string? note,
        IReadOnlyList<string>? allowedDomains, CancellationToken ct)
    {
        const string Raced = "Someone else acted on this request at the same moment. Refresh to see the latest.";
        var task = await _db.ApprovalTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null || task.Status != ApprovalStatus.Pending) return ActionResult.Fail("This is no longer waiting on anyone. Refresh to see the latest.");
        if (!task.CanBeDecidedBy(email) && !isAdmin && !canManage) return ActionResult.Fail("This is not yours to pass on.");
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == task.RunId, ct);
        await _defs.FillAsync(_db, run, ct);
        if (run is null || !RunStatus.IsWaitingOnPeople(run.Status) || run.CurrentNodeId != task.NodeId)
            return ActionResult.Fail("The request is no longer waiting on this step.");

        to = (to ?? "").Trim().ToLowerInvariant();
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > 1000) note = note[..1000];
        if (!EmailShape.IsMatch(to) || to.Length > 254) return ActionResult.Fail("Enter one email address to pass this on to.");
        if (allowedDomains is { Count: > 0 } domains && !domains.Any(d => to.EndsWith("@" + d, StringComparison.OrdinalIgnoreCase)))
            return ActionResult.Fail($"Only district addresses can be chosen ({string.Join(" or ", domains.Select(d => "@" + d))}).");
        if (to == run.SubmitterEmail.ToLowerInvariant()) return ActionResult.Fail("A request cannot be passed to the person who asked for it.");
        if (task.IsAssignedTo(to)) return ActionResult.Fail($"{to} is already being asked.");

        var isTask = task.Kind == TaskKinds.Task;
        // The place being handed over: the person's own, or the one they stand in for. Owners, admins and
        // people it was escalated to have no place of their own, so they replace everyone.
        var slot = task.IsAssignedTo(email) ? email.ToLowerInvariant() : task.StandsInFor(email)?.ToLowerInvariant();
        var ownPlace = slot is not null;
        if (ownPlace && task.HasApproved(slot!)) return ActionResult.Fail("This step has already been approved for your place, so there is nothing to pass on.");
        var before = task.AssigneeList.ToList();
        var after = ownPlace ? before.Select(a => a == slot ? to : a).Distinct().ToList() : new List<string> { to };
        task.Assignees = ApprovalTask.Pack(after);
        task.ApprovedBy = ApprovalTask.Pack(task.ApprovedByList.Where(after.Contains));
        if (task.ApprovedBy == ";;") task.ApprovedBy = "";
        // Stand-ins follow the people now asked: worked out again for the new list.
        var standIns = await StandInsForAsync(after, run.SubmitterEmail, ct);
        task.StandIns = standIns.Count == 0 ? "" : ";" + string.Join(";", standIns.Select(p => $"{p.StandIn}={p.For}")) + ";";
        task.RebuildExtra();
        task.ReminderCount = 0;
        task.LastReminderAt = null;

        var state = await LoadAsync(run, ct);
        // An approval chain remembers who each step asks; keep it in step with the change.
        if (!isTask && state.Context.GetValueOrDefault("chain") is Dictionary<string, object?> chain
            && chain.GetValueOrDefault("steps") is List<object?> steps && Values.TryToNumber(chain.GetValueOrDefault("index"), out var position)
            && position >= 0 && position < steps.Count && steps[(int)position] is Dictionary<string, object?> step)
            step["approvers"] = after.Cast<object?>().ToList();

        var who = string.IsNullOrWhiteSpace(name) ? email : $"{name} ({email})";
        var replaced = slot ?? string.Join(", ", before);
        state.NewEvents.Add(new RunEvent(DateTime.Now, task.NodeId, "info",
            $"{task.Title}: passed from {replaced} to {to} by {who}{(note is null ? "" : $" — “{note}”")}"));
        _db.AuditEntries.Add(new AuditEntry
        {
            At = DateTime.UtcNow, ActorEmail = email, ActorName = name, Action = isTask ? "task.forwarded" : "approval.forwarded",
            Target = $"{run.WorkflowName} #{run.Id}", Detail = $"“{task.Title}” passed from {replaced} to {to}." + (note is null ? "" : $" Note: {note}"),
        });
        var label = $"{run.WorkflowName} #{run.Id}";
        var sender = string.IsNullOrWhiteSpace(name) ? email : name;
        await _notifications.NotifyAsync(new[] { to }.Concat(standIns.Select(p => p.StandIn)), isTask ? $"Task for you: {label}" : $"Approval needed: {label}",
            $"{sender} passed this to you: {task.Title}{(task.Position.Length > 0 ? $" ({task.Position.ToLowerInvariant()})" : "")}." +
            (note is null ? "" : $"\n\nNote: {note}") + (task.Instructions.Length > 0 ? "\n\n" + task.Instructions : ""),
            $"/Requests/Detail/{run.Id}", ct);
        return await TrySaveAsync(run, state, ct) ? new ActionResult(true, RunId: run.Id) : ActionResult.Fail(Raced);
    }

    // ---- change tracking --------------------------------------------------------------

    /// <summary>
    /// The fields whose answer changed: their labels (for the timeline) and how each read before (for
    /// "Edited · was: …"). Student details are listed by label but their old values are not kept.
    /// </summary>
    internal static (List<string> Labels, Dictionary<string, string> Previous) Changes(IEnumerable<FieldDef> fields,
        IDictionary<string, object?> before, IDictionary<string, object?> after, HashSet<string> tainted)
    {
        var labels = new List<string>();
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (FieldTypes.IsLayout(field.Type) || string.IsNullOrWhiteSpace(field.Key)) continue;
            var old = before.TryGetValue(field.Key, out var o) ? o : null;
            var now = after.TryGetValue(field.Key, out var n) ? n : null;
            if (ResubmitGuard.SameAnswer(field, old, now)) continue;
            labels.Add(string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label.Trim());
            if (tainted.Contains(field.Key) || field.Type == FieldTypes.Student) continue;
            var shown = field.Type == FieldTypes.Table && old is List<object?> rows
                ? $"{rows.Count} row{(rows.Count == 1 ? "" : "s")}"
                : Ui.Display(old, field.Type, field.Format, field.Decimals);
            previous[field.Key] = shown.Length > 300 ? shown[..300] + "…" : shown;
        }
        return (labels, previous);
    }

    /// <summary>Replaces the remembered "before" values for these fields with this round's changes.</summary>
    private static void RememberPrevious(Dictionary<string, object?> context, IEnumerable<FieldDef> fields, Dictionary<string, string> previous)
    {
        var store = context.GetValueOrDefault("_previous") as Dictionary<string, object?> ?? Values.NewDict();
        foreach (var field in fields) store.Remove(field.Key);
        foreach (var (key, value) in previous) store[key] = value;
        if (store.Count == 0) context.Remove("_previous"); else context["_previous"] = store;
    }

    private static string Adjusted(List<string> labels) => labels.Count == 0 ? "" :
        $" (adjusted: {string.Join(", ", labels.Take(8))}{(labels.Count > 8 ? $" and {labels.Count - 8} more" : "")})";

    // ---- the requester's own actions ---------------------------------------------------

    /// <summary>
    /// Resubmits a request that an approver sent back. The answers are checked against the version of the
    /// form the request started on, and the request returns to the approval step that asked for changes.
    /// </summary>
    public async Task<(FormResult? Form, ActionResult Result)> ResubmitAsync(int runId, Dictionary<string, object?> input, string email, string name, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        await _defs.FillAsync(_db, run, ct);
        if (run is null || run.SubmitterEmail != email || email.Length == 0) return (null, ActionResult.Fail("Request not found."));
        if (run.Status != RunStatus.ChangesRequested) return (null, ActionResult.Fail("This request is not waiting for changes."));

        var def = Json.ParseDefinition(run.DefinitionJson);
        var ambient = Values.NewDict();
        ambient["submitter"] = SubmitterRecord(email, run.SubmitterName);
        ambient["rates"] = await _calendar.RatesAsync(ct);
        var form = FormEvaluator.Evaluate(def.Form, input, ambient, departments: await ActiveDepartmentsAsync(ct), locations: await ActiveLocationsAsync(ct),
            attachments: await _attachments.UsableForRunAsync(email, run, ct), closedDays: await _calendar.ClosedDaysAsync(ct),
            students: await new AccessService(_db, _roster).StudentsForAsync(def.Form, input, email, ct, _scope), allowedDomains: _domains,
            perDiem: _perDiem is null ? null : await _perDiem.DirectoryForAsync(def.Form, input, ct));
        DistrictAddresses.Check(def, form, _domains);
        if (!form.IsValid) return (form, ActionResult.Fail("Please fix the highlighted fields."));

        var state = await LoadAsync(run, ct);
        var before = new Dictionary<string, object?>(state.Context, StringComparer.OrdinalIgnoreCase);
        // What the approver will see as "Edited · was: …", and whether approvals already given still count.
        var (adjusted, previous) = Changes(def.Form.Fields, before, form.Values, StudentData.KeysOf(def));
        var restart = state.Context.Remove("_restart") ||
            (before.GetValueOrDefault("approvals") is List<object?> { Count: > 0 }
             && ResubmitGuard.Reasons(def.Flow, run.CurrentNodeId, def.Form.Fields, before, form.Values).Count > 0);
        foreach (var kv in form.Values) state.Context[kv.Key] = kv.Value;
        RememberPrevious(state.Context, def.Form.Fields, previous);
        // The request's files: the form's (as resubmitted) and any that completed tasks added.
        var files = AllFiles(def, state.Context);
        state.Context["attachments"] = files;
        var fileIds = files.OfType<Dictionary<string, object?>>().Select(f => Values.ToText(f.GetValueOrDefault("id"))).ToList();
        foreach (var attachment in await _db.Attachments.Where(a => fileIds.Contains(a.PublicId) && a.OwnerEmail == email && a.RunId == null).ToListAsync(ct))
            attachment.RunId = run.Id;
        // Files the requester took off the request are deleted, not left attached and downloadable.
        var removedFiles = await _db.Attachments.Where(a => a.RunId == run.Id && !fileIds.Contains(a.PublicId)).ToListAsync(ct);
        if (removedFiles.Count > 0)
        {
            _db.Attachments.RemoveRange(removedFiles);
            state.NewEvents.Add(new RunEvent(DateTime.Now, state.CurrentNodeId, "info",
                $"Removed {removedFiles.Count} file{(removedFiles.Count == 1 ? "" : "s")} taken off the request: {string.Join(", ", removedFiles.Select(f => f.FileName))}."));
        }

        var department = def.Form.Fields.Where(f => f.Type == FieldTypes.Department).Select(f => form.Values.GetValueOrDefault(f.Key)).OfType<Dictionary<string, object?>>().FirstOrDefault();
        if (department is not null && Values.TryToNumber(department.GetValueOrDefault("id"), out var departmentId))
        {
            run.DepartmentId = (long)departmentId;
            run.DepartmentName = Values.ToText(department.GetValueOrDefault("name"));
        }

        state.NewEvents.Add(new RunEvent(DateTime.Now, state.CurrentNodeId, "info",
            $"Resubmitted with changes by {name}{Adjusted(adjusted)}."));
        if (PerDiemService.UsedFallback(def.Form, form.Values))
            state.NewEvents.Add(new RunEvent(DateTime.Now, state.CurrentNodeId, "warning",
                "GSA per diem rates could not be looked up, so the standard rate was used. Please check the meal and hotel amounts."));
        await (await EngineAsync(ct)).ReenterApprovalAsync(def.Flow, state, ct, restart);
        if (!await TrySaveAsync(run, state, ct))
            return (form, ActionResult.Fail("The request changed while you were editing. Refresh and try again."));
        _attachments.DeleteFiles(removedFiles);   // only once the database agrees they are gone
        return (form, new ActionResult(true, RunId: run.Id));
    }

    /// <summary>Lets the requester take back a request that is still waiting for approval (or for their own changes).</summary>
    public async Task<ActionResult> WithdrawAsync(int runId, string email, string name, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        await _defs.FillAsync(_db, run, ct);
        if (run is null || run.SubmitterEmail != email || email.Length == 0) return ActionResult.Fail("Request not found.");
        if (run.Status is not (RunStatus.WaitingApproval or RunStatus.ChangesRequested))
            return ActionResult.Fail("This request can no longer be withdrawn: it has already been approved or has finished.");

        // The people who were being asked no longer need to do anything.
        var waiting = await _db.ApprovalTasks.AsNoTracking().Where(t => t.RunId == run.Id && t.Status == ApprovalStatus.Pending).ToListAsync(ct);
        var approvers = waiting.SelectMany(t => t.AssigneeList.Concat(t.StandInPairs.Select(p => p.StandIn)).Concat(t.EscalatedList)).Distinct().ToList();
        var state = await LoadAsync(run, ct);
        state.Status = RunStatus.Cancelled;
        state.PendingApproval = null;
        state.NewEvents.Add(new RunEvent(DateTime.Now, null, "cancelled", $"Withdrawn by {name}."));
        await _notifications.NotifyAsync(approvers, $"Withdrawn: {run.WorkflowName} #{run.Id}",
            $"{name} withdrew this request, so it no longer needs your decision.", $"/Requests/Detail/{run.Id}", ct);
        return await TrySaveAsync(run, state, ct) ? new ActionResult(true, RunId: run.Id)
            : ActionResult.Fail("The request changed at the same moment. Refresh to see the latest.");
    }

    /// <summary>Adds a message to a request's discussion and tells the other people involved.</summary>
    public async Task<ActionResult> CommentAsync(WorkflowRun run, string email, string name, string? body, CancellationToken ct)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return ActionResult.Fail("Write a message first.");
        if (body.Length > 4000) return ActionResult.Fail("That message is too long (4,000 characters at most).");
        _db.RunComments.Add(new RunComment { RunId = run.Id, AuthorEmail = email, AuthorName = name, Body = body, CreatedAt = DateTime.UtcNow });

        // The requester, whoever is being asked to decide right now, and anyone already in the conversation.
        var pending = await _db.ApprovalTasks.AsNoTracking().Where(t => t.RunId == run.Id && t.Status == ApprovalStatus.Pending).ToListAsync(ct);
        var people = pending.SelectMany(t => t.AssigneeList.Concat(t.StandInPairs.Select(p => p.StandIn)).Concat(t.EscalatedList))
            .Append(run.SubmitterEmail)
            .Concat(await _db.RunComments.AsNoTracking().Where(c => c.RunId == run.Id).Select(c => c.AuthorEmail).Distinct().ToListAsync(ct))
            .Where(p => p.Length > 0 && p != email).Distinct().ToList();
        await _notifications.NotifyAsync(people, $"New message: {run.WorkflowName} #{run.Id}",
            $"{name} wrote:\n\n{(body.Length > 600 ? body[..600] + "…" : body)}", $"/Requests/Detail/{run.Id}", ct);
        await _db.SaveChangesAsync(ct);
        return new ActionResult(true, RunId: run.Id);
    }

    /// <summary>Every file on the request: the form's answers and every task's, as the flat list flow steps use.</summary>
    private static List<object?> AllFiles(WorkflowDefinition def, IDictionary<string, object?> answers)
    {
        var files = CollectFiles(def.Form, answers);
        var taskFields = new FormDefinition { Fields = def.Flow.Nodes.Where(n => n.Type == NodeTypes.Task).SelectMany(n => n.Fields ?? new List<FieldDef>()).ToList() };
        files.AddRange(CollectFiles(taskFields, answers));
        return files;
    }

    /// <summary>Every uploaded file among the form's answers, as the flat list flow steps use.</summary>
    private static List<object?> CollectFiles(FormDefinition form, IDictionary<string, object?> values)
    {
        var files = new List<object?>();
        foreach (var field in form.Fields.Where(f => f.Type == FieldTypes.Attachments))
        {
            if (!values.TryGetValue(field.Key, out var raw) || raw is not List<object?> list) continue;
            foreach (var file in list.OfType<Dictionary<string, object?>>())
            {
                var copy = Values.NewDict();
                foreach (var kv in file) copy[kv.Key] = kv.Value;
                copy["field"] = field.Key;
                files.Add(copy);
            }
        }
        return files;
    }

    // ---- follow-up: reminders, escalation, missed ticket events ---------------------------

    /// <summary>
    /// Sends reminders for approvals that have sat unanswered and escalates the ones that have sat too
    /// long. Ages are counted in school days. Returns (reminders sent, steps escalated).
    /// </summary>
    /// <summary>How many days before a due date the person is given a heads-up.</summary>
    public const int DueSoonDays = 3;

    public async Task<(int Reminded, int Escalated)> FollowUpApprovalsAsync(DateTime nowUtc, CancellationToken ct)
    {
        int reminded = 0, escalated = 0;
        // Only items that can still get a reminder, an escalation or a due-date notice are read, and in
        // pages, so items that have had everything they will ever get can never crowd out newer ones.
        for (int lastId = 0, page = 0; page < 40; page++)
        {
            var tasks = await _db.ApprovalTasks.Where(t => t.Status == ApprovalStatus.Pending && t.Id > lastId
                    && ((t.RemindDays > 0 && t.ReminderCount < MaxReminders)
                        || (t.EscalateDays > 0 && t.EscalatedAt == null && t.EscalateTo != "")
                        || (t.DueOn != null && t.OverdueSentAt == null)))
                .OrderBy(t => t.Id).Take(500).ToListAsync(ct);
            if (tasks.Count == 0) break;
            lastId = tasks[^1].Id;
            var (r, e) = await FollowUpPageAsync(tasks, nowUtc, ct);
            reminded += r;
            escalated += e;
        }
        return (reminded, escalated);
    }

    private const int MaxReminders = 5;

    private async Task<(int Reminded, int Escalated)> FollowUpPageAsync(List<ApprovalTask> tasks, DateTime nowUtc, CancellationToken ct)
    {
        var runIds = tasks.Select(t => t.RunId).Distinct().ToList();
        var runs = await _db.Runs.Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        await _defs.FillAsync(_db, runs.Values, ct);
        int reminded = 0, escalated = 0;

        foreach (var task in tasks)
        {
            if (!runs.TryGetValue(task.RunId, out var run) || !RunStatus.IsWaitingOnPeople(run.Status)) continue;
            var link = $"/Requests/Detail/{run.Id}";
            var label = $"{run.WorkflowName} #{run.Id}";
            var waitingDays = await _calendar.SchoolDaysBetweenAsync(Ui.Local(task.CreatedAt), Ui.Local(nowUtc), ct);

            if (task.EscalateDays > 0 && task.EscalatedAt is null && waitingDays >= task.EscalateDays)
            {
                // The requester never becomes an approver of their own request.
                var targets = task.EscalateToList.Where(e => e != run.SubmitterEmail && !task.CanBeDecidedBy(e)).ToList();
                task.EscalatedAt = nowUtc;
                if (targets.Count > 0)
                {
                    task.Escalated = ApprovalTask.Pack(task.EscalatedList.Concat(targets).Distinct());
                    task.RebuildExtra();
                    _db.RunEvents.Add(new RunEventRow
                    {
                        RunId = run.Id, At = nowUtc, NodeId = task.NodeId, Kind = "waiting",
                        Message = $"No decision after {waitingDays} school day{(waitingDays == 1 ? "" : "s")}: escalated to {string.Join(", ", targets)}, who can now decide this step.",
                    });
                    await _notifications.NotifyAsync(targets, $"Escalated to you: {label}",
                        $"“{task.Title}” has had no decision from {string.Join(", ", task.AssigneeList)} for {waitingDays} school day{(waitingDays == 1 ? "" : "s")}, so it has been escalated to you. You can now approve, reject or send it back.", link, ct);
                    run.UpdatedAt = nowUtc;
                    escalated++;
                }
            }

            if (task.RemindDays > 0 && task.ReminderCount < MaxReminders)
            {
                var sinceLast = await _calendar.SchoolDaysBetweenAsync(Ui.Local(task.LastReminderAt ?? task.CreatedAt), Ui.Local(nowUtc), ct);
                if (sinceLast >= task.RemindDays)
                {
                    // Only the people who still owe a decision; under "all must approve" that excludes those who have signed.
                    var owing = task.AssigneeList.Where(a => !task.HasApproved(a))
                        .Concat(task.StandInPairs.Where(p => !task.HasApproved(p.For)).Select(p => p.StandIn))
                        .Concat(task.EscalatedList).Distinct().ToList();
                    task.LastReminderAt = nowUtc;
                    task.ReminderCount++;
                    var isTask = task.Kind == TaskKinds.Task;
                    await _notifications.NotifyAsync(owing, isTask ? $"Reminder: task waiting — {label}" : $"Reminder: approval needed — {label}",
                        $"{(string.IsNullOrEmpty(run.SubmitterName) ? "A request" : run.SubmitterName + "’s request")} has been waiting {waitingDays} school day{(waitingDays == 1 ? "" : "s")} for {(isTask ? "you to complete" : "your decision")}: {task.Title}.", link, ct);
                    reminded++;
                }
            }

            // Due dates are calendar days ("within 3 weeks of the trip"), not school days.
            if (task.DueOn is DateTime dueOn)
            {
                var today = Ui.Local(nowUtc).Date;
                var owing = task.AssigneeList.Concat(task.StandInPairs.Select(p => p.StandIn)).Concat(task.EscalatedList).Distinct().ToList();
                var what = task.Kind == TaskKinds.Task ? "complete" : "decide";
                if (today >= dueOn && task.OverdueSentAt is null)
                {
                    task.OverdueSentAt = nowUtc;
                    task.DueSoonSentAt ??= nowUtc;
                    // The person who owes it, and whoever the step says should know (they are told it is overdue, not asked to act).
                    await _notifications.NotifyAsync(owing, $"Overdue: {task.Title} — {label}",
                        $"This was due on {dueOn:dddd, MMMM d, yyyy} and is still waiting for you to {what} it: {task.Title}.", link, ct);
                    var others = task.DueNotifyList.Except(owing).Where(e => e != "").ToList();
                    if (others.Count > 0)
                        await _notifications.NotifyAsync(others, $"Overdue: {task.Title} — {label}",
                            $"“{task.Title}” was due on {dueOn:dddd, MMMM d, yyyy} and has not been done yet. It is waiting for {string.Join(", ", task.AssigneeList)}.", link, ct);
                    _db.RunEvents.Add(new RunEventRow
                    {
                        RunId = run.Id, At = nowUtc, NodeId = task.NodeId, Kind = "waiting",
                        Message = $"{task.Title}: overdue (was due {dueOn:MMM d, yyyy}). Reminded {string.Join(", ", owing)}" + (others.Count > 0 ? $" and told {string.Join(", ", others)}." : "."),
                    });
                    run.UpdatedAt = nowUtc;
                    reminded++;
                }
                else if (today < dueOn && today >= dueOn.AddDays(-DueSoonDays) && task.DueSoonSentAt is null)
                {
                    task.DueSoonSentAt = nowUtc;
                    await _notifications.NotifyAsync(owing, $"Due {dueOn:MMM d}: {task.Title} — {label}",
                        $"A reminder that this is due on {dueOn:dddd, MMMM d, yyyy}: {task.Title}.", link, ct);
                    reminded++;
                }
            }
        }
        await _db.SaveChangesAsync(ct);
        return (reminded, escalated);
    }

    /// <summary>
    /// The safety net for a missed webhook: asks Freshservice directly about tickets that requests are
    /// waiting on, and moves on any request whose condition is now met. Returns how many moved on.
    /// </summary>
    public async Task<int> CheckWaitingTicketsAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!_freshservice.IsLive) return 0; // simulated tickets never change on their own
        var recently = nowUtc.AddMinutes(-10);
        var waiting = await _db.Runs.Where(r => r.Status == RunStatus.WaitingTicket && r.WaitingTicketId != null && (r.LastPolledAt == null || r.LastPolledAt < recently))
            .OrderBy(r => r.LastPolledAt).ThenBy(r => r.Id).Take(40).ToListAsync(ct);
        await _defs.FillAsync(_db, waiting, ct);
        var moved = 0;
        foreach (var run in waiting)
        {
            Dictionary<string, object?>? ticket;
            try
            {
                ticket = await _freshservice.GetTicketAsync(run.WaitingTicketId!.Value, ct);
            }
            catch (Exception ex) when (ex is FreshserviceException or HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _log.LogWarning("Could not check ticket {TicketId} for request {RunId}: {Message}", run.WaitingTicketId, run.Id, ex.Message);
                break; // Freshservice is unreachable or throttling; try again on the next pass
            }

            var state = await LoadAsync(run, ct);
            if (ticket is null)
            {
                // Deleted in Freshservice: nothing will ever arrive, so say so instead of waiting forever.
                state.Status = RunStatus.Failed;
                state.Error = $"Freshservice ticket #{run.WaitingTicketId} no longer exists, so this request cannot continue. Cancel it, or retry after restoring the ticket.";
                state.NewEvents.Add(new RunEvent(DateTime.Now, run.CurrentNodeId, "error", state.Error));
                if (await TrySaveAsync(run, state, ct)) moved++;
                continue;
            }
            ticket["id"] = (decimal)run.WaitingTicketId!.Value;
            var def = Json.ParseDefinition(run.DefinitionJson);
            var resumed = await (await EngineAsync(ct)).ResumeWithTicketEventAsync(def.Flow, state, ticket, ct);
            if (!resumed)
            {
                // Still waiting: remember that we looked, without disturbing anything else about the run.
                run.LastPolledAt = nowUtc;
                await _db.SaveChangesAsync(ct);
                continue;
            }
            run.LastPolledAt = nowUtc;
            if (await TrySaveAsync(run, state, ct)) moved++;
        }
        return moved;
    }

    // ---- admin actions ---------------------------------------------------------------

    public async Task<ActionResult> RetryAsync(int runId, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        await _defs.FillAsync(_db, run, ct);
        if (run is null) return ActionResult.Fail("Request not found.");
        if (run.Status != RunStatus.Failed) return ActionResult.Fail("Only failed requests can be retried.");
        var def = Json.ParseDefinition(run.DefinitionJson);
        var state = await LoadAsync(run, ct);
        await (await EngineAsync(ct)).RetryAsync(def.Flow, state, ct);
        return await TrySaveAsync(run, state, ct) ? new ActionResult(true, RunId: run.Id) : ActionResult.Fail("The request changed while retrying. Refresh and try again.");
    }

    public async Task<ActionResult> CancelAsync(int runId, string by, CancellationToken ct)
    {
        var run = await _db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        await _defs.FillAsync(_db, run, ct);
        if (run is null) return ActionResult.Fail("Request not found.");
        if (RunStatus.IsFinished(run.Status)) return ActionResult.Fail("This request has already finished.");

        var state = await LoadAsync(run, ct);
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
        await _defs.FillAsync(_db, waiting, ct);
        foreach (var run in waiting)
        {
            var def = Json.ParseDefinition(run.DefinitionJson);
            var state = await LoadAsync(run, ct);
            if (payload.TryGetValue("event", out var ev)) state.Context["event"] = ev;
            var moved = await (await EngineAsync(ct)).ResumeWithTicketEventAsync(def.Flow, state, merged, ct);
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

    /// <summary>Loads a request's state, with its students' details put back from the roster for the steps to use.</summary>
    private async Task<RunState> LoadAsync(WorkflowRun run, CancellationToken ct)
    {
        var state = Load(run);
        await Students.RestoreAsync(Json.ParseDefinition(run.DefinitionJson), state.Context, ct);
        return state;
    }

    private StudentAnswers Students => _students ??= new StudentAnswers(_roster ?? new NoStudentRoster(), _calendar);
    private StudentAnswers? _students;

    /// <summary>What is written to the database: student fields keep only the student number.</summary>
    private static string Stored(WorkflowRun run, Dictionary<string, object?> context)
    {
        var copy = new Dictionary<string, object?>(context, StringComparer.OrdinalIgnoreCase);
        StudentData.Minimize(Json.ParseDefinition(run.DefinitionJson), copy);
        return Json.Serialize(copy);
    }

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

    /// <summary>Stand-ins named by approvers who are away today. Never the requester, and never someone already asked.</summary>
    private async Task<List<(string StandIn, string For)>> StandInsForAsync(IReadOnlyCollection<string> approvers, string submitterEmail, CancellationToken ct)
    {
        var today = DateTime.Now.Date;
        var active = await _db.Delegations.AsNoTracking()
            .Where(d => approvers.Contains(d.FromEmail) && d.StartsOn <= today && d.EndsOn >= today).ToListAsync(ct);
        return active.Where(d => d.ToEmail != submitterEmail && !approvers.Contains(d.ToEmail))
            .GroupBy(d => d.FromEmail).Select(g => (g.First().ToEmail, g.Key)).ToList();
    }

    private async Task SaveAsync(WorkflowRun run, RunState state, CancellationToken ct)
    {
        var statusBefore = run.Status;
        var link = $"/Requests/Detail/{run.Id}";
        var label = $"{run.WorkflowName} #{run.Id}";
        run.Status = state.Status;
        run.CurrentNodeId = state.CurrentNodeId;
        run.ContextJson = Stored(run, state.Context);
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
        if (RunStatus.IsWaitingOnPeople(state.Status) && state.PendingApproval is { } approval)
        {
            var isTask = approval.Kind == TaskKinds.Task;
            if (!pending.Any(t => t.NodeId == approval.NodeId))
            {
                var created = new ApprovalTask
                {
                    RunId = run.Id,
                    NodeId = approval.NodeId,
                    Kind = isTask ? TaskKinds.Task : TaskKinds.Approval,
                    Title = approval.Title.Length > 300 ? approval.Title[..300] : approval.Title,
                    Instructions = approval.Instructions,
                    Assignees = ApprovalTask.Pack(approval.Assignees),
                    Rule = ApprovalRules.Normalize(approval.Rule),
                    Position = approval.Position,
                    RemindDays = approval.RemindDays,
                    EscalateDays = approval.EscalateDays,
                    EscalateTo = approval.EscalateTo.Count == 0 ? "" : ApprovalTask.Pack(approval.EscalateTo),
                    DueOn = approval.DueOn,
                    DueNotify = approval.DueNotify.Count == 0 ? "" : ApprovalTask.Pack(approval.DueNotify),
                    CreatedAt = DateTime.UtcNow,
                };
                // Approvers who are away today have named someone to decide for them.
                var standIns = await StandInsForAsync(approval.Assignees, run.SubmitterEmail, ct);
                if (standIns.Count > 0)
                {
                    created.StandIns = ";" + string.Join(";", standIns.Select(p => $"{p.StandIn}={p.For}")) + ";";
                    created.RebuildExtra();
                    foreach (var (standIn, original) in standIns)
                        _db.RunEvents.Add(new RunEventRow { RunId = run.Id, At = at = at.AddTicks(1), NodeId = approval.NodeId, Kind = "info", Message = $"{original} is away, so {standIn} can decide in their place." });
                }
                _db.ApprovalTasks.Add(created);
                var standInEmails = standIns.Select(p => p.StandIn).ToList();
                var from = string.IsNullOrEmpty(run.SubmitterName) ? "A request" : run.SubmitterName + "’s request";
                await _notifications.NotifyAsync(approval.Assignees.Concat(standInEmails), isTask ? $"Task for you: {label}" : $"Approval needed: {label}",
                    $"{from} is waiting for {(isTask ? "you to complete" : "your decision")}: {approval.Title}{(approval.Position.Length > 0 ? $" ({approval.Position.ToLowerInvariant()})" : "")}." +
                    (approval.Instructions.Length > 0 ? "\n\n" + approval.Instructions : ""), link, ct);
            }
        }
        else if (!RunStatus.IsWaitingOnPeople(state.Status))
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
