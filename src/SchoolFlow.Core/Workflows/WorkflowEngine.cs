using System.Net;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Freshservice;

namespace SchoolFlow.Core.Workflows;

public static class RunStatus
{
    public const string Running = "Running", WaitingApproval = "WaitingApproval", WaitingTicket = "WaitingTicket",
        Completed = "Completed", Rejected = "Rejected", Failed = "Failed", Cancelled = "Cancelled";

    public static bool IsFinished(string status) => status is Completed or Rejected or Cancelled;
    public static bool IsWaiting(string status) => status is WaitingApproval or WaitingTicket;
}

public sealed record RunEvent(DateTime At, string? NodeId, string Kind, string Message);

public sealed class PendingApproval
{
    public string NodeId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<string> Assignees { get; set; } = new();
}

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
    private readonly Func<DateTime> _now;

    public WorkflowEngine(IFreshserviceClient freshservice, Func<DateTime>? now = null)
    {
        _freshservice = freshservice;
        _now = now ?? (() => DateTime.Now);
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
        state.Status = RunStatus.Running;
        Follow(flow, state, node, decision.Approved ? "approved" : "rejected");
        return AdvanceAsync(flow, state, ct);
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
                var assignees = ParseEmails(Template.Render(node.Get("approvers"), scope));
                if (assignees.Count == 0)
                    throw new ExpressionException($"{Name(node)}: no approver email could be worked out. Check the Approvers setting.");
                state.PendingApproval = new PendingApproval
                {
                    NodeId = node.Id,
                    Title = Name(node),
                    Instructions = Template.Render(node.Get("instructions"), scope),
                    Assignees = assignees,
                };
                state.Status = RunStatus.WaitingApproval;
                Log(state, node.Id, "waiting", $"Waiting for approval from {string.Join(", ", assignees)}");
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
                    Status = ResolveInt(node, "status", null, scope, 2, 2, 5, "Status"),
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
                    Status = string.IsNullOrWhiteSpace(node.Get("status")) ? null : ResolveInt(node, "status", null, scope, 2, 2, 5, "Status"),
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

    // ---- helpers ---------------------------------------------------------------------

    private Scope NewScope(RunState state) => new(state.Context, now: _now);

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
