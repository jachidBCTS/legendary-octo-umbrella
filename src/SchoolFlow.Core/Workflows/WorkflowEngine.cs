using System.Net;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Freshservice;

namespace SchoolFlow.Core.Workflows;

public static class RunStatus
{
    public const string Running = "Running", WaitingApproval = "WaitingApproval", WaitingTicket = "WaitingTicket",
        Completed = "Completed", Rejected = "Rejected", Failed = "Failed", Cancelled = "Cancelled",
        /// <summary>An approver sent the request back; it waits for the requester to edit and resubmit.</summary>
        ChangesRequested = "ChangesRequested";

    public static bool IsFinished(string status) => status is Completed or Rejected or Cancelled;
    /// <summary>Waiting for someone to complete a Task step (fill in details, upload documents).</summary>
    public const string WaitingTask = "WaitingTask";
    public static bool IsWaiting(string status) => status is WaitingApproval or WaitingTask or WaitingTicket;
    /// <summary>Waiting on a person: an approval or a task.</summary>
    public static bool IsWaitingOnPeople(string status) => status is WaitingApproval or WaitingTask;
}

public sealed record RunEvent(DateTime At, string? NodeId, string Kind, string Message);

public sealed class PendingApproval
{
    public string NodeId { get; set; } = "";
    /// <summary>"approval" (approve / reject) or "task" (fill in the step's fields).</summary>
    public string Kind { get; set; } = "approval";
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<string> Assignees { get; set; } = new();
    /// <summary>"any" (one approver decides) or "all" (every approver must approve).</summary>
    public string Rule { get; set; } = ApprovalRules.Any;
    /// <summary>For chains: "Step 2 of 3".</summary>
    public string Position { get; set; } = "";
    /// <summary>Remind the approvers every this many school days; 0 for never.</summary>
    public int RemindDays { get; set; }
    /// <summary>After this many school days unanswered, also let <see cref="EscalateTo"/> decide; 0 for never.</summary>
    public int EscalateDays { get; set; }
    public List<string> EscalateTo { get; set; } = new();
    /// <summary>The day this should be done by (calendar date), if the step sets one.</summary>
    public DateTime? DueOn { get; set; }
    /// <summary>Who else is told if it is not done by <see cref="DueOn"/>.</summary>
    public List<string> DueNotify { get; set; } = new();
}

/// <summary>A message the engine wants delivered (in the app and, where possible, by email).</summary>
public sealed record OutgoingNotification(string Email, string Title, string Body);

public sealed class ApprovalDecision
{
    public bool Approved { get; set; }
    public string By { get; set; } = "";
    public string? Comment { get; set; }
}

