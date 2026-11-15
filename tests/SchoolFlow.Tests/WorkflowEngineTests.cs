using SchoolFlow.Core;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;

namespace SchoolFlow.Tests;

public class WorkflowEngineTests
{
    private static NodeDef N(string id, string type, string? label = null, params (string k, string v)[] config) =>
        new() { Id = id, Type = type, Label = label ?? id, Config = config.ToDictionary(c => c.k, c => c.v) };

    private static EdgeDef E(string from, string to, string port = "next") => new() { From = from, To = to, Port = port };

    /// <summary>start → over500? → (yes) approval → create ticket → note → wait until closed → end; (no) straight to create.</summary>
    private static FlowDefinition PurchaseFlow() => new()
    {
        Nodes =
        {
            N("start", "start"),
            N("big", "condition", "Over $500?", ("expression", "total > 500")),
            N("approve", "approval", "Principal approval", ("approvers", "{{principal_email}}; backup@school.org"), ("instructions", "Total: {{TEXT(total, 'C2')}}")),
            N("create", "fs_create_ticket", "Create ticket",
                ("subject", "Purchase: {{title}}"), ("description", "Requested by {{submitter.name}}\nTotal <{{total}}>"),
                ("priorityFormula", "IF(total > 1000, 3, 2)"), ("groupId", "{{group}}"), ("tags", "purchase, {{kind}}")),
            N("note", "fs_add_note", "Note", ("body", "Approved by {{approval.by}}"), ("visibility", "private")),
            N("wait", "wait_ticket", "Wait for close", ("until", "ticket.status >= 4")),
            N("done", "end", "All done"),
            N("no", "end", "Declined", ("outcome", "rejected")),
        },
        Edges =
        {
            E("start", "big"), E("big", "approve", "true"), E("big", "create", "false"),
            E("approve", "create", "approved"), E("approve", "no", "rejected"),
            E("create", "note"), E("note", "wait"), E("wait", "done"),
        },
    };

