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

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_uploads)) Directory.Delete(_uploads, recursive: true);
    }

    private AppDbContext Db() => new(_options);
    private static readonly Microsoft.Extensions.Configuration.IConfiguration Config =
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Mode"] = "Graph", ["Email:From"] = "noreply@school.org", ["Email:BaseUrl"] = "https://flow.school.org",
            ["AzureAd:TenantId"] = "t", ["AzureAd:ClientId"] = "c", ["AzureAd:ClientSecret"] = "s",
            ["Auth:Admins:0"] = "admin@school.org",
        }).Build();

    private readonly string _uploads = Path.Combine(Path.GetTempPath(), "schoolflow-tests-" + Guid.NewGuid().ToString("N"));

    private AttachmentService Files(AppDbContext db) => new(db,
        new AttachmentSettings(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Attachments:Path"] = _uploads, ["Attachments:MaxFileMb"] = "1" }).Build(), _uploads),
        NullLogger<AttachmentService>.Instance);

    private RunService Service(AppDbContext db, IFreshserviceClient? freshservice = null) =>
        new(db, freshservice ?? _freshservice, new NotificationService(db, new EmailSettings(Config), Config), Files(db),
            new SchoolCalendar(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            NullLogger<RunService>.Instance);

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

    [Fact]
    public async Task AdminDecidingOnSomeonesBehalf_IsAllowed_Labelled_AndAudited()
    {
        var runId = await SubmitTripAsync("Science"); // waits on principal@example.org
        await DecideAsync(runId, "nosy@school.org", admin: false, expectOk: false);   // an ordinary person cannot
        await DecideAsync(runId, "admin@school.org", admin: true);                    // an admin can
        await using var db = Db();
        Assert.Equal(RunStatus.Completed, (await db.Runs.SingleAsync(r => r.Id == runId)).Status);
        Assert.Contains(await db.RunEvents.Where(e => e.RunId == runId).ToListAsync(),
            e => e.Message.Contains("an admin acting on behalf of principal@example.org"));
        var entry = await db.AuditEntries.SingleAsync();
        Assert.Equal("approval.override-approved", entry.Action);
        Assert.Equal("admin@school.org", entry.ActorEmail);
        Assert.Contains("on behalf of principal@example.org", entry.Detail);
    }

    [Fact]
    public async Task OrdinaryApprovals_AreNotInTheAuditLog()
    {
        var runId = await SubmitTripAsync("Science");
        await DecideAsync(runId, "principal@example.org");
        await using var db = Db();
        Assert.Equal(0, await db.AuditEntries.CountAsync());
        Assert.DoesNotContain(await db.RunEvents.ToListAsync(), e => e.Message.Contains("on behalf"));
    }

    // ---- send back, resubmit, withdraw, discussion ------------------------------------------

    private async Task<ActionResult> ActAsync(int runId, string email, ApprovalAction action, string? comment = null, bool admin = false)
    {
        await using var db = Db();
        var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
        return await Service(db).DecideAsync(task.Id, email, email, admin, action, comment, default);
    }

    [Fact]
    public async Task SentBack_ThenResubmitted_ReturnsToTheSameStep_KeepingEarlierApprovals()
    {
        var chain = new Dictionary<string, object?>
        {
            ["steps"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "Team leader", ["approvers"] = new List<object?> { "lead@school.org" } } },
        };
        var runId = await SubmitTripAsync("Science", chain);
        await DecideAsync(runId, "lead@school.org");                                         // step 1 approved

        Assert.False((await ActAsync(runId, "principal@example.org", ApprovalAction.Return)).Ok);   // must say what to change
        Assert.True((await ActAsync(runId, "principal@example.org", ApprovalAction.Return, "Need 5 chaperones")).Ok);
        await using (var db = Db())
        {
            Assert.Equal(RunStatus.ChangesRequested, (await db.Runs.SingleAsync()).Status);
            Assert.Equal(0, await db.ApprovalTasks.CountAsync(t => t.Status == ApprovalStatus.Pending));
            var note = await db.Notifications.Where(n => n.UserEmail == "t@school.org").OrderBy(n => n.Id).LastAsync();
            Assert.StartsWith("Changes needed", note.Title);
            Assert.Contains("Need 5 chaperones", note.Body);

            // Only the requester can resubmit, and the answers are validated again.
            var input = TripInput("Science", (await db.Departments.SingleAsync(d => d.Name == "Science")).Id, chain);
            Assert.False((await Service(db).ResubmitAsync(runId, input, "other@school.org", "Other", default)).Result.Ok);
            input["chaperones"] = 1m;                                                        // breaks the 1-per-10 rule
            var invalid = await Service(db).ResubmitAsync(runId, input, "t@school.org", "T Teacher", default);
            Assert.False(invalid.Result.Ok);
            Assert.Contains("every 10 students", invalid.Form!.Fields["chaperones"].Errors.Single());
            Assert.Equal(RunStatus.ChangesRequested, (await db.Runs.AsNoTracking().SingleAsync()).Status);
        }
        await using (var db = Db())
        {
            var input = TripInput("Science", (await db.Departments.SingleAsync(d => d.Name == "Science")).Id, chain);
            input["chaperones"] = 5m;
            Assert.True((await Service(db).ResubmitAsync(runId, input, "t@school.org", "T Teacher", default)).Result.Ok);
        }
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync();
            Assert.Equal(RunStatus.WaitingApproval, run.Status);
            Assert.Contains("\"chaperones\":5", run.ContextJson);
            var task = await db.ApprovalTasks.SingleAsync(t => t.Status == ApprovalStatus.Pending);
            Assert.Equal("Final approval — Science", task.Title);                            // back at the step that asked, not the start
            Assert.Equal("Step 2 of 2", task.Position);
            Assert.Equal(1, await db.ApprovalTasks.CountAsync(t => t.Status == ApprovalStatus.Approved));   // the team leader is not asked again
            Assert.Equal(1, await db.ApprovalTasks.CountAsync(t => t.Status == ApprovalStatus.Returned));
            Assert.Contains(await db.RunEvents.ToListAsync(), e => e.Message.Contains("Resubmitted with changes"));
            Assert.False((await Service(db).ResubmitAsync(runId, new(), "t@school.org", "T", default)).Result.Ok);   // nothing to resubmit now
        }
        await DecideAsync(runId, "principal@example.org");
        await using var done = Db();
        Assert.Equal(RunStatus.Completed, (await done.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Requester_CanWithdraw_WhileWaiting_ButNotOnceItHasMovedOn()
    {
        var waiting = await SubmitTripAsync("Science");
        var finished = await SubmitTripAsync("Science");
        await DecideAsync(finished, "principal@example.org");
        await using var db = Db();
        Assert.False((await Service(db).WithdrawAsync(waiting, "nosy@school.org", "Nosy", default)).Ok);     // not theirs
        Assert.False((await Service(db).WithdrawAsync(finished, "t@school.org", "T", default)).Ok);          // already approved
        Assert.True((await Service(db).WithdrawAsync(waiting, "t@school.org", "T Teacher", default)).Ok);
        Assert.False((await Service(db).WithdrawAsync(waiting, "t@school.org", "T Teacher", default)).Ok);   // already withdrawn

        await using var check = Db();
        Assert.Equal(RunStatus.Cancelled, (await check.Runs.SingleAsync(r => r.Id == waiting)).Status);
        Assert.Equal(ApprovalStatus.Cancelled, (await check.ApprovalTasks.SingleAsync(t => t.RunId == waiting)).Status);
        Assert.Contains(await check.Notifications.Where(n => n.UserEmail == "principal@example.org").ToListAsync(), n => n.Title.StartsWith("Withdrawn"));
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == waiting).ToListAsync(), e => e.Message == "Withdrawn by T Teacher.");
    }

    [Fact]
    public async Task Discussion_NotifiesTheOtherPeopleInvolved_NotTheAuthor()
    {
        var runId = await SubmitTripAsync("Science");
        await using var db = Db();
        var run = await db.Runs.AsNoTracking().SingleAsync();
        var before = await db.Notifications.CountAsync();
        Assert.False((await Service(db).CommentAsync(run, "principal@example.org", "Pat", "   ", default)).Ok);
        Assert.True((await Service(db).CommentAsync(run, "principal@example.org", "Pat", "Which bus company?", default)).Ok);
        Assert.True((await Service(db).CommentAsync(run, "t@school.org", "T Teacher", "Coach USA", default)).Ok);

        var added = await db.Notifications.OrderBy(n => n.Id).Skip(before).ToListAsync();
        Assert.Equal(new[] { "t@school.org", "principal@example.org" }, added.Select(n => n.UserEmail).ToArray());
        Assert.Contains("Which bus company?", added[0].Body);
        Assert.Equal(2, await db.RunComments.CountAsync(c => c.RunId == runId));
    }

    // ---- stand-ins, reminders, escalation ------------------------------------------------

    [Fact]
    public async Task StandIn_DecidesInPlaceOfAnAbsentApprover_AndCountsAsThemWhenAllMustApprove()
    {
        await using (var db = Db())
        {
            db.Delegations.Add(new Delegation { FromEmail = "principal@example.org", ToEmail = "deputy@school.org", StartsOn = DateTime.Today.AddDays(-1), EndsOn = DateTime.Today.AddDays(3) });
            db.Delegations.Add(new Delegation { FromEmail = "admin@example.org", ToEmail = "t@school.org", StartsOn = DateTime.Today, EndsOn = DateTime.Today });   // would make the requester their own approver
            db.Delegations.Add(new Delegation { FromEmail = "admin@example.org", ToEmail = "expired@school.org", StartsOn = DateTime.Today.AddDays(-9), EndsOn = DateTime.Today.AddDays(-2) });
            await db.SaveChangesAsync();
        }
        var runId = await SubmitTripAsync("Mathematics");   // principal AND admin must both approve
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync();
            Assert.Equal("principal@example.org", task.StandsInFor("deputy@school.org"));
            Assert.True(task.CanBeDecidedBy("deputy@school.org"));
            Assert.False(task.CanBeDecidedBy("t@school.org"));
            Assert.False(task.CanBeDecidedBy("expired@school.org"));
            Assert.Contains(await db.Notifications.ToListAsync(), n => n.UserEmail == "deputy@school.org" && n.Title.StartsWith("Approval needed"));
            Assert.Contains(await db.RunEvents.ToListAsync(), e => e.Message == "principal@example.org is away, so deputy@school.org can decide in their place.");
        }
        await DecideAsync(runId, "deputy@school.org");
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync();
            Assert.Equal(ApprovalStatus.Pending, task.Status);                           // admin still owes theirs
            Assert.True(task.HasApproved("principal@example.org"));                      // counted as the person they stand in for
            Assert.True(task.IsDoneFor("deputy@school.org"));
            Assert.Contains(await db.RunEvents.ToListAsync(), e => e.Message.Contains("standing in for principal@example.org") && e.Message.Contains("(1 of 2)"));
        }
        await DecideAsync(runId, "principal@example.org", expectOk: false);              // the stand-in already signed for them
        await DecideAsync(runId, "admin@example.org");
        await using var done = Db();
        Assert.Equal(RunStatus.Completed, (await done.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Reminders_AndEscalation_FollowSchoolDays()
    {
        // Monday 2 Nov 2026; Wed 4th is a closed day.
        var created = new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc);
        var runId = await SubmitTripAsync("Science");
        await using (var db = Db())
        {
            db.ClosedDays.Add(new ClosedDay { Date = new DateTime(2026, 11, 4), Name = "Election day" });
            var task = await db.ApprovalTasks.SingleAsync();
            task.CreatedAt = created;
            Assert.Equal(WorkflowEngine.DefaultRemindDays, task.RemindDays);             // reminders are on by default
            task.EscalateDays = 4;
            task.EscalateTo = ApprovalTask.Pack(new[] { "super@school.org", "t@school.org" });
            await db.SaveChangesAsync();
        }
        async Task<(int, int)> At(DateTime utc) { await using var db = Db(); return await Service(db).FollowUpApprovalsAsync(utc, default); }
        async Task<int> Count(string email, string prefix) { await using var db = Db(); return await db.Notifications.CountAsync(n => n.UserEmail == email && n.Title.StartsWith(prefix)); }

        Assert.Equal((0, 0), await At(created.AddDays(1)));                              // Tue: 1 school day
        Assert.Equal((0, 0), await At(created.AddDays(2)));                              // Wed is closed: still 1
        Assert.Equal((1, 0), await At(created.AddDays(3)));                              // Thu: 2 school days → reminder
        Assert.Equal((0, 0), await At(created.AddDays(3).AddHours(2)));                  // not twice on the same day
        Assert.Equal(1, await Count("principal@example.org", "Reminder"));
        Assert.Equal((0, 0), await At(created.AddDays(5)));                              // Sat: Fri was only the 3rd school day
        Assert.Equal((1, 1), await At(created.AddDays(7)));                              // next Mon: 4 school days → escalate, and remind again

        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync();
            Assert.True(task.IsEscalatedTo("super@school.org"));
            Assert.False(task.IsEscalatedTo("t@school.org"));                            // never the requester
            Assert.Contains(await db.RunEvents.ToListAsync(), e => e.Message.Contains("escalated to super@school.org"));
        }
        Assert.Equal(1, await Count("super@school.org", "Escalated to you"));
        Assert.Equal(1, await Count("super@school.org", "Reminder"));                    // they are now one of the people who owe a decision
        Assert.Equal((0, 0), await At(created.AddDays(7).AddHours(1)));                  // escalates once

        Assert.True((await ActAsync(runId, "super@school.org", ApprovalAction.Approve)).Ok);
        await using var done = Db();
        Assert.Equal(RunStatus.Completed, (await done.Runs.SingleAsync()).Status);
        Assert.Contains(await done.RunEvents.ToListAsync(), e => e.Message.Contains("after the step was escalated"));
        Assert.Equal((0, 0), await Service(done).FollowUpApprovalsAsync(created.AddDays(30), default));   // nothing pending, nothing sent
    }

    [Fact]
    public async Task Reminders_StopAfterFive_AndCanBeSwitchedOff()
    {
        var created = new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc);
        await SubmitTripAsync("Science");
        await using (var db = Db()) { (await db.ApprovalTasks.SingleAsync()).CreatedAt = created; await db.SaveChangesAsync(); }
        var sent = 0;
        for (var week = 1; week <= 9; week++)
        {
            await using var db = Db();
            sent += (await Service(db).FollowUpApprovalsAsync(created.AddDays(7 * week), default)).Reminded;
        }
        Assert.Equal(5, sent);

        await SubmitTripAsync("Athletics");
        await using var off = Db();
        var quiet = await off.ApprovalTasks.OrderBy(t => t.Id).LastAsync();
        quiet.CreatedAt = created;
        quiet.RemindDays = 0;
        await off.SaveChangesAsync();
        Assert.Equal((0, 0), await Service(off).FollowUpApprovalsAsync(created.AddDays(90), default));
    }

    // ---- safety net for missed ticket events ---------------------------------------------

    private sealed class LiveFreshservice : IFreshserviceClient
    {
        private readonly SimulatedFreshserviceClient _inner = new();
        public Dictionary<long, Dictionary<string, object?>?> Tickets { get; } = new();
        public bool Down { get; set; }
        public bool IsLive => true;
        public Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default) =>
            Down ? throw new FreshserviceException("rate limited", 429) : Task.FromResult(Tickets.TryGetValue(ticketId, out var t) ? t : new Dictionary<string, object?> { ["status"] = 2m });
        public Task<Dictionary<string, object?>> CreateTicketAsync(TicketCreate ticket, CancellationToken ct = default) => _inner.CreateTicketAsync(ticket, ct);
        public Task<Dictionary<string, object?>> UpdateTicketAsync(long ticketId, TicketUpdate update, CancellationToken ct = default) => _inner.UpdateTicketAsync(ticketId, update, ct);
        public Task AddNoteAsync(long ticketId, string bodyHtml, bool isPrivate, CancellationToken ct = default) => _inner.AddNoteAsync(ticketId, bodyHtml, isPrivate, ct);
        public Task AttachFilesAsync(long ticketId, IReadOnlyList<TicketFile> files, CancellationToken ct = default) => _inner.AttachFilesAsync(ticketId, files, ct);
        public Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default) => _inner.FindRequesterAsync(email, ct);
        public Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity, Dictionary<string, object?> customFields, CancellationToken ct = default) => _inner.PlaceServiceRequestAsync(itemDisplayId, requesterEmail, quantity, customFields, ct);
        public Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default) => _inner.LookupAsync(source, query, userEmail, ct);
        public Task<string> TestConnectionAsync(CancellationToken ct = default) => _inner.TestConnectionAsync(ct);
    }

    [Fact]
    public async Task MissedTicketEvents_AreCaughtByAskingFreshserviceDirectly()
    {
        var live = new LiveFreshservice();
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        int a, b, c;
        async Task<int> Submit()
        {
            await using var db = Db();
            var workflow = await db.Workflows.SingleAsync(w => w.Name == "Technology Purchase Request");
            return (await Service(db, live).SubmitFormAsync(workflow, PurchaseInput(300), "t@school.org", "T Teacher", default)).RunId!.Value;
        }
        (a, b, c) = (await Submit(), await Submit(), await Submit());   // tickets 90001, 90002, 90003: all waiting to be resolved

        await using (var db = Db())
        {
            Assert.Equal(0, await Service(db).CheckWaitingTicketsAsync(now, default));           // simulation mode: never polls
            Assert.Equal(0, await Service(db, live).CheckWaitingTicketsAsync(now, default));     // all still open in Freshservice
            Assert.All(await db.Runs.AsNoTracking().ToListAsync(), r => Assert.Equal(now, r.LastPolledAt));
        }
        live.Tickets[90001] = new() { ["status"] = 5m };     // closed, but the webhook never arrived
        live.Tickets[90002] = null;                          // deleted in Freshservice
        await using (var db = Db())
        {
            Assert.Equal(0, await Service(db, live).CheckWaitingTicketsAsync(now.AddMinutes(5), default));   // checked too recently
            Assert.Equal(2, await Service(db, live).CheckWaitingTicketsAsync(now.AddMinutes(20), default));
        }
        await using (var db = Db())
        {
            Assert.Equal(RunStatus.Completed, (await db.Runs.SingleAsync(r => r.Id == a)).Status);
            var gone = await db.Runs.SingleAsync(r => r.Id == b);
            Assert.Equal(RunStatus.Failed, gone.Status);
            Assert.Contains("no longer exists", gone.Error);
            Assert.Equal(RunStatus.WaitingTicket, (await db.Runs.SingleAsync(r => r.Id == c)).Status);

            live.Down = true;                                                                            // an outage is not an error on the request
            Assert.Equal(0, await Service(db, live).CheckWaitingTicketsAsync(now.AddHours(2), default));
            Assert.Equal(RunStatus.WaitingTicket, (await db.Runs.AsNoTracking().SingleAsync(r => r.Id == c)).Status);
        }
    }

    // ---- export --------------------------------------------------------------------------

    [Fact]
    public async Task Export_HasOneColumnPerAnswer_AndCannotSmuggleSpreadsheetFormulas()
    {
        await using (var db = Db())
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Name == "Technology Purchase Request");
            var input = PurchaseInput(300);
            input["title"] = "=HYPERLINK(\"http://evil\",\"click\")";
            input["justification"] = "He said \"yes\",\nthen left";
            Assert.NotNull((await Service(db).SubmitFormAsync(workflow, input, "t@school.org", "T Teacher", default)).RunId);
        }
        await SubmitTripAsync("Science");
        await using var read = Db();
        var csv = RequestExport.ToCsv(await read.Runs.AsNoTracking().OrderBy(r => r.Id).ToListAsync());
        var header = csv.Split('\n')[0];
        Assert.StartsWith("\"Request #\",\"Form\",\"Status\",\"Submitted by\",\"Email\",\"Department\"", header);
        Assert.Contains("\"What do you need?\"", header);              // a purchase question
        Assert.Contains("\"Where are you going?\"", header);           // a trip question, in the same file
        Assert.DoesNotContain("\"Items\",", header);                   // section headings are not answers
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\",\"\"click\"\")\"", csv);   // neutralised and quoted
        Assert.Contains("\"He said \"\"yes\"\",\nthen left\"", csv);
        Assert.Contains("\"Projector | Hardware | 1 | 300 | 300\"", csv);  // table rows flattened, numbers left as numbers
        Assert.Contains("\"Science Museum\"", csv);

        Assert.Equal("\"-5\"", RequestExport.Cell("-5"));               // a real negative number is not text
        Assert.Equal("\"'-5 apples\"", RequestExport.Cell("-5 apples"));
        Assert.Equal("\"'@SUM(A1)\"", RequestExport.Cell("@SUM(A1)"));
        Assert.Equal("\"\"", RequestExport.Cell(null));
    }

    // ---- attachments ----------------------------------------------------------------------

    private static readonly byte[] Pdf = "%PDF-1.7 pretend quote"u8.ToArray();

    private static Microsoft.AspNetCore.Http.IFormFile Upload(string name, byte[] content) =>
        new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(content), 0, content.Length, "file", name);

    [Fact]
    public async Task Upload_StoresTheFileUnderARandomName_InsideTheUploadsFolder()
    {
        await using var db = Db();
        var result = await Files(db).SaveAsync(Upload("..\\..\\web.config\\Quote: \"final\".pdf", Pdf), 1, "t@school.org", default);
        Assert.Null(result.Error);
        var saved = result.Attachment!;
        Assert.Equal("Quote final.pdf", saved.FileName);                    // path parts and awkward characters removed
        Assert.Equal("application/pdf", saved.ContentType);
        Assert.Matches("^[0-9a-f]{32}\\.bin$", saved.StoredName);            // nothing of the uploaded name reaches the disk
        Assert.Equal(new[] { saved.StoredName }, Directory.GetFiles(_uploads).Select(Path.GetFileName).ToArray());
        Assert.Equal(Pdf, await File.ReadAllBytesAsync(Path.Combine(_uploads, saved.StoredName)));
        Assert.Null(saved.RunId);
    }

    [Theory]
    [InlineData("virus.exe", "MZ-program", "cannot be uploaded")]
    [InlineData("page.html", "<script>", "cannot be uploaded")]
    [InlineData("noextension", "%PDF-1.7", "cannot be uploaded")]
    [InlineData("renamed.pdf", "MZ this is really a program", "does not look like a real PDF")]
    [InlineData("renamed.png", "%PDF-1.7", "does not look like a real PNG")]
    [InlineData("notes.txt", "MZ\0\0binary", "does not look like a real TXT")]
    [InlineData("empty.pdf", "", "empty")]
    public async Task Upload_RejectsDangerousOrMislabelledFiles_AndLeavesNothingOnDisk(string name, string content, string expected)
    {
        await using var db = Db();
        var result = await Files(db).SaveAsync(Upload(name, System.Text.Encoding.Latin1.GetBytes(content)), 1, "t@school.org", default);
        Assert.Null(result.Attachment);
        Assert.Contains(expected, result.Error);
        Assert.Equal(0, await db.Attachments.CountAsync());
        Assert.True(!Directory.Exists(_uploads) || Directory.GetFiles(_uploads).Length == 0);
    }

    [Fact]
    public async Task Upload_EnforcesTheSizeLimit()
    {
        await using var db = Db();
        var big = new byte[1024 * 1024 + 1];
        Pdf.CopyTo(big, 0);
        var result = await Files(db).SaveAsync(Upload("big.pdf", big), 1, "t@school.org", default);
        Assert.Contains("larger than the 1 MB limit", result.Error);
    }

    [Fact]
    public async Task SubmittedFiles_BelongToTheRequest_GoToTheTicket_AndCannotBeReusedOrDeleted()
    {
        int repairId;
        string fileId;
        await using (var db = Db())
        {
            repairId = (await db.Workflows.SingleAsync(w => w.Name == "Device Repair Request")).Id;
            fileId = (await Files(db).SaveAsync(Upload("crack.pdf", Pdf), repairId, "t@school.org", default)).Attachment!.PublicId;
        }
        Dictionary<string, object?> Input(string id) => new()
        {
            ["device_other"] = "Laptop 7", ["problem"] = "Cracked screen",
            ["photos"] = new List<object?> { new Dictionary<string, object?> { ["id"] = id, ["name"] = "spoofed-name.exe", ["size"] = 1m } },
        };

        // Someone else cannot attach a file they did not upload, even knowing its ID.
        await using (var db = Db())
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Id == repairId);
            var (form, runId) = await Service(db).SubmitFormAsync(workflow, Input(fileId), "other@school.org", "Other", default);
            Assert.Null(runId);
            Assert.Contains("could not be found", form.Fields["photos"].Errors.Single());
        }

        int run;
        await using (var db = Db())
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Id == repairId);
            var (form, runId) = await Service(db).SubmitFormAsync(workflow, Input(fileId), "t@school.org", "T Teacher", default);
            Assert.True(form.IsValid, string.Join(" | ", form.Fields.SelectMany(f => f.Value.Errors)));
            run = runId!.Value;
        }
        await using (var db = Db())
        {
            Assert.Equal(run, (await db.Attachments.SingleAsync()).RunId);
            var (ticketId, files) = ((long, IReadOnlyList<TicketFile>))_freshservice.Calls.Single(c => c.Action == "attach_files").Payload;
            Assert.Equal(90001, ticketId);
            Assert.Equal("crack.pdf", files.Single().Name);                  // the server's name, not the browser's
            Assert.Equal(Pdf, files.Single().Content);
            Assert.Contains(await db.RunEvents.ToListAsync(), e => e.Message.Contains("Attached 1 file to ticket #90001: crack.pdf"));
            Assert.Contains("\"attachments\":[{", (await db.Runs.SingleAsync()).ContextJson);

            // Part of the request's record now: the uploader can no longer delete it, and it cannot ride on a second request.
            Assert.False(await Files(db).DeletePendingAsync(fileId, "t@school.org", default));
            var workflow = await db.Workflows.SingleAsync(w => w.Id == repairId);
            var (again, second) = await Service(db).SubmitFormAsync(workflow, Input(fileId), "t@school.org", "T Teacher", default);
            Assert.Null(second);
            Assert.Contains("could not be found", again.Fields["photos"].Errors.Single());
        }
    }

    [Fact]
    public async Task AbandonedUploads_AreCleanedUp_ButNotOnesInADraftOrARequest()
    {
        await using var db = Db();
        var files = Files(db);
        var old = DateTime.UtcNow.AddDays(-40);
        async Task<Attachment> Add(string name, DateTime created, int? runId = null)
        {
            var a = (await files.SaveAsync(Upload(name, Pdf), 1, "t@school.org", default)).Attachment!;
            a.CreatedAt = created;
            a.RunId = runId;
            return a;
        }
        var abandoned = await Add("abandoned.pdf", old);
        var inDraft = await Add("in-draft.pdf", old);
        var submitted = await Add("submitted.pdf", old, runId: 7);
        var recent = await Add("recent.pdf", DateTime.UtcNow.AddDays(-2));
        db.FormDrafts.Add(new FormDraft { WorkflowId = 1, OwnerEmail = "t@school.org", ValuesJson = $"{{\"photos\":[{{\"id\":\"{inDraft.PublicId}\"}}]}}", CreatedAt = old, UpdatedAt = old });
        await db.SaveChangesAsync();

        Assert.Equal(1, await files.RemoveAbandonedAsync(AttachmentCleanupWorker.KeepUnsubmittedFor, DateTime.UtcNow, default));
        var left = await db.Attachments.AsNoTracking().Select(a => a.FileName).OrderBy(n => n).ToListAsync();
        Assert.Equal(new[] { "in-draft.pdf", "recent.pdf", "submitted.pdf" }, left.ToArray());
        Assert.False(File.Exists(Path.Combine(_uploads, abandoned.StoredName)));
        Assert.True(File.Exists(Path.Combine(_uploads, submitted.StoredName)));
        Assert.Equal(3, Directory.GetFiles(_uploads).Length);
    }

    [Fact]
    public async Task MissingFileOnDisk_IsReported_NotThrown()
    {
        await using var db = Db();
        var files = Files(db);
        var saved = (await files.SaveAsync(Upload("gone.pdf", Pdf), 1, "t@school.org", default)).Attachment!;
        File.Delete(Path.Combine(_uploads, saved.StoredName)); // e.g. quarantined by antivirus
        Assert.Null(await files.OpenAsync(saved.PublicId, default));
        Assert.Null(await files.ReadAsync(saved.PublicId));
        Assert.Null(await files.OpenAsync("no-such-id", default));
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
