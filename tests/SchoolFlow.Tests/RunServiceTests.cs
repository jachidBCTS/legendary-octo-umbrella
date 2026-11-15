using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SchoolFlow.Core;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Tests;

/// <summary>Runs the real persistence layer against an in-memory SQLite database.</summary>
public sealed class RunServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly SimulatedFreshserviceClient _freshservice = new();

    public RunServiceTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
        SeedData.EnsureStarterWorkflows(db);
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext Db() => new(_options);
    private RunService Service(AppDbContext db) => new(db, _freshservice, NullLogger<RunService>.Instance);

    private static Dictionary<string, object?> PurchaseInput(decimal price) => new()
    {
        ["title"] = "Projector",
        ["department"] = new Dictionary<string, object?> { ["id"] = 104m, ["name"] = "Mathematics" },
        ["needed_by"] = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"),
        ["kind"] = "Hardware",
        ["items"] = new List<object?> { new Dictionary<string, object?> { ["item"] = "Projector", ["category"] = "Hardware", ["qty"] = 1m, ["unit_price"] = price } },
        ["approver_email"] = "boss@school.org",
        ["justification"] = "Old one broke",
    };

    private async Task<int> SubmitPurchaseAsync(decimal price)
    {
        await using var db = Db();
        var workflow = await db.Workflows.SingleAsync(w => w.Name == "Technology Purchase Request");
        var (form, runId) = await Service(db).SubmitFormAsync(workflow, PurchaseInput(price), "t@school.org", "T Teacher", default);
        Assert.True(form.IsValid, string.Join(" | ", form.Fields.SelectMany(f => f.Value.Errors.Select(e => f.Key + ": " + e))));
        return runId!.Value;
    }

    [Fact]
    public void StarterWorkflows_HaveNoValidationIssues()
    {
        foreach (var (name, json) in SeedData.Templates)
        {
            var issues = DefinitionValidator.Validate(Json.ParseDefinition(json));
            Assert.True(issues.Count == 0, name + ": " + string.Join(" | ", issues.Select(i => i.Message)));
        }
    }

    [Fact]
    public async Task InvalidSubmission_CreatesNothing()
    {
        await using var db = Db();
        var workflow = await db.Workflows.SingleAsync(w => w.Name == "Technology Purchase Request");
        var (form, runId) = await Service(db).SubmitFormAsync(workflow, new Dictionary<string, object?>(), "t@school.org", "T", default);
        Assert.False(form.IsValid);
        Assert.Null(runId);
        Assert.Equal(0, await db.Runs.CountAsync());
    }

    [Fact]
    public async Task SmallPurchase_GoesStraightToTicket_AndWebhookCompletesIt()
    {
        var runId = await SubmitPurchaseAsync(300);
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.WaitingTicket, run.Status);
            Assert.Equal(90001, run.TicketId);
            Assert.Equal(90001, run.WaitingTicketId);
            Assert.Equal(0, await db.ApprovalTasks.CountAsync());

            var outcome = await Service(db).HandleTicketEventAsync(Json.ToValues("{\"ticket\":{\"id\":\"INC-90001\",\"status\":4}}"), default);
            Assert.Contains("resumed 1", outcome);
        }
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Completed, run.Status);
            Assert.Null(run.WaitingTicketId);
            Assert.Contains(await db.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message == "Purchase complete");
        }
    }

    [Fact]
    public async Task Approval_OnlyAssigneeCanDecide_AndOnlyOnce()
    {
        var runId = await SubmitPurchaseAsync(900);
        int taskId;
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync();
            taskId = task.Id;
            Assert.True(task.IsAssignedTo("BOSS@school.org"));
            Assert.False(task.IsAssignedTo("oss@school.org")); // no partial-email matches

            var stranger = await Service(db).DecideAsync(taskId, "other@school.org", "Other", isAdmin: false, approved: true, null, default);
            Assert.False(stranger.Ok);
            Assert.Equal(ApprovalStatus.Pending, (await db.ApprovalTasks.AsNoTracking().SingleAsync()).Status);
        }
        await using (var db = Db())
        {
            var ok = await Service(db).DecideAsync(taskId, "boss@school.org", "Boss", false, true, "fine", default);
            Assert.True(ok.Ok, ok.Error);
        }
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync();
            Assert.Equal(ApprovalStatus.Approved, task.Status); // not overwritten to Cancelled when the run moved on
            Assert.Equal("boss@school.org", task.DecidedBy);
            Assert.Equal(RunStatus.WaitingTicket, (await db.Runs.SingleAsync()).Status);
            Assert.Single(_freshservice.Calls, c => c.Action == "create_ticket");
            Assert.Single(_freshservice.Calls, c => c.Action == "add_note");

            var again = await Service(db).DecideAsync(taskId, "boss@school.org", "Boss", false, false, null, default);
            Assert.False(again.Ok);
            Assert.Contains("already approved", again.Error);
            Assert.Single(_freshservice.Calls, c => c.Action == "create_ticket");
        }
    }

    [Fact]
    public async Task ConcurrentDecisions_SecondOneLoses_WithoutDoubleRunning()
    {
        await SubmitPurchaseAsync(900);
        await using var first = Db();
        await using var second = Db();
        var taskId = (await first.ApprovalTasks.SingleAsync()).Id;
        // Load the same run into both contexts before either saves, as two simultaneous requests would.
        await first.Runs.SingleAsync();
        await second.Runs.SingleAsync();
        await second.ApprovalTasks.SingleAsync();

        var a = await Service(first).DecideAsync(taskId, "boss@school.org", "Boss", false, true, null, default);
        var b = await Service(second).DecideAsync(taskId, "boss@school.org", "Boss", false, false, null, default);
        Assert.True(a.Ok);
        Assert.False(b.Ok);

        await using var check = Db();
        Assert.Equal(RunStatus.WaitingTicket, (await check.Runs.SingleAsync()).Status);
        Assert.Equal(ApprovalStatus.Approved, (await check.ApprovalTasks.SingleAsync()).Status);
    }

    [Fact]
    public async Task Rejection_And_Cancel()
    {
        var rejectedId = await SubmitPurchaseAsync(900);
        var cancelledId = await SubmitPurchaseAsync(900);
        await using var db = Db();
        var tasks = await db.ApprovalTasks.OrderBy(t => t.Id).ToListAsync();
        Assert.True((await Service(db).DecideAsync(tasks[0].Id, "boss@school.org", "Boss", false, false, "no budget", default)).Ok);
        Assert.True((await Service(db).CancelAsync(cancelledId, "Admin", default)).Ok);
        Assert.False((await Service(db).CancelAsync(cancelledId, "Admin", default)).Ok);

        await using var check = Db();
        Assert.Equal(RunStatus.Rejected, (await check.Runs.SingleAsync(r => r.Id == rejectedId)).Status);
        Assert.Equal(RunStatus.Cancelled, (await check.Runs.SingleAsync(r => r.Id == cancelledId)).Status);
        Assert.Equal(ApprovalStatus.Cancelled, (await check.ApprovalTasks.SingleAsync(t => t.RunId == cancelledId)).Status);
        Assert.Empty(_freshservice.Calls);
    }

    [Fact]
    public async Task TicketTrigger_StartsOncePerTicket_AndRespectsCondition()
    {
        await using (var db = Db())
        {
            var w = await db.Workflows.SingleAsync(x => x.TriggerType == TriggerTypes.Ticket);
            w.PublishedJson = w.DraftJson;
            w.PublishedVersion = 1;
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            var service = Service(db);
            Assert.Contains("started 0", await service.HandleTicketEventAsync(Json.ToValues("{\"ticket\":{\"id\":7,\"priority\":2}}"), default));
            Assert.Contains("started 1", await service.HandleTicketEventAsync(Json.ToValues("{\"ticket\":{\"id\":7,\"priority\":4}}"), default));
            // The workflow's own note would make Freshservice fire the webhook again: must not start a second run.
            Assert.Contains("started 0", await service.HandleTicketEventAsync(Json.ToValues("{\"ticket\":{\"id\":7,\"priority\":4}}"), default));
            Assert.Contains("no ticket.id", await service.HandleTicketEventAsync(Json.ToValues("{\"ticket\":{}}"), default));
        }
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync();
            Assert.Equal(RunStatus.WaitingTicket, run.Status);
            Assert.Equal(7, run.TriggerTicketId);
            Assert.Contains("resumed 1", await Service(db).HandleTicketEventAsync(Json.ToValues("{\"ticket\":{\"id\":7,\"priority\":4,\"status\":5}}"), default));
        }
        await using (var db = Db())
            Assert.Equal(RunStatus.Completed, (await db.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task WebhookPayload_CannotSpoofSubmitter()
    {
        await using (var db = Db())
        {
            var w = await db.Workflows.SingleAsync(x => x.TriggerType == TriggerTypes.Ticket);
            w.PublishedJson = w.DraftJson;
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
            await Service(db).HandleTicketEventAsync(Json.ToValues("{\"submitter\":{\"email\":\"evil@x.org\"},\"ticket\":{\"id\":9,\"priority\":4}}"), default);
        await using (var db = Db())
            Assert.DoesNotContain("evil@x.org", (await db.Runs.SingleAsync()).ContextJson);
    }

    [Theory]
    [InlineData("INC-123", 123L)]
    [InlineData("SR-45", 45L)]
    [InlineData("77", 77L)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void ParseTicketId_HandlesDisplayIds(string input, long? expected) =>
        Assert.Equal(expected, RunService.ParseTicketId(input));
}