    private static RunState State(decimal total) => new()
    {
        Context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["total"] = total, ["title"] = "Lab kits", ["kind"] = "Hardware", ["principal_email"] = "Boss@School.org",
            ["group"] = new Dictionary<string, object?> { ["id"] = 203m, ["name"] = "Purchasing" },
            ["submitter"] = new Dictionary<string, object?> { ["email"] = "t@school.org", ["name"] = "T <Teacher>" },
        },
    };

    [Fact]
    public async Task FullPath_WithApproval_Ticket_Wait_AndCompletion()
    {
        var fs = new SimulatedFreshserviceClient();
        var engine = new WorkflowEngine(fs);
        var flow = PurchaseFlow();
        var state = State(1200);

        await engine.StartAsync(flow, state);
        Assert.Equal(RunStatus.WaitingApproval, state.Status);
        Assert.Equal(new[] { "boss@school.org", "backup@school.org" }, state.PendingApproval!.Assignees);
        Assert.Equal("Total: $1,200.00", state.PendingApproval.Instructions);
        Assert.Empty(fs.Calls);

        await engine.ResumeWithDecisionAsync(flow, state, new ApprovalDecision { Approved = true, By = "boss@school.org", Comment = "ok" });
        Assert.Equal(RunStatus.WaitingTicket, state.Status);
        Assert.Null(state.PendingApproval);

        var created = (TicketCreate)fs.Calls[0].Payload;
        Assert.Equal("Purchase: Lab kits", created.Subject);
        Assert.Equal("t@school.org", created.RequesterEmail);
        Assert.Equal(3, created.Priority);
        Assert.Equal(203, created.GroupId);
        Assert.Equal(new[] { "purchase", "Hardware" }, created.Tags);
        // Values and literal text are HTML-encoded; newlines become <br>.
        Assert.Equal("Requested by T &lt;Teacher&gt;<br>Total &lt;1200&gt;", created.DescriptionHtml);
        Assert.Equal("add_note", fs.Calls[1].Action);

        var ticketId = state.WaitingTicketId!.Value;
        Assert.Equal(90001, ticketId);

        // An update that does not satisfy the condition keeps waiting but still refreshes ticket data.
        var moved = await engine.ResumeWithTicketEventAsync(flow, state, new Dictionary<string, object?> { ["id"] = 90001m, ["status"] = 3m });
        Assert.False(moved);
        Assert.Equal(RunStatus.WaitingTicket, state.Status);

        moved = await engine.ResumeWithTicketEventAsync(flow, state, new Dictionary<string, object?> { ["id"] = 90001m, ["status"] = 5m });
        Assert.True(moved);
        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Null(state.WaitingTicketId);
        Assert.Equal("All done", state.NewEvents.Last().Message);
        Assert.All(state.NewEvents.Where(e => e.Kind == "ticket"), e => Assert.StartsWith("[Simulated]", e.Message));
    }

    [Fact]
    public async Task SmallPurchase_SkipsApproval()
    {
        var fs = new SimulatedFreshserviceClient();
        var state = State(100);
        var flow = PurchaseFlow();
        flow.Nodes.Single(n => n.Id == "note").Config["body"] = "Auto-approved (under limit)";
        await new WorkflowEngine(fs).StartAsync(flow, state);
        Assert.Equal(RunStatus.WaitingTicket, state.Status);
        Assert.Equal(2, ((TicketCreate)fs.Calls[0].Payload).Priority);
    }

    [Fact]
    public async Task Rejection_EndsAsRejected_WithoutTouchingFreshservice()
    {
        var fs = new SimulatedFreshserviceClient();
        var engine = new WorkflowEngine(fs);
        var flow = PurchaseFlow();
        var state = State(900);
        await engine.StartAsync(flow, state);
        await engine.ResumeWithDecisionAsync(flow, state, new ApprovalDecision { Approved = false, By = "boss@school.org" });
        Assert.Equal(RunStatus.Rejected, state.Status);
        Assert.Empty(fs.Calls);
        Assert.Equal("rejected", ((Dictionary<string, object?>)state.Context["approval"]!)["decision"]);
    }

    [Fact]
    public async Task DanglingRejectedBranch_StillRejects()
    {
        var flow = PurchaseFlow();
        flow.Edges.RemoveAll(e => e.Port == "rejected");
        var engine = new WorkflowEngine(new SimulatedFreshserviceClient());
        var state = State(900);
        await engine.StartAsync(flow, state);
        await engine.ResumeWithDecisionAsync(flow, state, new ApprovalDecision { Approved = false, By = "x@y.z" });
        Assert.Equal(RunStatus.Rejected, state.Status);
    }

    [Fact]
    public async Task StepError_FailsRun_AndRetryResumesFromThatStep()
    {
        var fs = new SimulatedFreshserviceClient();
        var engine = new WorkflowEngine(fs);
        var flow = PurchaseFlow();
        var state = State(100);
        state.Context["group"] = "not-a-number";

        await engine.StartAsync(flow, state);
        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal("create", state.CurrentNodeId);
        Assert.Contains("numeric ID", state.Error);
        Assert.Empty(fs.Calls);

        state.Context["group"] = 203m;
        await engine.RetryAsync(flow, state);
        Assert.Equal(RunStatus.WaitingTicket, state.Status);
        Assert.Single(fs.Calls, c => c.Action == "create_ticket");
    }

    [Fact]
    public async Task NoApprovers_Fails_WithHelpfulMessage()
    {
        var state = State(900);
        state.Context["principal_email"] = "";
        var flow = PurchaseFlow();
        flow.Nodes.Single(n => n.Id == "approve").Config["approvers"] = "{{principal_email}}";
        await new WorkflowEngine(new SimulatedFreshserviceClient()).StartAsync(flow, state);
        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Contains("no approver email", state.Error);
    }

    [Fact]
    public async Task InfiniteLoop_IsStopped()
    {
        var flow = new FlowDefinition
        {
            Nodes = { N("start", "start"), N("a", "set_variable", "A", ("name", "n"), ("expression", "n + 1")), N("b", "condition", "B", ("expression", "true")) },
            Edges = { E("start", "a"), E("a", "b"), E("b", "a", "true") },
        };
        var state = new RunState();
        await new WorkflowEngine(new SimulatedFreshserviceClient()).StartAsync(flow, state);
        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Contains("loop", state.Error);
    }

    [Fact]
    public async Task FreshserviceFailure_IsCaught()
    {
        var engine = new WorkflowEngine(new ThrowingClient());
        var state = State(100);
        await engine.StartAsync(PurchaseFlow(), state);
        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Equal("Freshservice: boom", state.Error);
    }

    [Fact]
    public async Task UpdateTicket_Lookup_SetVariable_ServiceRequest()
    {
        var fs = new SimulatedFreshserviceClient();
        var flow = new FlowDefinition
        {
            Nodes =
            {
                N("start", "start"),
                N("who", "fs_lookup", "Find person", ("kind", "requester"), ("saveAs", "person")),
                N("set", "set_variable", "Set", ("name", "greeting"), ("expression", "'Hi ' & person.name")),
                N("sr", "fs_service_request", "SR", ("itemId", "42"), ("quantity", "{{qty}}")),
                new NodeDef
                {
                    Id = "upd", Type = "fs_update_ticket", Label = "Upd", Config = new() { ["status"] = "3", ["priority"] = "4" },
                    CustomFields = new() { ["cf_total"] = "{{qty * 2}}", ["cf_note"] = "{{greeting}}!", ["cf_blank"] = "{{missing}}" },
                },
            },
            Edges = { E("start", "who"), E("who", "set"), E("set", "sr"), E("sr", "upd") },
        };
        var state = new RunState { Context = { ["qty"] = 3m, ["submitter"] = new Dictionary<string, object?> { ["email"] = "ann.lee@school.org" } } };
        await new WorkflowEngine(fs).StartAsync(flow, state);

        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Equal("Hi Ann Lee", state.Context["greeting"]);
        var (id, update) = ((long, TicketUpdate))fs.Calls.Single(c => c.Action == "update_ticket").Payload;
        Assert.Equal(90001, id);
        Assert.Equal(3, update.Status);
        Assert.Equal(4, update.Priority);
        Assert.Equal(6m, update.CustomFields["cf_total"]);       // single placeholder keeps its number type
        Assert.Equal("Hi Ann Lee!", update.CustomFields["cf_note"]);
        Assert.False(update.CustomFields.ContainsKey("cf_blank")); // blanks are not sent
        Assert.Equal(3m, ((Dictionary<string, object?>)state.Context["ticket"]!)["status"]);
    }

    [Fact]
    public async Task State_SurvivesJsonRoundTrip_BetweenSteps()
    {
        var fs = new SimulatedFreshserviceClient();
        var engine = new WorkflowEngine(fs);
        var flow = PurchaseFlow();
        var state = State(900);
        await engine.StartAsync(flow, state);

        var restored = new RunState
        {
            Status = state.Status, CurrentNodeId = state.CurrentNodeId,
            Context = Json.ToValues(Json.Serialize(state.Context)),
        };
        await engine.ResumeWithDecisionAsync(flow, restored, new ApprovalDecision { Approved = true, By = "boss@school.org" });
        Assert.Equal(RunStatus.WaitingTicket, restored.Status);

        var again = new RunState
        {
            Status = restored.Status, CurrentNodeId = restored.CurrentNodeId, WaitingTicketId = restored.WaitingTicketId,
            Context = Json.ToValues(Json.Serialize(restored.Context)),
        };
        Assert.True(await engine.ResumeWithTicketEventAsync(flow, again, Json.ToValues("{\"id\":90001,\"status\":4}")));
        Assert.Equal(RunStatus.Completed, again.Status);
    }

    [Fact]
    public async Task Resume_InWrongState_IsRefused()
    {
        var engine = new WorkflowEngine(new SimulatedFreshserviceClient());
        var state = new RunState { Status = RunStatus.Completed };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.ResumeWithDecisionAsync(PurchaseFlow(), state, new ApprovalDecision()));
        Assert.False(await engine.ResumeWithTicketEventAsync(PurchaseFlow(), state, new()));
    }

    private sealed class ThrowingClient : IFreshserviceClient
    {
        public bool IsLive => true;
        public Task<Dictionary<string, object?>> CreateTicketAsync(TicketCreate ticket, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task<Dictionary<string, object?>> UpdateTicketAsync(long ticketId, TicketUpdate update, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task AddNoteAsync(long ticketId, string bodyHtml, bool isPrivate, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task AttachFilesAsync(long ticketId, IReadOnlyList<TicketFile> files, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity, Dictionary<string, object?> customFields, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default) => throw new FreshserviceException("boom");
        public Task<string> TestConnectionAsync(CancellationToken ct = default) => throw new FreshserviceException("boom");
    }
}
