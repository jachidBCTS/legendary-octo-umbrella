using SchoolFlow.Core;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;

namespace SchoolFlow.Tests;

public class ApprovalChainTests
{
    private sealed class FakeDirectory : IApprovalDirectory
    {
        public Dictionary<long, List<ChainStep>> Chains { get; } = new()
        {
            [1] = new()
            {
                new ChainStep { Name = "Team lead", Approvers = { "lead@school.org" } },
                new ChainStep { Name = "Committee", Approvers = { "c1@school.org", "c2@school.org" }, Rule = "all" },
            },
        };
        public Dictionary<long, DepartmentInfo> Departments { get; } = new()
        {
            [10] = new DepartmentInfo(10, "Science", new() { "head@school.org", "T@School.org" }, "any"),
            [11] = new DepartmentInfo(11, "Empty", new(), "any"),
            [12] = new DepartmentInfo(12, "Solo", new() { "t@school.org" }, "any"),
        };
        public Task<List<ChainStep>?> GetChainAsync(long id, CancellationToken ct = default) => Task.FromResult(Chains.GetValueOrDefault(id));
        public Task<DepartmentInfo?> GetDepartmentAsync(long id, CancellationToken ct = default) => Task.FromResult(Departments.GetValueOrDefault(id));
    }

    private static FlowDefinition Flow(params (string k, string v)[] config) => new()
    {
        Nodes =
        {
            new NodeDef { Id = "s", Type = "start" },
            new NodeDef { Id = "chain", Type = "approval_chain", Label = "Sign-off", Config = config.ToDictionary(c => c.k, c => c.v) },
            new NodeDef { Id = "tell", Type = "notify", Label = "Tell requester", Config = new() { ["to"] = "{{submitter.email}}, parent@example.com", ["subject"] = "Trip to {{place}} approved", ["message"] = "Approved by {{approval.by}}" } },
            new NodeDef { Id = "yes", Type = "end", Label = "Approved" },
            new NodeDef { Id = "no", Type = "end", Label = "Declined", Config = new() { ["outcome"] = "rejected" } },
        },
        Edges =
        {
            new EdgeDef { From = "s", To = "chain" }, new EdgeDef { From = "chain", To = "tell", Port = "approved" },
            new EdgeDef { From = "chain", To = "no", Port = "rejected" }, new EdgeDef { From = "tell", To = "yes" },
        },
    };

