using Microsoft.Extensions.Configuration;
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
        SeedData.EnsureStarterWorkflows(db, sampleDepartments: true);
        db.SaveChanges();
        db.Users.Add(new AppUser { Email = "optout@school.org", Name = "Opt Out", EmailNotifications = false, LastSeenAt = DateTime.UtcNow });
        db.Users.Add(new AppUser { Email = "t@school.org", Name = "T Teacher", LastSeenAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext Db() => new(_options);
    private static readonly Microsoft.Extensions.Configuration.IConfiguration Config =
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Mode"] = "Graph", ["Email:From"] = "noreply@school.org", ["Email:BaseUrl"] = "https://flow.school.org",
            ["AzureAd:TenantId"] = "t", ["AzureAd:ClientId"] = "c", ["AzureAd:ClientSecret"] = "s",
            ["Auth:Admins:0"] = "admin@school.org",
        }).Build();

    private RunService Service(AppDbContext db) =>
        new(db, _freshservice, new NotificationService(db, new EmailSettings(Config), Config), NullLogger<RunService>.Instance);

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

    // ---- approval chains, departments, notifications ---------------------------------

    private static Dictionary<string, object?> TripInput(string department, int departmentId, object? chain) => new()
    {
        ["destination"] = "Science Museum",
        ["department"] = new Dictionary<string, object?> { ["id"] = (decimal)departmentId, ["name"] = department },
        ["trip_date"] = DateTime.Today.AddDays(60).ToString("yyyy-MM-dd"),
        ["students"] = 40m, ["chaperones"] = 4m, ["cost_per_student"] = 12m, ["purpose"] = "Unit on energy",
        ["approvers"] = chain,
    };

    private async Task<int> SubmitTripAsync(string department, object? chain = null, string email = "t@school.org")
    {
        await using var db = Db();
        var workflow = await db.Workflows.SingleAsync(w => w.Name == "Field Trip Request");
        var dept = await db.Departments.SingleAsync(d => d.Name == department);
        var (form, runId) = await Service(db).SubmitFormAsync(workflow, TripInput(department, dept.Id, chain), email, "T Teacher", default);
        Assert.True(form.IsValid, string.Join(" | ", form.Fields.SelectMany(f => f.Value.Errors.Select(e => f.Key + ": " + e))));
        return runId!.Value;
    }

    private async Task DecideAsync(int runId, string email, bool approved = true, bool admin = false, bool expectOk = true)
    {
        await using var db = Db();
        var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
        var result = await Service(db).DecideAsync(task.Id, email, email, admin, approved, null, default);
        Assert.Equal(expectOk, result.Ok);
    }

    [Fact]
    public async Task Chain_RunsSubmitterSteps_ThenDepartment_AndNotifiesEachStage()
    {
        var chain = new Dictionary<string, object?>
        {
            ["steps"] = new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "Team leader", ["approvers"] = new List<object?> { "lead@school.org" } },
                new Dictionary<string, object?> { ["name"] = "Nurse", ["approvers"] = new List<object?> { "optout@school.org" } },
            },
        };
        var runId = await SubmitTripAsync("Science", chain);
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            Assert.Equal("Science", run.DepartmentName);
            var task = await db.ApprovalTasks.SingleAsync();
            Assert.Equal("Team leader", task.Title);
            Assert.Equal("Step 1 of 3", task.Position);
            Assert.Contains("Science Museum", task.Instructions);
            var note = await db.Notifications.SingleAsync();
            Assert.Equal("lead@school.org", note.UserEmail);
            Assert.StartsWith("Approval needed: Field Trip Request", note.Title);
            Assert.Equal($"/Requests/Detail/{runId}", note.Link);
            Assert.Equal(EmailStatus.Pending, note.EmailStatus);
        }

        await DecideAsync(runId, "principal@example.org", expectOk: false); // not their step yet
        await DecideAsync(runId, "lead@school.org");
        await using (var db = Db())
        {
            Assert.Equal("Nurse", (await db.ApprovalTasks.SingleAsync(t => t.Status == ApprovalStatus.Pending)).Title);
            // This person switched email off: they still get the in-app notification.
            Assert.Equal(EmailStatus.None, (await db.Notifications.SingleAsync(n => n.UserEmail == "optout@school.org")).EmailStatus);
        }
        await DecideAsync(runId, "optout@school.org");
        await using (var db = Db())
        {
            var final = await db.ApprovalTasks.SingleAsync(t => t.Status == ApprovalStatus.Pending);
            Assert.Equal("Final approval — Science", final.Title);
            Assert.True(final.IsAssignedTo("principal@example.org"));
        }
        await DecideAsync(runId, "principal@example.org");

        await using (var check = Db())
        {
            Assert.Equal(RunStatus.Completed, (await check.Runs.SingleAsync(r => r.Id == runId)).Status);
            Assert.Single(_freshservice.Calls, c => c.Action == "create_ticket");
            var mine = await check.Notifications.Where(n => n.UserEmail == "t@school.org").OrderBy(n => n.Id).Select(n => n.Title).ToListAsync();
            Assert.Equal(new[] { "Your trip to Science Museum is approved", $"Completed: Field Trip Request #{runId}" }, mine);
            Assert.Equal(3, await check.ApprovalTasks.CountAsync(t => t.Status == ApprovalStatus.Approved));
        }
    }

    [Fact]
    public async Task AllMustApprove_WaitsForEveryone_AndBlocksDoubleApproval()
    {
        var runId = await SubmitTripAsync("Mathematics"); // final approvers: principal AND admin
        await DecideAsync(runId, "principal@example.org");
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync();
            Assert.Equal(ApprovalStatus.Pending, task.Status);
            Assert.True(task.HasApproved("principal@example.org"));
            Assert.Equal(RunStatus.WaitingApproval, (await db.Runs.SingleAsync()).Status);
            Assert.Contains(await db.RunEvents.ToListAsync(), e => e.Message.Contains("(1 of 2). Still waiting for admin@example.org"));
        }
        await DecideAsync(runId, "principal@example.org", expectOk: false); // cannot approve twice
        await DecideAsync(runId, "admin@example.org");
        await using (var check = Db())
            Assert.Equal(RunStatus.Completed, (await check.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task AllMustApprove_OneRejectionEndsIt_AndRequesterIsTold()
    {
        var runId = await SubmitTripAsync("Mathematics");
        await DecideAsync(runId, "principal@example.org");
        await DecideAsync(runId, "admin@example.org", approved: false);
        await using var check = Db();
        Assert.Equal(RunStatus.Rejected, (await check.Runs.SingleAsync()).Status);
        Assert.Empty(_freshservice.Calls);
        Assert.Contains(await check.Notifications.Where(n => n.UserEmail == "t@school.org").ToListAsync(), n => n.Title.StartsWith("Not approved"));
    }

    [Fact]
    public async Task UnknownOrInactiveDepartment_IsRefusedAtSubmission()
    {
        await using var db = Db();
        var athletics = await db.Departments.SingleAsync(d => d.Name == "Athletics");
        athletics.IsActive = false;
        await db.SaveChangesAsync();
        var workflow = await db.Workflows.SingleAsync(w => w.Name == "Field Trip Request");
        var (form, runId) = await Service(db).SubmitFormAsync(workflow, TripInput("Athletics", athletics.Id, null), "t@school.org", "T", default);
        Assert.Null(runId);
        Assert.Contains("no longer available", form.Fields["department"].Errors.Single());
    }

    [Fact]
    public async Task DepartmentWithoutFinalApprover_FailsTheRun_AndAlertsAdmins()
    {
        await using (var db = Db())
        {
            (await db.Departments.SingleAsync(d => d.Name == "Science")).FinalApprovers = "";
            await db.SaveChangesAsync();
        }
        var runId = await SubmitTripAsync("Science");
        await using var check = Db();
        var run = await check.Runs.SingleAsync(r => r.Id == runId);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("has no final approver set", run.Error);
        var alert = await check.Notifications.SingleAsync();
        Assert.Equal("admin@school.org", alert.UserEmail); // from Auth:Admins
        Assert.StartsWith("Needs attention", alert.Title);
    }

    private sealed class FakeSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public Queue<Exception?> Outcomes { get; } = new();
        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
        {
            if (Outcomes.Count > 0 && Outcomes.Dequeue() is { } failure) throw failure;
            Sent.Add((to, subject, htmlBody));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Outbox_Sends_Retries_AndGivesUp()
    {
        await using var db = Db();
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        db.Notifications.AddRange(
            new Notification { UserEmail = "a@school.org", Title = "Hello <b>", Body = "Line 1\nLine 2\n\nSecond <para>", Link = "/Requests/Detail/5", EmailStatus = EmailStatus.Pending, CreatedAt = now },
            new Notification { UserEmail = "b@school.org", Title = "Throttled", EmailStatus = EmailStatus.Pending, CreatedAt = now },
            new Notification { UserEmail = "c@school.org", Title = "Bad config", EmailStatus = EmailStatus.Pending, CreatedAt = now },
            new Notification { UserEmail = "d@school.org", Title = "In-app only", EmailStatus = EmailStatus.None, CreatedAt = now });
        await db.SaveChangesAsync();

        var sender = new FakeSender();
        sender.Outcomes.Enqueue(null);
        sender.Outcomes.Enqueue(new EmailException("slow down", transient: true));
        sender.Outcomes.Enqueue(new EmailException("Mail.Send missing", transient: false));
        Assert.Equal(1, await EmailOutboxWorker.ProcessBatchAsync(db, sender, "https://flow.school.org", now, default));

        var first = sender.Sent.Single();
        Assert.Equal("a@school.org", first.To);
        Assert.Contains("https://flow.school.org/Requests/Detail/5", first.Html);
        Assert.Contains("Hello &lt;b&gt;", first.Html);                 // HTML-encoded
        Assert.Contains("Line 1<br>Line 2", first.Html);
        Assert.Contains("Second &lt;para&gt;", first.Html);

        var rows = await db.Notifications.OrderBy(n => n.Id).ToListAsync();
        Assert.Equal(EmailStatus.Sent, rows[0].EmailStatus);
        Assert.Equal(EmailStatus.Pending, rows[1].EmailStatus);          // transient: will retry
        Assert.Equal(now.AddMinutes(2), rows[1].EmailNextAttemptAt);
        Assert.Equal(EmailStatus.Failed, rows[2].EmailStatus);           // configuration problem: no point retrying
        Assert.Equal("Mail.Send missing", rows[2].EmailError);
        Assert.Equal(EmailStatus.None, rows[3].EmailStatus);

        // Not due yet, so nothing is attempted.
        Assert.Equal(0, await EmailOutboxWorker.ProcessBatchAsync(db, sender, "", now.AddMinutes(1), default));
        Assert.Single(sender.Sent);
        // Keeps failing: gives up after the maximum number of attempts.
        var later = now;
        for (var i = 0; i < EmailOutboxWorker.MaxAttempts; i++)
        {
            sender.Outcomes.Enqueue(new HttpRequestException("network down"));
            later = later.AddHours(1);
            await EmailOutboxWorker.ProcessBatchAsync(db, sender, "", later, default);
        }
        var throttled = await db.Notifications.AsNoTracking().SingleAsync(n => n.Title == "Throttled");
        Assert.Equal(EmailStatus.Failed, throttled.EmailStatus);
        Assert.Equal(EmailOutboxWorker.MaxAttempts, throttled.EmailAttempts);
    }

    [Fact]
    public async Task OutsideAddresses_GetEmailWithoutALink_StaffKeepTheLink()
    {
        await using var db = Db();
        var service = new NotificationService(db, new EmailSettings(Config), Config);
        await service.NotifyAsync(new[] { "Parent@Gmail.com", "newteacher@school.org", "t@school.org", "not-an-email", "parent@gmail.com" }, "Trip approved", "Details", "/Requests/Detail/1", default);
        await db.SaveChangesAsync();
        var rows = await db.Notifications.OrderBy(n => n.UserEmail).ToListAsync();
        Assert.Equal(new[] { "newteacher@school.org", "parent@gmail.com", "t@school.org" }, rows.Select(r => r.UserEmail).ToArray());
        Assert.Equal("", rows[1].Link);                         // outside the school: cannot sign in, so no link
        Assert.Equal("/Requests/Detail/1", rows[0].Link);       // same domain as the sender mailbox: treated as staff
        Assert.All(rows, r => Assert.Equal(EmailStatus.Pending, r.EmailStatus));
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