/// <summary>In-memory state of one workflow run. The web layer persists it between steps.</summary>
public sealed class RunState
{
    public string Status { get; set; } = RunStatus.Running;
    public string? CurrentNodeId { get; set; }
    public Dictionary<string, object?> Context { get; set; } = Values.NewDict();
    public List<RunEvent> NewEvents { get; } = new();
    public List<OutgoingNotification> NewNotifications { get; } = new();
    public PendingApproval? PendingApproval { get; set; }
    public long? WaitingTicketId { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Executes a flow graph node by node until it finishes, fails, or has to wait for a person
/// (approval) or for Freshservice (ticket event).
/// </summary>
public sealed class WorkflowEngine
{
    public const int MaxStepsPerAdvance = 200;

    private readonly IFreshserviceClient _freshservice;
    private readonly IApprovalDirectory? _directory;
    private readonly IAttachmentStore? _files;
    private readonly Func<DateTime> _now;

    /// <summary>Freshservice's documented ceiling for all attachments on one ticket.</summary>
    public const long MaxTicketAttachmentBytes = 40L * 1024 * 1024;

    private readonly IReadOnlySet<DateTime>? _closedDays;
    public const int DefaultRemindDays = 2;

    public WorkflowEngine(IFreshserviceClient freshservice, Func<DateTime>? now = null, IApprovalDirectory? directory = null,
        IAttachmentStore? files = null, IReadOnlySet<DateTime>? closedDays = null)
    {
        _closedDays = closedDays;
        _freshservice = freshservice;
        _directory = directory;
        _files = files;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Teams by name (case-insensitive) → member emails. Steps name a team as [Team name].</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Teams { get; init; }

    private static readonly System.Text.RegularExpressions.Regex TeamReference = new(@"\[([^\[\]]{1,200})\]");

    /// <summary>
    /// The people a step's setting names: email addresses, and the members of any [Team name] in it.
    /// A team that does not exist, or has nobody in it, stops the step with a message an admin can act on.
    /// </summary>
    public List<string> ResolvePeople(string text, string step)
    {
        var people = new List<string>();
        var rest = TeamReference.Replace(text ?? "", match =>
        {
            var name = match.Groups[1].Value.Trim();
            if (Teams is null || !Teams.TryGetValue(name, out var members))
                throw new ExpressionException($"{step}: there is no team called “{name}”. An admin can add it under Admin → Teams.");
            if (members.Count == 0)
                throw new ExpressionException($"{step}: the team “{name}” has nobody in it yet. An admin can add people under Admin → Teams.");
            people.AddRange(members.Select(m => m.Trim().ToLowerInvariant()));
            return " ";
        });
        people.AddRange(ParseEmails(rest));
        return people.Where(p => p.Contains('@')).Distinct().ToList();
    }

    /// <summary>Resumes a run whose Task step has been completed: its answers join the request.</summary>
    public Task ResumeWithTaskAsync(FlowDefinition flow, RunState state, string by, IDictionary<string, object?> answers, CancellationToken ct = default)
    {
        if (state.Status != RunStatus.WaitingTask)
            throw new InvalidOperationException("This request is not waiting for a task.");
        var node = FindNode(flow, state.CurrentNodeId);
        if (node is null || node.Type != NodeTypes.Task)
        {
            Fail(state, state.CurrentNodeId, "The task step no longer exists in this workflow version.");
            return Task.CompletedTask;
        }
        // Only the step's own fields are taken: a completed task can never overwrite another answer.
        foreach (var field in (node.Fields ?? new List<FieldDef>()).Where(f => !FieldTypes.IsLayout(f.Type) && !string.IsNullOrWhiteSpace(f.Key)))
            state.Context[field.Key] = answers.TryGetValue(field.Key, out var value) ? value : null;
        var record = Values.NewDict();
        record["step"] = node.Label ?? "";
        record["by"] = by;
        record["at"] = _now();
        state.Context["task"] = record;
        Log(state, node.Id, "approved", $"{Name(node)}: completed by {by}");
        state.PendingApproval = null;
        state.Status = RunStatus.Running;
        Follow(flow, state, node, "next");
        return AdvanceAsync(flow, state, ct);
    }

    public Task StartAsync(FlowDefinition flow, RunState state, CancellationToken ct = default)
    {
        var start = flow.Nodes.FirstOrDefault(n => n.Type == NodeTypes.Start);
        if (start is null)
        {
            Fail(state, null, "This workflow has no Start step.");
            return Task.CompletedTask;
        }
        state.Status = RunStatus.Running;
        state.CurrentNodeId = start.Id;
        return AdvanceAsync(flow, state, ct);
    }

    /// <summary>Resumes a run that is waiting on an approval.</summary>
    public Task ResumeWithDecisionAsync(FlowDefinition flow, RunState state, ApprovalDecision decision, CancellationToken ct = default)
    {
        if (state.Status != RunStatus.WaitingApproval)
            throw new InvalidOperationException("This request is not waiting for an approval.");
        var node = FindNode(flow, state.CurrentNodeId);
        if (node is null)
        {
            Fail(state, state.CurrentNodeId, "The approval step no longer exists in this workflow version.");
            return Task.CompletedTask;
        }

        var record = Values.NewDict();
        record["decision"] = decision.Approved ? "approved" : "rejected";
        record["approved"] = decision.Approved;
        record["by"] = decision.By;
        record["comment"] = decision.Comment ?? "";
        record["step"] = node.Label ?? "";
        record["at"] = _now();
        state.Context["approval"] = record;

        var history = state.Context.TryGetValue("approvals", out var existing) && existing is List<object?> list ? list : new List<object?>();
        history.Add(record);
        state.Context["approvals"] = history;

        var comment = string.IsNullOrWhiteSpace(decision.Comment) ? "" : $" — “{decision.Comment.Trim()}”";
        Log(state, node.Id, decision.Approved ? "approved" : "rejected",
            $"{(decision.Approved ? "Approved" : "Rejected")} by {decision.By}{comment}");

        state.PendingApproval = null;

        if (node.Type == NodeTypes.ApprovalChain)
        {
            // Move to the next step of the chain, or leave the chain when it is finished or rejected.
            if (state.Context.GetValueOrDefault("chain") is not Dictionary<string, object?> chain
                || chain.GetValueOrDefault("steps") is not List<object?> steps
                || !Values.TryToNumber(chain.GetValueOrDefault("index"), out var position)
                || position < 0 || position >= steps.Count)
            {
                Fail(state, node.Id, "The approval chain's progress could not be read.");
                return Task.CompletedTask;
            }
            var index = (int)position;
            if (steps[index] is Dictionary<string, object?> current)
            {
                current["status"] = decision.Approved ? "approved" : "rejected";
                current["by"] = decision.By;
            }
            if (decision.Approved && index + 1 < steps.Count)
            {
                chain["index"] = (decimal)(index + 1);
                try
                {
                    EnterChainStep(state, node, chain);
                }
                catch (ExpressionException ex)
                {
                    Fail(state, node.Id, ex.Message);
                }
                return Task.CompletedTask;
            }
            chain["finished"] = true;
        }

        state.Status = RunStatus.Running;
        Follow(flow, state, node, decision.Approved ? "approved" : "rejected");
        return AdvanceAsync(flow, state, ct);
    }

    private void EnterChainStep(RunState state, NodeDef node, Dictionary<string, object?> chain)
    {
        var steps = (List<object?>)chain["steps"]!;
        var index = (int)Values.ToNumber(chain["index"]);
        var step = ChainValue.ToSteps(chain)[index];
        if (steps[index] is Dictionary<string, object?> record) record["status"] = "waiting";
        state.PendingApproval = new PendingApproval
        {
            NodeId = node.Id,
            Title = step.Name,
            Instructions = Template.Render(node.Get("instructions"), NewScope(state)),
            Assignees = step.Approvers,
            Rule = step.Rule,
            Position = $"Step {index + 1} of {steps.Count}",
        };
        ApplyFollowUp(node, state.PendingApproval, state);
        state.Status = RunStatus.WaitingApproval;
        var who = string.Join(step.Rule == ApprovalRules.All && step.Approvers.Count > 1 ? " and " : " or ", step.Approvers);
        Log(state, node.Id, "waiting", $"{step.Name} ({state.PendingApproval.Position.ToLowerInvariant()}): waiting for {who}");
    }

    /// <summary>Works out the full list of steps: the chosen chain, then the department's final approver(s).</summary>
    private async Task<List<ChainStep>> BuildChainAsync(NodeDef node, RunState state, CancellationToken ct)
    {
        var steps = new List<ChainStep>();
        var submitter = state.Context.GetValueOrDefault("submitter") is Dictionary<string, object?> person
            ? Values.ToText(person.GetValueOrDefault("email")).Trim().ToLowerInvariant() : "";

        switch (node.Get("chainSource"))
        {
            case "template":
            {
                if (!long.TryParse(node.Get("chainId"), out var chainId))
                    throw new ExpressionException($"{Name(node)}: choose which approval chain template to use.");
                var template = _directory is null ? null : await _directory.GetChainAsync(chainId, ct);
                steps.AddRange(template ?? throw new ExpressionException($"{Name(node)}: the approval chain template it uses no longer exists."));
                break;
            }
            case "field":
            {
                var key = node.Get("chainField").Trim();
                if (key.Length == 0) throw new ExpressionException($"{Name(node)}: choose which form field holds the approval chain.");
                steps.AddRange(ChainValue.ToSteps(new Scope(state.Context).TryGet(key, out var value) ? value : null));
                break;
            }
        }

        long? departmentId = null;
        switch (node.Get("departmentSource"))
        {
            case "fixed":
                departmentId = long.TryParse(node.Get("departmentId"), out var fixedId) ? fixedId
                    : throw new ExpressionException($"{Name(node)}: choose the department.");
                break;
            case "field":
            {
                var key = node.Get("departmentField").Trim();
                var value = key.Length > 0 && new Scope(state.Context).TryGet(key, out var found) ? found : null;
                if (value is Dictionary<string, object?> record && Values.TryToNumber(record.GetValueOrDefault("id"), out var id) && id > 0)
                    departmentId = (long)id;
                else
                    throw new ExpressionException($"{Name(node)}: no department was chosen on the form, so the final approver cannot be worked out.");
                break;
            }
        }
        if (departmentId is long deptId)
        {
            var department = (_directory is null ? null : await _directory.GetDepartmentAsync(deptId, ct))
                             ?? throw new ExpressionException($"{Name(node)}: the department no longer exists.");
            if (department.FinalApprovers.Count == 0)
                throw new ExpressionException($"{Name(node)}: {department.Name} has no final approver set. An admin needs to add one under Admin → Departments.");
            steps.Add(new ChainStep
            {
                Name = $"Final approval — {department.Name}",
                Approvers = department.FinalApprovers.Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList(),
                Rule = ApprovalRules.Normalize(department.FinalRule),
            });
        }

        // Nobody approves their own request where someone else can; a step left with only the
        // submitter is kept (an admin put them there) rather than silently skipped.
        foreach (var step in steps)
        {
            step.Approvers = step.Approvers.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Contains('@')).Distinct().ToList();
            if (submitter.Length > 0 && step.Approvers.Count > 1) step.Approvers.Remove(submitter);
        }
        if (steps.Any(s => s.Approvers.Count == 0))
            throw new ExpressionException($"{Name(node)}: the step “{steps.First(s => s.Approvers.Count == 0).Name}” has no approvers.");
        if (steps.Count == 0)
            throw new ExpressionException($"{Name(node)}: there is nobody to approve this — no approval chain was given and no department is set.");
        if (steps.Count > ChainValue.MaxSteps + 1)
            throw new ExpressionException($"{Name(node)}: the approval chain is too long.");
        return steps;
    }

    /// <summary>
    /// Offers a Freshservice ticket event to a run waiting on that ticket. Returns true when the run moved on.
    /// </summary>
    public async Task<bool> ResumeWithTicketEventAsync(FlowDefinition flow, RunState state,
        Dictionary<string, object?> ticket, CancellationToken ct = default)
    {
        if (state.Status != RunStatus.WaitingTicket) return false;
        var node = FindNode(flow, state.CurrentNodeId);
        if (node is null)
        {
            Fail(state, state.CurrentNodeId, "The wait step no longer exists in this workflow version.");
            return true;
        }

        MergeTicket(state, ticket);
        var until = node.Get("until");
        if (!string.IsNullOrWhiteSpace(until))
        {
            try
            {
                if (!Values.ToBool(Formula.Evaluate(until, NewScope(state)))) return false;
            }
            catch (ExpressionException ex)
            {
                Fail(state, node.Id, $"“Continue when” formula: {ex.Message}");
                return true;
            }
        }

        Log(state, node.Id, "info", "Ticket update received — continuing.");
        state.WaitingTicketId = null;
        state.Status = RunStatus.Running;
        Follow(flow, state, node, "next");
        await AdvanceAsync(flow, state, ct);
        return true;
    }

    /// <summary>Retries a failed run from the step that failed.</summary>
    public Task RetryAsync(FlowDefinition flow, RunState state, CancellationToken ct = default)
    {
        if (state.Status != RunStatus.Failed) throw new InvalidOperationException("Only failed requests can be retried.");
        if (FindNode(flow, state.CurrentNodeId) is null)
        {
            Fail(state, state.CurrentNodeId, "The failed step no longer exists.");
            return Task.CompletedTask;
        }
        state.Status = RunStatus.Running;
        state.Error = null;
        Log(state, state.CurrentNodeId, "info", "Retrying.");
        return AdvanceAsync(flow, state, ct);
    }

    private async Task AdvanceAsync(FlowDefinition flow, RunState state, CancellationToken ct)
    {
        for (var steps = 0; state.Status == RunStatus.Running; steps++)
        {
            if (steps >= MaxStepsPerAdvance)
            {
                Fail(state, state.CurrentNodeId, $"Stopped after {MaxStepsPerAdvance} steps — the flow appears to loop forever.");
                return;
            }
            var node = FindNode(flow, state.CurrentNodeId);
            if (node is null)
            {
                Fail(state, state.CurrentNodeId, "The flow points to a step that does not exist.");
                return;
            }

            try
            {
                await ExecuteAsync(flow, state, node, ct);
            }
            catch (ExpressionException ex)
            {
                Fail(state, node.Id, ex.Message);
            }
            catch (FreshserviceException ex)
            {
                Fail(state, node.Id, $"Freshservice: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                Fail(state, node.Id, $"Could not reach Freshservice: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Fail(state, node.Id, "Freshservice did not respond in time.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(state, node.Id, $"Unexpected error in this step: {ex.Message}");
            }
        }
    }

    private async Task ExecuteAsync(FlowDefinition flow, RunState state, NodeDef node, CancellationToken ct)
    {
        var scope = NewScope(state);
        switch (node.Type)
        {
            case NodeTypes.Start:
                Follow(flow, state, node, "next");
                break;

            case NodeTypes.Condition:
            {
                var source = node.Get("expression");
                if (string.IsNullOrWhiteSpace(source)) throw new ExpressionException("This decision has no condition.");
                var outcome = Values.ToBool(Formula.Evaluate(source, scope));
                Log(state, node.Id, "info", $"{Name(node)}{(Name(node).TrimEnd().EndsWith('?') ? "" : ":")} {(outcome ? "Yes" : "No")}");
                Follow(flow, state, node, outcome ? "true" : "false");
                break;
            }

            case NodeTypes.SetVariable:
            {
                var name = node.Get("name").Trim();
                if (!DefinitionValidator.IsValidKey(name)) throw new ExpressionException("Set value: the name must be letters, digits and underscores.");
                if (DefinitionValidator.Reserved.Contains(name)) throw new ExpressionException($"Set value: '{name}' is a reserved name.");
                var value = Formula.Evaluate(node.Get("expression"), scope);
                state.Context[name] = value;
                Log(state, node.Id, "info", $"Set {name} = {Values.Describe(value)}");
                Follow(flow, state, node, "next");
                break;
            }

            case NodeTypes.Approval:
            {
                var assignees = ResolvePeople(Template.Render(node.Get("approvers"), scope), Name(node));
                if (assignees.Count == 0)
                    throw new ExpressionException($"{Name(node)}: no approver email could be worked out. Check the Approvers setting.");
                var rule = ApprovalRules.Normalize(node.Get("rule"));
                state.PendingApproval = new PendingApproval
                {
                    NodeId = node.Id,
                    Title = Name(node),
                    Instructions = Template.Render(node.Get("instructions"), scope),
                    Assignees = assignees,
                    Rule = rule,
                };
                ApplyFollowUp(node, state.PendingApproval, state);
                state.Status = RunStatus.WaitingApproval;
                Log(state, node.Id, "waiting", rule == ApprovalRules.All && assignees.Count > 1
                    ? $"Waiting for approval from all of: {string.Join(", ", assignees)}"
                    : $"Waiting for approval from {string.Join(", ", assignees)}");
                break;
            }

            case NodeTypes.Task:
            {
                var assignees = ResolvePeople(Template.Render(node.Get("assignees"), scope), Name(node));
                if (assignees.Count == 0)
                    throw new ExpressionException($"{Name(node)}: nobody to give this task to. Check the “Who does this” setting.");
                state.PendingApproval = new PendingApproval
                {
                    NodeId = node.Id,
                    Kind = "task",
                    Title = Name(node),
                    Instructions = Template.Render(node.Get("instructions"), scope),
                    Assignees = assignees,
                    Rule = ApprovalRules.Any,
                };
                ApplyFollowUp(node, state.PendingApproval, state);
                state.Status = RunStatus.WaitingTask;
                Log(state, node.Id, "waiting", $"{Name(node)}: waiting for {string.Join(", ", assignees)}");
                break;
            }

            case NodeTypes.ApprovalChain:
            {
                var steps = await BuildChainAsync(node, state, ct);
                var chain = Values.NewDict();
                chain["node"] = node.Id;
                chain["index"] = 0m;
                chain["steps"] = steps.Select(s =>
                {
                    var record = Values.NewDict();
                    record["name"] = s.Name;
                    record["approvers"] = s.Approvers.Cast<object?>().ToList();
                    record["rule"] = s.Rule;
                    record["status"] = "pending";
                    return (object?)record;
                }).ToList();
                state.Context["chain"] = chain;
                EnterChainStep(state, node, chain);
                break;
            }

            case NodeTypes.Notify:
            {
                var recipients = ResolvePeople(Template.Render(node.Get("to"), scope), Name(node));
                var title = Template.Render(node.Get("subject"), scope).Trim();
                var message = Template.Render(node.Get("message"), scope).Trim();
                if (title.Length == 0) throw new ExpressionException($"{Name(node)}: the notification has no subject.");
                if (title.Length > 200) title = title[..200];
                // A notification should never stop a request, so missing recipients are noted, not fatal.
                if (recipients.Count == 0)
                    Log(state, node.Id, "info", $"{Name(node)}: nobody to notify (the recipient list was empty).");
                else
                {
                    foreach (var email in recipients.Take(50)) state.NewNotifications.Add(new OutgoingNotification(email, title, message));
                    Log(state, node.Id, "info", $"Notified {string.Join(", ", recipients.Take(50))}: {title}");
                }
                Follow(flow, state, node, "next");
                break;
            }

            case NodeTypes.CreateTicket:
            {
                var request = new TicketCreate
                {
                    Subject = Template.Render(node.Get("subject"), scope).Trim(),
                    DescriptionHtml = RenderHtml(node.Get("description"), scope),
                    RequesterEmail = Template.Render(Default(node.Get("requesterEmail"), "{{submitter.email}}"), scope).Trim(),
                    Priority = ResolveInt(node, "priority", "priorityFormula", scope, 2, 1, 4, "Priority"),
                    Status = ResolveInt(node, "status", null, scope, 2, 2, 999, "Status"),   // above 5 = a custom Freshservice status
                    Type = NullIfBlank(node.Get("type")),
                    GroupId = ResolveId(node.Get("groupId"), scope, "Group"),
                    DepartmentId = ResolveId(node.Get("departmentId"), scope, "Department"),
                    Category = NullIfBlank(Template.Render(node.Get("category"), scope)),
                    SubCategory = NullIfBlank(Template.Render(node.Get("subCategory"), scope)),
                    Tags = Template.Render(node.Get("tags"), scope).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    CustomFields = ResolveCustomFields(node, scope),
                };
                if (request.Subject.Length == 0) throw new ExpressionException($"{Name(node)}: the ticket subject is empty.");
                if (request.RequesterEmail.Length == 0) throw new ExpressionException($"{Name(node)}: the requester email is empty.");
                if (request.DescriptionHtml.Length == 0) request.DescriptionHtml = WebUtility.HtmlEncode(request.Subject);

                var ticket = await _freshservice.CreateTicketAsync(request, ct);
                state.Context["ticket"] = ticket;
                Log(state, node.Id, "ticket", $"{Sim()}Created Freshservice ticket #{Values.ToText(ticket.GetValueOrDefault("id"))}: {request.Subject}");
                if (node.Get("attachFiles") == "true" && Values.TryToNumber(ticket.GetValueOrDefault("id"), out var newTicketId))
                    await AttachRequestFilesAsync(state, node, (long)newTicketId, ct);
                Follow(flow, state, node, "next");
                break;
            }

            case NodeTypes.ServiceRequest:
            {
                var itemId = ResolveId(node.Get("itemId"), scope, "Catalog item ID")
                             ?? throw new ExpressionException($"{Name(node)}: the catalog item ID is empty.");
                var email = Template.Render(Default(node.Get("requesterEmail"), "{{submitter.email}}"), scope).Trim();
                if (email.Length == 0) throw new ExpressionException($"{Name(node)}: the requester email is empty.");
                var quantity = (int)Math.Clamp(Values.ToNumber(Template.RenderValue(Default(node.Get("quantity"), "1"), scope)), 1, 1000);
                var ticket = await _freshservice.PlaceServiceRequestAsync(itemId, email, quantity, ResolveCustomFields(node, scope), ct);
                state.Context["ticket"] = ticket;
                Log(state, node.Id, "ticket", $"{Sim()}Placed service request #{Values.ToText(ticket.GetValueOrDefault("id"))} for catalog item {itemId}");
                Follow(flow, state, node, "next");
                break;
            }

            case NodeTypes.UpdateTicket:
            {
                var ticketId = ResolveTicketId(node, scope);
                var update = new TicketUpdate
                {
                    Priority = string.IsNullOrWhiteSpace(node.Get("priority")) && string.IsNullOrWhiteSpace(node.Get("priorityFormula"))
                        ? null : ResolveInt(node, "priority", "priorityFormula", scope, 2, 1, 4, "Priority"),
                    Status = string.IsNullOrWhiteSpace(node.Get("status")) ? null : ResolveInt(node, "status", null, scope, 2, 2, 999, "Status"),
                    GroupId = ResolveId(node.Get("groupId"), scope, "Group"),
                    ResponderId = ResolveId(node.Get("responderId"), scope, "Agent"),
                    CustomFields = ResolveCustomFields(node, scope),
                };
                if (update.IsEmpty) throw new ExpressionException($"{Name(node)}: nothing to update — choose at least one field to change.");
                var ticket = await _freshservice.UpdateTicketAsync(ticketId, update, ct);
                MergeTicket(state, ticket);
                Log(state, node.Id, "ticket", $"{Sim()}Updated Freshservice ticket #{ticketId}");
                Follow(flow, state, node, "next");
                break;
            }

            case NodeTypes.AddNote:
            {
                var ticketId = ResolveTicketId(node, scope);
                var body = RenderHtml(node.Get("body"), scope);
                if (body.Length == 0) throw new ExpressionException($"{Name(node)}: the note is empty.");
                var isPrivate = !string.Equals(node.Get("visibility"), "public", StringComparison.OrdinalIgnoreCase);
                await _freshservice.AddNoteAsync(ticketId, body, isPrivate, ct);
                Log(state, node.Id, "ticket", $"{Sim()}Added a {(isPrivate ? "private" : "public")} note to ticket #{ticketId}");
                Follow(flow, state, node, "next");
                break;
            }

            case NodeTypes.Lookup:
            {
                var saveAs = node.Get("saveAs").Trim();
                if (!DefinitionValidator.IsValidKey(saveAs)) throw new ExpressionException($"{Name(node)}: “Save as” must be letters, digits and underscores.");
                if (DefinitionValidator.Reserved.Contains(saveAs) && !saveAs.Equals("ticket", StringComparison.OrdinalIgnoreCase))
                    throw new ExpressionException($"{Name(node)}: '{saveAs}' is a reserved name.");
                Dictionary<string, object?>? found;
                if (node.Get("kind") == "ticket")
                {
                    var id = ResolveId(Default(node.Get("value"), "{{ticket.id}}"), scope, "Ticket ID")
                             ?? throw new ExpressionException($"{Name(node)}: no ticket ID to look up.");
                    found = await _freshservice.GetTicketAsync(id, ct);
                }
                else
                {
                    var email = Template.Render(Default(node.Get("value"), "{{submitter.email}}"), scope).Trim();
                    found = email.Length == 0 ? null : await _freshservice.FindRequesterAsync(email, ct);
                }
                state.Context[saveAs] = found;
                Log(state, node.Id, "info", found is null
                    ? $"{Sim()}{Name(node)}: nothing found — {saveAs} is blank"
                    : $"{Sim()}{Name(node)}: saved as {saveAs}");
                Follow(flow, state, node, "next");
                break;
            }

            case NodeTypes.WaitTicket:
            {
                var ticketId = ResolveTicketId(node, scope);
                state.WaitingTicketId = ticketId;
                state.Status = RunStatus.WaitingTicket;
                Log(state, node.Id, "waiting", $"Waiting for an update to Freshservice ticket #{ticketId}");
                break;
            }

            case NodeTypes.End:
            {
                var rejected = string.Equals(node.Get("outcome"), "rejected", StringComparison.OrdinalIgnoreCase);
                Finish(state, node.Id, rejected ? RunStatus.Rejected : RunStatus.Completed,
                    string.IsNullOrWhiteSpace(node.Label) ? (rejected ? "Request rejected." : "Completed.") : node.Label!);
                break;
            }

            default:
                throw new ExpressionException($"Unknown step type '{node.Type}'.");
        }
    }

    /// <summary>
    /// Sends the request's uploaded files to the ticket. The ticket already exists, so a problem here is
    /// recorded on the timeline and the request carries on: the files remain available in SchoolFlow.
    /// </summary>
    private async Task AttachRequestFilesAsync(RunState state, NodeDef node, long ticketId, CancellationToken ct)
    {
        if (state.Context.GetValueOrDefault("attachments") is not List<object?> listed || listed.Count == 0) return;
        var files = new List<TicketFile>();
        var skipped = new List<string>();
        long total = 0;
        foreach (var item in listed.OfType<Dictionary<string, object?>>())
        {
            var id = Values.ToText(item.GetValueOrDefault("id"));
            var name = Values.ToText(item.GetValueOrDefault("name"));
            var file = _files is null ? null : await _files.ReadAsync(id, ct);
            if (file is null) { skipped.Add($"{name} (file missing)"); continue; }
            if (total + file.Content.LongLength > MaxTicketAttachmentBytes) { skipped.Add($"{name} (over Freshservice's 40 MB per-ticket limit)"); continue; }
            total += file.Content.LongLength;
            files.Add(file);
        }
        try
        {
            if (files.Count > 0)
            {
                await _freshservice.AttachFilesAsync(ticketId, files, ct);
                Log(state, node.Id, "ticket", $"{Sim()}Attached {files.Count} file{(files.Count == 1 ? "" : "s")} to ticket #{ticketId}: {string.Join(", ", files.Select(f => f.Name))}");
            }
            if (skipped.Count > 0)
                Log(state, node.Id, "warning", $"Not attached to ticket #{ticketId}: {string.Join(", ", skipped)}. Anything still on file is available on this request.");
        }
        catch (Exception ex) when (ex is FreshserviceException or HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            Log(state, node.Id, "warning",
                $"Ticket #{ticketId} was created, but the files could not be attached to it ({(ex is FreshserviceException ? ex.Message : "Freshservice could not be reached")}). They are still available on this request.");
        }
    }

    // ---- helpers ---------------------------------------------------------------------

    private Scope NewScope(RunState state) => new(state.Context, now: _now, closedDays: _closedDays);

    /// <summary>Reads a step's reminder and escalation settings onto the approval it is about to ask for.</summary>
    private void ApplyFollowUp(NodeDef node, PendingApproval approval, RunState state)
    {
        static int Days(string text, int whenBlank) =>
            string.IsNullOrWhiteSpace(text) ? whenBlank : int.TryParse(text.Trim(), out var n) ? Math.Clamp(n, 0, 60) : whenBlank;
        approval.RemindDays = Days(node.Get("remindDays"), DefaultRemindDays);
        approval.EscalateDays = Days(node.Get("escalateDays"), 0);
        // People already asked are left out: escalating to someone who is ignoring it helps nobody.
        approval.EscalateTo = ResolvePeople(Template.Render(node.Get("escalateTo"), NewScope(state)), Name(node)).Except(approval.Assignees).ToList();
        if (approval.EscalateTo.Count == 0) approval.EscalateDays = 0;

        // A due date is a reminder, not a rule: a formula that cannot be worked out is noted and skipped.
        var dueFormula = node.Get("dueDate").Trim();
        if (dueFormula.Length > 0)
        {
            try
            {
                var due = Formula.Evaluate(dueFormula, NewScope(state));
                if (Values.IsBlank(due)) Log(state, node.Id, "info", $"{Name(node)}: no due date (the due-date formula gave nothing).");
                else if (Values.TryToDate(due, out var date)) approval.DueOn = date.Date;
                else Log(state, node.Id, "warning", $"{Name(node)}: the due date could not be worked out ('{Values.Describe(due)}' is not a date), so none was set.");
            }
            catch (ExpressionException ex)
            {
                Log(state, node.Id, "warning", $"{Name(node)}: the due date could not be worked out ({ex.Message}), so none was set.");
            }
        }
        if (approval.DueOn is not null)
            approval.DueNotify = ResolvePeople(Template.Render(node.Get("dueNotify"), NewScope(state)), Name(node)).Except(approval.Assignees).ToList();
    }

    /// <summary>
    /// Puts a request that was sent back for changes in front of the same approval step again, now that
    /// the requester has resubmitted it.
    /// </summary>
    public Task ReenterApprovalAsync(FlowDefinition flow, RunState state, CancellationToken ct = default)
    {
        if (state.Status != RunStatus.ChangesRequested) throw new InvalidOperationException("This request is not waiting for changes.");
        var node = FindNode(flow, state.CurrentNodeId);
        if (node is null)
        {
            Fail(state, state.CurrentNodeId, "The approval step no longer exists in this workflow version.");
            return Task.CompletedTask;
        }
        if (node.Type == NodeTypes.ApprovalChain && state.Context.GetValueOrDefault("chain") is Dictionary<string, object?> chain
            && chain.GetValueOrDefault("steps") is List<object?>)
        {
            // Keep the chain and its earlier approvals; go back to the step that asked for changes.
            try { EnterChainStep(state, node, chain); }
            catch (ExpressionException ex) { Fail(state, node.Id, ex.Message); }
            return Task.CompletedTask;
        }
        state.Status = RunStatus.Running; // a single approval step is simply asked again
        return AdvanceAsync(flow, state, ct);
    }

    private string Sim() => _freshservice.IsLive ? "" : "[Simulated] ";

    private static string Name(NodeDef node) => string.IsNullOrWhiteSpace(node.Label) ? node.Type : node.Label!;

    private static NodeDef? FindNode(FlowDefinition flow, string? id) =>
        id is null ? null : flow.Nodes.FirstOrDefault(n => n.Id == id);

    private void Follow(FlowDefinition flow, RunState state, NodeDef node, string port)
    {
        var edge = flow.Edges.FirstOrDefault(e => e.From == node.Id && e.Port == port);
        if (edge is null)
        {
            // A dangling branch simply ends the run; a dangling "rejected" branch ends it as rejected.
            if (port == "rejected") Finish(state, node.Id, RunStatus.Rejected, "Request rejected.");
            else Finish(state, node.Id, RunStatus.Completed, "Completed.");
            return;
        }
        state.CurrentNodeId = edge.To;
    }

    private void Finish(RunState state, string? nodeId, string status, string message)
    {
        state.Status = status;
        state.PendingApproval = null;
        state.WaitingTicketId = null;
        Log(state, nodeId, status == RunStatus.Rejected ? "rejected" : "completed", message);
    }

    private void Fail(RunState state, string? nodeId, string message)
    {
        state.Status = RunStatus.Failed;
        state.Error = message;
        Log(state, nodeId, "error", message);
    }

    private void Log(RunState state, string? nodeId, string kind, string message) =>
        state.NewEvents.Add(new RunEvent(_now(), nodeId, kind, message.Length > 2000 ? message[..2000] : message));

    private static void MergeTicket(RunState state, Dictionary<string, object?> incoming)
    {
        if (state.Context.TryGetValue("ticket", out var existing) && existing is Dictionary<string, object?> current)
        {
            foreach (var kv in incoming) current[kv.Key] = kv.Value;
        }
        else
        {
            var copy = Values.NewDict();
            foreach (var kv in incoming) copy[kv.Key] = kv.Value;
            state.Context["ticket"] = copy;
        }
    }

    private static string Default(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Renders a template as HTML: substituted values are encoded, line breaks become &lt;br&gt;.</summary>
    public static string RenderHtml(string template, Scope scope)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        // Encode the literal parts too: builders type plain text, not markup.
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (true)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(WebUtility.HtmlEncode(template[i..])); break; }
            var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) throw new ExpressionException("A {{ placeholder is missing its closing }}");
            sb.Append(WebUtility.HtmlEncode(template[i..open]));
            sb.Append(Template.Render(template[open..(close + 2)], scope, WebUtility.HtmlEncode));
            i = close + 2;
        }
        return sb.ToString().Trim().Replace("\r\n", "\n").Replace("\n", "<br>");
    }

    public static List<string> ParseEmails(string text) =>
        text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Contains('@') && e.Length <= 320)
            .Distinct()
            .ToList();