    private static RunState State() => new()
    {
        Context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["place"] = "Zoo",
            ["submitter"] = new Dictionary<string, object?> { ["email"] = "t@school.org", ["name"] = "T" },
            ["dept"] = new Dictionary<string, object?> { ["id"] = 10m, ["name"] = "Science" },
            ["my_chain"] = new Dictionary<string, object?>
            {
                ["name"] = "Mine",
                ["steps"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "Buddy", ["approvers"] = new List<object?> { "buddy@school.org" }, ["rule"] = "any" } },
            },
        },
    };

    private static WorkflowEngine Engine() => new(new SimulatedFreshserviceClient(), directory: new FakeDirectory());
    private static ApprovalDecision Yes(string by) => new() { Approved = true, By = by };

    [Fact]
    public async Task Template_ThenDepartmentFinalApprover_RunInOrder()
    {
        var engine = Engine();
        var flow = Flow(("chainSource", "template"), ("chainId", "1"), ("departmentSource", "field"), ("departmentField", "dept"), ("instructions", "Trip to {{place}}"));
        var state = State();
        await engine.StartAsync(flow, state);

        Assert.Equal(RunStatus.WaitingApproval, state.Status);
        Assert.Equal("Team lead", state.PendingApproval!.Title);
        Assert.Equal("Step 1 of 3", state.PendingApproval.Position);
        Assert.Equal("Trip to Zoo", state.PendingApproval.Instructions);
        Assert.Equal(new[] { "lead@school.org" }, state.PendingApproval.Assignees);

        await engine.ResumeWithDecisionAsync(flow, state, Yes("lead"));
        Assert.Equal(RunStatus.WaitingApproval, state.Status);
        Assert.Equal("Committee", state.PendingApproval!.Title);
        Assert.Equal("all", state.PendingApproval.Rule);
        Assert.Equal("Step 2 of 3", state.PendingApproval.Position);

        await engine.ResumeWithDecisionAsync(flow, state, Yes("committee"));
        Assert.Equal("Final approval — Science", state.PendingApproval!.Title);
        // The submitter is one of the department's final approvers but may not approve their own request.
        Assert.Equal(new[] { "head@school.org" }, state.PendingApproval.Assignees);

        await engine.ResumeWithDecisionAsync(flow, state, Yes("head"));
        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Null(state.PendingApproval);
        var notes = state.NewNotifications;
        Assert.Equal(new[] { "t@school.org", "parent@example.com" }, notes.Select(n => n.Email).ToArray());
        Assert.All(notes, n => { Assert.Equal("Trip to Zoo approved", n.Title); Assert.Equal("Approved by head", n.Body); });
        var steps = (List<object?>)((Dictionary<string, object?>)state.Context["chain"]!)["steps"]!;
        Assert.All(steps, s => Assert.Equal("approved", ((Dictionary<string, object?>)s!)["status"]));
    }

    [Fact]
    public async Task SubmitterChain_FromFormField_SurvivesStorageBetweenSteps()
    {
        var engine = Engine();
        var flow = Flow(("chainSource", "field"), ("chainField", "my_chain"), ("departmentSource", "fixed"), ("departmentId", "10"));
        var state = State();
        await engine.StartAsync(flow, state);
        Assert.Equal("Buddy", state.PendingApproval!.Title);

        var restored = new RunState { Status = state.Status, CurrentNodeId = state.CurrentNodeId, Context = Json.ToValues(Json.Serialize(state.Context)) };
        await engine.ResumeWithDecisionAsync(flow, restored, Yes("buddy"));
        Assert.Equal("Final approval — Science", restored.PendingApproval!.Title);
        Assert.Equal("Step 2 of 2", restored.PendingApproval.Position);
    }

    [Fact]
    public async Task Rejection_MidChain_StopsTheChain()
    {
        var engine = Engine();
        var flow = Flow(("chainSource", "template"), ("chainId", "1"), ("departmentSource", "fixed"), ("departmentId", "10"));
        var state = State();
        await engine.StartAsync(flow, state);
        await engine.ResumeWithDecisionAsync(flow, state, new ApprovalDecision { Approved = false, By = "lead" });
        Assert.Equal(RunStatus.Rejected, state.Status);
        Assert.Empty(state.NewNotifications);
        Assert.Contains(state.NewEvents, e => e.Message == "Declined");
    }

    [Fact]
    public async Task DepartmentOnly_And_SoleFinalApproverWhoIsTheSubmitter_IsKept()
    {
        var engine = Engine();
        var state = State();
        await engine.StartAsync(Flow(("departmentSource", "fixed"), ("departmentId", "12")), state);
        Assert.Equal(new[] { "t@school.org" }, state.PendingApproval!.Assignees);
        Assert.Equal("Step 1 of 1", state.PendingApproval.Position);
    }

    [Theory]
    [InlineData("fixed", "11", "has no final approver set")]
    [InlineData("fixed", "99", "department no longer exists")]
    [InlineData("field", "", "no department was chosen")]
    public async Task DepartmentProblems_FailWithAClearMessage(string source, string id, string expected)
    {
        var state = State();
        state.Context.Remove("dept");
        await Engine().StartAsync(Flow(("departmentSource", source), ("departmentId", id), ("departmentField", "dept")), state);
        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Contains(expected, state.Error);
    }

    [Fact]
    public async Task MissingTemplate_Or_NothingToApprove_Fails()
    {
        var a = State();
        await Engine().StartAsync(Flow(("chainSource", "template"), ("chainId", "77")), a);
        Assert.Contains("no longer exists", a.Error);

        var b = State();
        b.Context["my_chain"] = null;
        await Engine().StartAsync(Flow(("chainSource", "field"), ("chainField", "my_chain")), b);
        Assert.Contains("nobody to approve", b.Error);
    }

    [Fact]
    public async Task Notify_WithNoRecipients_DoesNotStopTheRequest()
    {
        var flow = Flow(("departmentSource", "fixed"), ("departmentId", "10"));
        flow.Nodes.Single(n => n.Id == "tell").Config["to"] = "{{missing_field}}";
        var engine = Engine();
        var state = State();
        await engine.StartAsync(flow, state);
        await engine.ResumeWithDecisionAsync(flow, state, Yes("head"));
        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Empty(state.NewNotifications);
        Assert.Contains(state.NewEvents, e => e.Message.Contains("nobody to notify"));
    }

    [Fact]
    public void ChainField_IsCleaned_AndProblemsReported()
    {
        var form = new FormDefinition { Fields = { new FieldDef { Key = "approvers", Label = "Approvers", Type = "approval_chain", Required = true } } };
        var ambient = new Dictionary<string, object?> { ["submitter"] = new Dictionary<string, object?> { ["email"] = "t@school.org" } };
        var input = Json.ToValues("""
        { "approvers": { "name": " Mine ", "evil": 1, "steps": [
            { "name": "", "approvers": ["Lead@School.org", "lead@school.org", " "], "rule": "all", "status": "approved" },
            { "name": "Second", "approvers": "a@school.org; not-an-email, t@school.org", "rule": "whatever" },
            { "name": "Third", "approvers": [] } ] } }
        """);
        var result = FormEvaluator.Evaluate(form, input, ambient);
        var errors = string.Join(" | ", result.Fields["approvers"].Errors);
        Assert.Contains("'not-an-email' is not an email address", errors);
        Assert.Contains("Second: you cannot be an approver on your own request", errors);
        Assert.Contains("Third: add at least one approver", errors);

        var steps = ChainValue.ToSteps(result.Values["approvers"]);
        Assert.Equal("Step 1", steps[0].Name);
        Assert.Equal(new[] { "lead@school.org" }, steps[0].Approvers);
        Assert.Equal("all", steps[0].Rule);
        Assert.Equal(new[] { "a@school.org" }, steps[1].Approvers);
        Assert.Equal("any", steps[1].Rule);
        var json = Json.Serialize(result.Values);
        Assert.DoesNotContain("evil", json);
        Assert.DoesNotContain("approved", json); // a client cannot pre-mark a step as approved
        Assert.Equal("Step 1: lead@school.org → Second: a@school.org → Third: ", ChainValue.Describe(result.Values["approvers"]));

        var empty = FormEvaluator.Evaluate(form, Json.ToValues("{\"approvers\":{\"steps\":[]}}"), ambient);
        Assert.Equal("This field is required.", empty.Fields["approvers"].Errors.Single());
    }

    [Fact]
    public void DepartmentField_OnlyAcceptsKnownDepartments_AndUsesTheServersName()
    {
        var form = new FormDefinition { Fields = { new FieldDef { Key = "dept", Label = "Department", Type = "department" } } };
        var known = new Dictionary<long, string> { [10] = "Science" };
        var ok = FormEvaluator.Evaluate(form, Json.ToValues("{\"dept\":{\"id\":10,\"name\":\"Hacked name\"}}"), departments: known);
        Assert.Equal("Science", ((Dictionary<string, object?>)ok.Values["dept"]!)["name"]);
        var bad = FormEvaluator.Evaluate(form, Json.ToValues("{\"dept\":{\"id\":99,\"name\":\"Ghost\"}}"), departments: known);
        Assert.Null(bad.Values["dept"]);
        Assert.Contains("no longer available", bad.Fields["dept"].Errors.Single());
        var junk = FormEvaluator.Evaluate(form, Json.ToValues("{\"dept\":{\"id\":\"abc\"}}"), departments: known);
        Assert.Contains("from the list", junk.Fields["dept"].Errors.Single());
    }

    [Fact]
    public void Validator_ChecksChainAndNotifySteps()
    {
        var def = new WorkflowDefinition
        {
            Form = { Fields = { new FieldDef { Key = "dept", Label = "Dept", Type = "department" }, new FieldDef { Key = "who", Label = "Who", Type = "approval_chain" }, new FieldDef { Key = "title", Label = "Title", Type = "text" } } },
            Flow = Flow(("chainSource", "field"), ("chainField", "who"), ("departmentSource", "field"), ("departmentField", "dept")),
        };
        def.Flow.Nodes.Single(n => n.Id == "tell").Config["subject"] = "Approved";
        def.Flow.Nodes.Single(n => n.Id == "tell").Config["message"] = "";
        Assert.DoesNotContain(DefinitionValidator.Validate(def), i => i.IsError);

        var chain = def.Flow.Nodes.Single(n => n.Id == "chain");
        chain.Config["chainField"] = "title";
        chain.Config["departmentField"] = "nope";
        def.Flow.Nodes.Single(n => n.Id == "tell").Config.Remove("to");
        var errors = string.Join("\n", DefinitionValidator.Validate(def).Where(i => i.IsError).Select(i => i.Message));
        Assert.Contains("choose an Approval chain field", errors);
        Assert.Contains("choose a Department field", errors);
        Assert.Contains("recipients is empty", errors);

        chain.Config.Clear();
        Assert.Contains(DefinitionValidator.Validate(def), i => i.IsError && i.Message.Contains("nobody would approve"));
    }
}