    private static int ResolveInt(NodeDef node, string key, string? formulaKey, Scope scope, int fallback, int min, int max, string what)
    {
        object? value = null;
        var formula = formulaKey is null ? "" : node.Get(formulaKey);
        if (!string.IsNullOrWhiteSpace(formula)) value = Formula.Evaluate(formula, scope);
        if (Values.IsBlank(value)) value = Template.RenderValue(node.Get(key), scope);
        if (Values.IsBlank(value)) return fallback;
        if (value is bool || !Values.TryToNumber(value, out var number) || number != Math.Floor(number) || number < min || number > max)
            throw new ExpressionException($"{Name(node)}: {what} must be a whole number from {min} to {max}, but got '{Values.Describe(value)}'.");
        return (int)number;
    }

    private static long? ResolveId(string template, Scope scope, string what)
    {
        var value = Template.RenderValue(template, scope);
        // A lookup field's value is a record {id, name}; use its id.
        if (value is Dictionary<string, object?> record) value = record.GetValueOrDefault("id");
        if (Values.IsBlank(value)) return null;
        if (value is bool || !Values.TryToNumber(value, out var number) || number != Math.Floor(number) || number <= 0 || number > long.MaxValue)
            throw new ExpressionException($"{what} must be a numeric ID, but got '{Values.Describe(value)}'.");
        return (long)number;
    }

    private static long ResolveTicketId(NodeDef node, Scope scope) =>
        ResolveId(Default(node.Get("ticketId"), "{{ticket.id}}"), scope, "Ticket ID")
        ?? throw new ExpressionException($"{Name(node)}: there is no ticket yet. Add a “Create ticket” step first, or set the Ticket ID.");

    private static Dictionary<string, object?> ResolveCustomFields(NodeDef node, Scope scope)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (name, template) in node.CustomFields)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = Template.RenderValue(template, scope);
            if (Values.IsBlank(value)) continue;
            if (value is Dictionary<string, object?> record) value = Values.ToText(record);
            if (value is DateTime date) value = Values.ToText(date);
            if (value is List<object?> list) value = list.Select(Values.ToText).ToList();
            result[name.Trim()] = value;
        }
        return result;
    }
}
