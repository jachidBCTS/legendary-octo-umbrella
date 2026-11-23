using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SchoolFlow.Core;
using SchoolFlow.Core.Forms;
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

    private RunService Service(AppDbContext db, IFreshserviceClient? freshservice = null, IPerDiemSource? perDiem = null, StudentScopeService? scope = null)
    {
        var calendar = new SchoolCalendar(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        return new(db, freshservice ?? _freshservice, new NotificationService(db, new EmailSettings(Config), Config), Files(db), calendar,
            NullLogger<RunService>.Instance, roster: new SampleStudentRoster(), scope: scope, perDiem: new PerDiemService(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                calendar, NullLogger<PerDiemService>.Instance, perDiem ?? new FixedPerDiem()));
    }

    /// <summary>A stand-in for GSA: $59 a day for meals and a $200 hotel limit everywhere (the rates the travel tests were written with).</summary>
    private sealed class FixedPerDiem : IPerDiemSource
    {
        public int Calls;
        public string Source => "gsa";
        public Task<SchoolFlow.Core.Forms.PerDiemArea?> GetAsync(string zip, int fiscalYear, CancellationToken ct)
        {
            Calls++;
            if (zip == "99999") throw new HttpRequestException("down");
            return Task.FromResult<SchoolFlow.Core.Forms.PerDiemArea?>(new SchoolFlow.Core.Forms.PerDiemArea("Princeton", "Mercer", "NJ", 59m,
                Enumerable.Range(1, 12).ToDictionary(m => m, m => m == 3 ? 220m : 200m), false));
        }
    }

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
            Assert.Equal(90001, run.WaitingTicketId);   // kept once the wait ends: it waited, so it reads "Completed"
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
            Assert.Empty(_freshservice.Calls);                                 // field trips no longer go to the help desk
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

    // ---- resubmitting after a send-back: route, chain and files are worked out again ------------

    [Fact]
    public async Task Resubmitted_UnderTheApprovalAmount_TakesTheNewRoute_InsteadOfFailing()
    {
        var runId = await SubmitPurchaseAsync(600);                       // $500 or more: needs the approver it names
        Assert.True((await ActAsync(runId, "boss@school.org", ApprovalAction.Return, "Can you find a cheaper one?")).Ok);
        await using (var db = Db())
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Name == "Technology Purchase Request");
            var input = PurchaseInput(300);
            input.Remove("approver_email");                                // hidden now: the form no longer asks for it
            var (form, result) = await Service(db).ResubmitAsync(runId, input, "t@school.org", "T Teacher", default);
            Assert.True(result.Ok, result.Error);
        }
        await using var check = Db();
        var run = await check.Runs.SingleAsync(r => r.Id == runId);
        Assert.Equal(RunStatus.WaitingTicket, run.Status);                 // straight to the purchasing ticket, as a $300 order would
        Assert.Null(run.Error);
        Assert.Equal(0, await check.ApprovalTasks.CountAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending));
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("route has been worked out again"));
    }

    [Fact]
    public async Task Resubmitted_WithADifferentDepartmentOrChain_RebuildsTheChain()
    {
        var chain = new Dictionary<string, object?>
        {
            ["steps"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "Team leader", ["approvers"] = new List<object?> { "lead@school.org" } } },
        };
        var runId = await SubmitTripAsync("Science", chain);
        await DecideAsync(runId, "lead@school.org");
        Assert.True((await ActAsync(runId, "principal@example.org", ApprovalAction.Return, "This is a maths trip")).Ok);
        async Task Resubmit(string department, object? withChain)
        {
            await using var db = Db();
            var input = TripInput(department, (await db.Departments.SingleAsync(d => d.Name == department)).Id, withChain);
            Assert.True((await Service(db).ResubmitAsync(runId, input, "t@school.org", "T Teacher", default)).Result.Ok);
        }

        // Same first step, new department: the team leader's approval stands; the new department's head decides.
        await Resubmit("Mathematics", chain);
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal(("Final approval — Mathematics", "Step 2 of 2", ApprovalRules.All), (task.Title, task.Position, task.Rule));
            Assert.Equal(new[] { "admin@example.org", "principal@example.org" }, task.AssigneeList.OrderBy(a => a).ToArray());
        }
        Assert.True((await ActAsync(runId, "principal@example.org", ApprovalAction.Return, "Add the nurse first")).Ok);

        // A different first step: the chain starts again, so the new approver is asked first.
        var nurse = new Dictionary<string, object?>
        {
            ["steps"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "Nurse", ["approvers"] = new List<object?> { "nurse@school.org" } } },
        };
        await Resubmit("Mathematics", nurse);
        await using var check = Db();
        var first = await check.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
        Assert.Equal(("Nurse", "Step 1 of 2"), (first.Title, first.Position));
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("starts again from the first step"));
    }

    [Fact]
    public async Task Resubmitted_WithoutAFile_DeletesThatFile()
    {
        string kept, dropped;
        int runId;
        Directory.CreateDirectory(_uploads);
        await using (var db = Db())
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Name == "Technology Purchase Request");
            foreach (var (fileId, file) in new[] { ("keep1", "quote-a.pdf"), ("drop1", "wrong.pdf") })
            {
                db.Attachments.Add(new Attachment { PublicId = fileId, StoredName = fileId + ".bin", FileName = file, Size = 10, WorkflowId = workflow.Id, OwnerEmail = "t@school.org", CreatedAt = DateTime.UtcNow });
                await File.WriteAllTextAsync(Path.Combine(_uploads, fileId + ".bin"), "x");
            }
            await db.SaveChangesAsync();
            var input = PurchaseInput(600);
            input["quotes"] = new List<object?> { new Dictionary<string, object?> { ["id"] = "keep1" }, new Dictionary<string, object?> { ["id"] = "drop1" } };
            var (form, id) = await Service(db).SubmitFormAsync(workflow, input, "t@school.org", "T Teacher", default);
            Assert.True(form.IsValid);
            runId = id!.Value;
            (kept, dropped) = ("keep1", "drop1");
        }
        Assert.True((await ActAsync(runId, "boss@school.org", ApprovalAction.Return, "Wrong quote attached")).Ok);
        await using (var db = Db())
        {
            var input = PurchaseInput(600);
            input["quotes"] = new List<object?> { new Dictionary<string, object?> { ["id"] = kept } };
            Assert.True((await Service(db).ResubmitAsync(runId, input, "t@school.org", "T Teacher", default)).Result.Ok);
        }
        await using var check = Db();
        Assert.Equal(new[] { kept }, await check.Attachments.Where(a => a.RunId == runId).Select(a => a.PublicId).ToArrayAsync());
        Assert.False(File.Exists(Path.Combine(_uploads, dropped + ".bin")));
        Assert.True(File.Exists(Path.Combine(_uploads, kept + ".bin")));
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("Removed 1 file taken off the request: wrong.pdf"));
    }

    [Fact]
    public async Task FollowUp_IsNotCrowdedOut_ByItemsThatHaveHadAllTheirReminders()
    {
        var runId = await SubmitTripAsync("Science");
        await using (var db = Db())
        {
            // 700 old items that will never be reminded again, ahead of one that is due.
            for (var i = 0; i < 700; i++)
                db.ApprovalTasks.Add(new ApprovalTask { RunId = runId, NodeId = "old" + i, Title = "Old", Assignees = ApprovalTask.Pack(new[] { "x@school.org" }),
                    RemindDays = 2, ReminderCount = 5, CreatedAt = DateTime.UtcNow.AddDays(-90) });
            await db.SaveChangesAsync();
        }
        // Put the live item after the 700 old ones, as the newest pending item would be.
        await using (var db = Db())
        {
            var real = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.NodeId == "signoff");
            db.ApprovalTasks.Remove(real);
            await db.SaveChangesAsync();
            db.ApprovalTasks.Add(new ApprovalTask { RunId = runId, NodeId = "signoff", Title = real.Title, Assignees = real.Assignees, RemindDays = 2, CreatedAt = DateTime.UtcNow.AddDays(-14) });
            await db.SaveChangesAsync();
        }
        await using var check = Db();
        var (reminded, _) = await Service(check).FollowUpApprovalsAsync(DateTime.UtcNow, default);
        Assert.Equal(1, reminded);
        Assert.Contains(await check.Notifications.ToListAsync(), n => n.UserEmail == "principal@example.org" && n.Title.StartsWith("Reminder"));
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

    // ---- work related travel template ------------------------------------------------------

    private async Task<(int RunId, Dictionary<string, object?> Context)> SubmitTravelAsync(string district, Action<Dictionary<string, object?>> change)
    {
        await using var db = Db();
        var workflow = await db.Workflows.SingleAsync(w => w.Name == "Request for Work Related Travel");
        var dept = await db.Departments.SingleAsync(d => d.Name == "Science");
        var input = new Dictionary<string, object?>
        {
            // The browser sends the defaults it was given when the form opened (today, the person's name, "1 room").
            ["request_date"] = "2027-01-15", ["employee_name"] = "T Teacher", ["meal_day_people"] = 1m, ["lodging_rooms"] = 1m,
            ["district"] = district,
            ["department"] = new Dictionary<string, object?> { ["id"] = (decimal)dept.Id, ["name"] = "Science" },
            ["event_title"] = "NJ Science Convention", ["location"] = "Princeton, NJ", ["dest_zip"] = "08540",
            ["from_date"] = "2027-03-10", ["to_date"] = "2027-03-12", ["from_time"] = "8:00 AM",
            ["paid_by"] = new List<object?> { "BOE" }, ["reason_a"] = "New lab standards",
            ["mileage_rate"] = 99m, ["meal_day_rate"] = 999m,                 // a tampered browser cannot set a fixed rate
        };
        change(input);
        var (form, runId) = await Service(db).SubmitFormAsync(workflow, input, "t@school.org", "T Teacher", default);
        Assert.True(form.IsValid, string.Join(" | ", form.Fields.SelectMany(f => f.Value.Errors.Select(e => f.Key + ": " + e))));
        return (runId!.Value, form.Values);
    }

    private async Task ApproveInTurnAsync(int runId, params (string Step, string Approver)[] steps)
    {
        foreach (var (step, approver) in steps)
        {
            await using (var db = Db())
                Assert.Equal(step, (await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending)).Title);
            await DecideAsync(runId, approver);
        }
    }

    private static readonly (string Step, string Approver)[] TechApprovals =
    {
        ("Final approval — Science", "principal@example.org"), ("Central Office Administrator approval", "builder@example.org"),
        ("Business Administrator approval", "admin@example.org"), ("Superintendent approval", "principal@example.org"),
    };

    private async Task<(FormResult? Form, ActionResult Result)> DoTaskAsync(int runId, string email, Dictionary<string, object?> values, bool admin = false)
    {
        await using var db = Db();
        var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
        return await Service(db).CompleteTaskAsync(task.Id, values, email, email, admin, default);
    }

    [Fact]
    public async Task TravelTemplate_RunsEntirelyInTheApp_FromApprovalsToReconciliation()
    {
        var (runId, values) = await SubmitTravelAsync("Bergen County Technical Schools", input =>
        {
            input["miles"] = 120m;                                             // x 0.47 = 56.40
            input["tolls"] = 12.5m;
            input["meal_first_people"] = 1m;                                   // 44.25
            input["meal_days"] = 1m; input["meal_day_people"] = 2m;            // 59 x 2 = 118.00
            input["lodging_nights"] = 2m; input["lodging_rate"] = 189m;        // x 1 room = 378.00
            input["registration"] = 250m;
        });
        Assert.Equal(0.47m, values["mileage_rate"]);
        Assert.Equal(56.40m, values["mileage_cost"]);
        Assert.Equal(0m, values["meal_last"]);                                 // nobody entered: nothing claimed
        Assert.Equal(859.15m, values["total"]);

        await ApproveInTurnAsync(runId, TechApprovals);
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.WaitingTask, run.Status);
            Assert.Null(run.TicketId);                                         // nothing goes to the help desk
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal((TaskKinds.Task, "Business office: budget, PO and Board approval"), (task.Kind, task.Title));
            Assert.Equal(new[] { "admin@example.org" }, task.AssigneeList.ToArray());   // the Technical Schools business office team
            Assert.Contains("needs Board of Education approval", task.Instructions);
            Assert.Contains(await db.Notifications.ToListAsync(), n => n.UserEmail == "admin@example.org" && n.Title.StartsWith("Task for you"));
            // A later change of rate does not alter what was submitted.
            (await db.Rates.SingleAsync(r => r.Key == "mileage")).Value = 0.70m;
            await db.SaveChangesAsync();
            Assert.Contains("\"mileage_cost\":56.4", run.ContextJson);
        }

        // It is a task, not an approval; only the team (or an admin, on the record) can do it; and it checks its fields.
        Assert.False((await ActAsync(runId, "admin@example.org", ApprovalAction.Approve)).Ok);
        Assert.False((await DoTaskAsync(runId, "t@school.org", new())).Result.Ok);
        var incomplete = await DoTaskAsync(runId, "admin@example.org", new() { ["office_po"] = "PO-7781" });
        Assert.False(incomplete.Result.Ok);
        Assert.Single(incomplete.Form!.Fields["board_decision"].Errors);       // $150 or more: the Board's decision is required
        Assert.Empty(incomplete.Form.Fields["office_budget"].Errors);         // the budget account only once travel is going ahead
        Assert.False(incomplete.Form.Fields["board_date"].Visible);
        var noBudget = await DoTaskAsync(runId, "admin@example.org", new() { ["board_decision"] = "Approved", ["board_date"] = "2027-02-20" });
        Assert.Single(noBudget.Form!.Fields["office_budget"].Errors);

        Assert.True((await DoTaskAsync(runId, "admin@example.org", new()
        {
            ["office_budget"] = "11-000-223", ["office_po"] = "PO-7781", ["board_decision"] = "Approved", ["board_date"] = "2027-02-20",
            ["total"] = 1m, ["approval"] = "x",                                // a task cannot overwrite anything but its own fields
        })).Result.Ok);

        string receiptId, reportId;
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.WaitingTask, run.Status);
            Assert.Contains("\"total\":859.15", run.ContextJson);
            Assert.Contains("\"board_date\":\"2027-02-20", run.ContextJson);
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal("After the trip: actual costs and receipts", task.Title);
            Assert.Equal(new[] { "t@school.org" }, task.AssigneeList.ToArray());   // back to the employee
            Assert.Equal(0, task.RemindDays);
            var approved = await db.Notifications.SingleAsync(n => n.UserEmail == "t@school.org" && n.Title.StartsWith("Travel approved:"));
            Assert.Contains("PO-7781", approved.Body);

            // The employee uploads receipts and the report while doing the task.
            var receipt = new Attachment { PublicId = "rc1", StoredName = "rc1.bin", FileName = "hotel.pdf", Size = 1200, WorkflowId = run.WorkflowId, OwnerEmail = "t@school.org", CreatedAt = DateTime.UtcNow };
            var report = new Attachment { PublicId = "pd1", StoredName = "pd1.bin", FileName = "pd-report.docx", Size = 900, WorkflowId = run.WorkflowId, OwnerEmail = "t@school.org", CreatedAt = DateTime.UtcNow };
            db.Attachments.AddRange(receipt, report);
            await db.SaveChangesAsync();
            (receiptId, reportId) = (receipt.PublicId, report.PublicId);
        }
        object File(string id) => new List<object?> { new Dictionary<string, object?> { ["id"] = id } };
        var noReport = await DoTaskAsync(runId, "t@school.org", new() { ["actual_total"] = 812.4m, ["receipts"] = File(receiptId) });
        Assert.Single(noReport.Form!.Fields["pd_report"].Errors);
        Assert.True((await DoTaskAsync(runId, "t@school.org", new() { ["actual_total"] = 812.4m, ["receipts"] = File(receiptId), ["pd_report"] = File(reportId) })).Result.Ok);

        await using (var db = Db())
        {
            Assert.All(await db.Attachments.Where(a => a.PublicId == receiptId || a.PublicId == reportId).ToListAsync(), a => Assert.Equal(runId, a.RunId));
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal("Business office: reconcile travel costs", task.Title);
            Assert.Contains("Actual: $812.40", task.Instructions);
        }
        Assert.True((await DoTaskAsync(runId, "admin@example.org", new() { ["reimbursement"] = 812.4m })).Result.Ok);

        await using var done = Db();
        var finished = await done.Runs.SingleAsync(r => r.Id == runId);
        await new DefinitionStore().FillAsync(done, finished);
        Assert.Equal(RunStatus.Completed, finished.Status);
        Assert.Contains(await done.Notifications.Where(n => n.UserEmail == "t@school.org").ToListAsync(), n => n.Title.StartsWith("Travel reconciled"));
        Assert.Equal(3, await done.ApprovalTasks.CountAsync(t => t.RunId == runId && t.Kind == TaskKinds.Task && t.Status == ApprovalStatus.Done));
        var csv = RequestExport.ToCsv(new[] { finished });
        Assert.Contains("\"PO # issued\"", csv.Split('\n')[0]);            // what tasks collected is exported too
        Assert.Contains("\"PO-7781\"", csv);
        Assert.Contains("\"hotel.pdf\"", csv);
    }

    [Fact]
    public async Task TravelTemplate_BoardNotApproved_EndsTheRequest()
    {
        var (runId, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 400m);
        await ApproveInTurnAsync(runId, TechApprovals);
        Assert.True((await DoTaskAsync(runId, "admin@example.org", new() { ["office_budget"] = "11-000-223", ["board_decision"] = "Not approved", ["office_notes"] = "Over the annual limit." })).Result.Ok);
        await using var done = Db();
        Assert.Equal(RunStatus.Rejected, (await done.Runs.SingleAsync(r => r.Id == runId)).Status);
        var mine = await done.Notifications.Where(n => n.UserEmail == "t@school.org").ToListAsync();
        Assert.Contains(mine, n => n.Title.StartsWith("Travel not approved") && n.Body.Contains("Over the annual limit."));
        Assert.DoesNotContain(mine, n => n.Title.StartsWith("Travel approved:"));
    }

    [Fact]
    public async Task Teams_AreResolvedWhenAStepIsReached_AndAnEmptyOrMissingTeamStopsTheRequest()
    {
        await using (var db = Db())
        {
            (await db.Teams.SingleAsync(t => t.Name == "Superintendent")).Members = ApprovalTask.Pack(new[] { "super@school.org", "deputy@school.org" });
            (await db.Teams.SingleAsync(t => t.Name == "Business Administrator")).Members = "";
            await db.SaveChangesAsync();
        }
        var (runId, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 40m);
        await ApproveInTurnAsync(runId, TechApprovals[0], TechApprovals[1]);
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.Contains("the team “Business Administrator” has nobody in it yet", run.Error);
            (await db.Teams.SingleAsync(t => t.Name == "Business Administrator")).Members = ApprovalTask.Pack(new[] { "admin@example.org" });
            await db.SaveChangesAsync();
        }
        var engine = new WorkflowEngine(new SimulatedFreshserviceClient())
        {
            Teams = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["Front Office"] = new[] { "A@school.org", "b@school.org" } },
        };
        Assert.Equal(new[] { "a@school.org", "b@school.org", "c@school.org" }, engine.ResolvePeople("[front office], c@school.org; b@school.org", "Step").ToArray());
        Assert.Contains("no team called “Nobody”", Assert.Throws<SchoolFlow.Core.Expressions.ExpressionException>(() => engine.ResolvePeople("[Nobody]", "Step")).Message);
    }

    [Fact]
    public async Task FieldTrip_WithABus_IsBookedByTheTransportationTeam_InTheApp()
    {
        int runId;
        await using (var db = Db())
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Name == "Field Trip Request");
            var dept = await db.Departments.SingleAsync(d => d.Name == "Science");
            var input = TripInput("Science", dept.Id, null);
            input["bus_needed"] = true;
            input["bus_cost"] = 450m;
            var (form, id) = await Service(db).SubmitFormAsync(workflow, input, "t@school.org", "T Teacher", default);
            Assert.True(form.IsValid);
            runId = id!.Value;
        }
        await DecideAsync(runId, "principal@example.org");
        await using (var db = Db())
        {
            Assert.Equal(RunStatus.WaitingTask, (await db.Runs.SingleAsync(r => r.Id == runId)).Status);
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal(("Transportation: book the bus", TaskKinds.Task), (task.Title, task.Kind));
            Assert.Equal(new[] { "admin@example.org" }, task.AssigneeList.ToArray());
            Assert.Contains("Students: 40, chaperones: 4", task.Instructions);
            var opened = await Service(db).EvaluateTaskAsync(task.Id, new Dictionary<string, object?>(), "admin@example.org", false, applyDefaults: true, default);
            Assert.Equal(450m, opened!.Values["bus_final_cost"]);   // starts from the quote on the request
        }
        Assert.False((await DoTaskAsync(runId, "admin@example.org", new() { ["bus_company"] = "Coach USA" })).Result.Ok);   // pick-up time is required
        Assert.True((await DoTaskAsync(runId, "admin@example.org", new() { ["bus_company"] = "Coach USA", ["pickup_time"] = "7:45 AM", ["bus_confirmation"] = "C-118" })).Result.Ok);

        await using var done = Db();
        Assert.Equal(RunStatus.Completed, (await done.Runs.SingleAsync(r => r.Id == runId)).Status);
        var told = await done.Notifications.SingleAsync(n => n.UserEmail == "t@school.org" && n.Title.StartsWith("Your trip to"));
        Assert.Contains("Your bus is booked with Coach USA, picking up at 07:45 (confirmation C-118).", told.Body);
        Assert.Empty(_freshservice.Calls);
    }

    [Fact]
    public async Task AfterTheTrip_IsDueThreeWeeksAfterReturning_AndTheBusinessOfficeIsToldIfItIsLate()
    {
        var (runId, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 40m);   // under $150: no Board
        await ApproveInTurnAsync(runId, TechApprovals);
        Assert.True((await DoTaskAsync(runId, "admin@example.org", new() { ["office_budget"] = "11-000-223" })).Result.Ok);
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal("After the trip: actual costs and receipts", task.Title);
            Assert.Equal(new DateTime(2027, 4, 2), task.DueOn);                // returned Mar 12 + 21 days
            Assert.Equal(new[] { "admin@example.org" }, task.DueNotifyList.ToArray());   // that district's business office
            Assert.Contains("Complete this by Friday, Apr 2, 2027", task.Instructions);
        }
        async Task<int> At(int month, int day) { await using var db = Db(); return (await Service(db).FollowUpApprovalsAsync(new DateTime(2027, month, day, 15, 0, 0, DateTimeKind.Utc), default)).Reminded; }
        async Task<List<Notification>> Sent() { await using var db = Db(); return await db.Notifications.Where(n => n.Title.Contains("After the trip")).OrderBy(n => n.Id).ToListAsync(); }

        Assert.Equal(0, await At(3, 25));                                      // well before: nothing
        Assert.Equal(1, await At(3, 30));                                      // 3 days before: a heads-up to the employee only
        Assert.Equal(0, await At(3, 31));                                      // once
        var soon = Assert.Single(await Sent());
        Assert.Equal(("t@school.org", "Due Apr 2: After the trip: actual costs and receipts"), (soon.UserEmail, soon.Title.Split(" — ")[0]));

        Assert.Equal(1, await At(4, 2));                                       // the due date: overdue
        var overdue = (await Sent()).Skip(1).ToList();
        Assert.Equal(new[] { "t@school.org", "admin@example.org" }, overdue.Select(n => n.UserEmail).ToArray());
        Assert.All(overdue, n => Assert.StartsWith("Overdue:", n.Title));
        Assert.Contains("waiting for t@school.org", overdue[1].Body);
        Assert.Equal(0, await At(4, 9));                                       // told once, not every hour
        await using var check = Db();
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("overdue (was due Apr 2, 2027)"));
    }

    [Fact]
    public async Task WithDistrictDomainsSet_AnApproverEmailField_MustBeADistrictAddress()
    {
        var domains = new DomainPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:AllowedDomains:0"] = "school.org" }).Build());
        await using var db = Db();
        var service = new RunService(db, _freshservice, new NotificationService(db, new EmailSettings(Config), Config), Files(db),
            new SchoolCalendar(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            NullLogger<RunService>.Instance, domains);
        var workflow = await db.Workflows.SingleAsync(w => w.Name == "Technology Purchase Request");

        var outside = PurchaseInput(600);
        outside["approver_email"] = "boss@gmail.com";
        var (form, id) = await service.SubmitFormAsync(workflow, outside, "t@school.org", "T Teacher", default);
        Assert.Null(id);
        Assert.Contains("use a district address (@school.org)", form.Fields["approver_email"].Errors.Single());

        var small = PurchaseInput(100);                       // under $500 the field is hidden and not used, so it is not checked
        small["approver_email"] = "boss@gmail.com";
        Assert.NotNull((await service.SubmitFormAsync(workflow, small, "t@school.org", "T Teacher", default)).RunId);
        Assert.NotNull((await service.SubmitFormAsync(workflow, PurchaseInput(600), "t@school.org", "T Teacher", default)).RunId);   // boss@school.org
        Assert.Contains("approver_email", DistrictAddresses.KeysChoosingPeople(Json.ParseDefinition(workflow.PublishedJson!)));

        // Notification steps may still email outside addresses (asked for); approvers and assignees may not.
        var engine = new WorkflowEngine(new SimulatedFreshserviceClient()) { AllowedDomains = new[] { "school.org" } };
        Assert.Equal(new[] { "vendor@acme.com" }, engine.ResolvePeople("vendor@acme.com", "Tell the vendor").ToArray());
        Assert.Contains("'vendor@acme.com' is not a district address",
            Assert.Throws<SchoolFlow.Core.Expressions.ExpressionException>(() => engine.ResolveActingPeople("lead@school.org, vendor@acme.com", "Approval")).Message);
    }

    [Fact]
    public async Task TeamCheck_ReadsOnlyTheWhoSettings_SoBracketsInMessagesAreFine()
    {
        await using var db = Db();
        var def = Json.ParseDefinition(SeedData.Templates.Single(t => t.Name == "Travel").Json);
        def.Flow.Nodes.Single(n => n.Id == "tell").Config["subject"] = "[Action needed] Travel approved: {{event_title}}";
        def.Flow.Nodes.Single(n => n.Id == "tell").Config["message"] = "Paid by {{JOIN(paid_by, ', ')}}. [See the request]";
        def.Flow.Nodes.Single(n => n.Id == "board_ok").Config["expression"] = "IN(board_decision, [\"Approved\"]) || total < 150";
        Assert.Empty(await TeamCheck.IssuesAsync(db, def));
        def.Flow.Nodes.Single(n => n.Id == "tell").Config["to"] = "{{submitter.email}}, [Payroll]";   // a "who" setting: checked
        Assert.Contains(await TeamCheck.IssuesAsync(db, def), i => i.Message.Contains("no team called “Payroll”"));
    }

    [Fact]
    public async Task OverdueNotice_LetsItsRecipientsOpenTheRequest()
    {
        var runId = await SubmitTripAsync("Science");
        await using var db = Db();
        var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId);
        task.DueNotify = ApprovalTask.Pack(new[] { "office@school.org" });
        await db.SaveChangesAsync();
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
        var office = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim("preferred_username", "office@school.org"), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, "office@school.org") }, "test"));
        Assert.False(await new AccessService(db).CanViewRunAsync(office, run));    // not before they have been told
        task.OverdueSentAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.True(await new AccessService(db).CanViewRunAsync(office, run));
    }

    [Fact]
    public async Task Publishing_IsRefused_WhileATeamAStepNamesIsMissingOrEmpty()
    {
        await using var db = Db();
        (await db.Teams.SingleAsync(t => t.Name == "Superintendent")).Members = "";
        await db.SaveChangesAsync();
        var def = Json.ParseDefinition(SeedData.Templates.Single(t => t.Name == "Travel").Json);
        def.Flow.Nodes.Single(n => n.Id == "business").Config["approvers"] = "[Business Administrator], [Nobody Here]";
        var issues = await TeamCheck.IssuesAsync(db, def);
        Assert.All(issues, i => Assert.True(i.IsError));
        Assert.Equal(new[] { "business", "superintendent" }, issues.Select(i => i.Target).OrderBy(t => t).ToArray());
        Assert.Contains(issues, i => i.Message.Contains("no team called “Nobody Here”"));
        Assert.Contains(issues, i => i.Message.Contains("the team “Superintendent” has nobody in it yet"));
        Assert.Empty(await TeamCheck.IssuesAsync(db, Json.ParseDefinition(SeedData.Templates.Single(t => t.Name == "Purchase").Json)));   // names no team
    }

    // ---- student information ---------------------------------------------------------------

    private async Task<Workflow> StudentFormAsync()
    {
        await using var db = Db();
        var def = new WorkflowDefinition
        {
            Form = { Fields =
            {
                new FieldDef { Key = "student", Label = "Student", Type = FieldTypes.Student, Required = true },
                new FieldDef { Key = "reason", Label = "Reason", Type = "text" },
                new FieldDef { Key = "label", Label = "Label", Type = "calculated", Formula = "student.last & ' (' & student.grade & ')'" },
            } },
            Flow = { Nodes = { new NodeDef { Id = "start", Type = NodeTypes.Start }, new NodeDef { Id = "end", Type = NodeTypes.End } }, Edges = { new EdgeDef { From = "start", To = "end" } } },
        };
        var json = Json.Serialize(def);
        var workflow = new Workflow { Name = "Early dismissal", DraftJson = json, PublishedJson = json, PublishedVersion = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Workflows.Add(workflow);
        // The roster itself is the district's (read live); the example roster has Maya Okafor (20451) and Leo Okon (20452).
        db.Users.Add(new AppUser { Email = "nurse@school.org", Name = "N Nurse", StudentAccess = true });
        await db.SaveChangesAsync();
        return workflow;
    }

    [Fact]
    public async Task StudentField_NeedsTheAccessFlag_AndTakesItsDetailsFromTheRoster()
    {
        var workflow = await StudentFormAsync();
        // What a tampered browser might send: a real ID with invented details.
        Dictionary<string, object?> Pick(string id) => new()
        {
            ["student"] = new Dictionary<string, object?> { ["id"] = id, ["name"] = "Someone Else", ["grade"] = "12", ["school"] = "Nowhere" },
        };
        await using var db = Db();
        var (refused, none) = await Service(db).SubmitFormAsync(workflow, Pick("20451"), "t@school.org", "T Teacher", default);
        Assert.Null(none);
        Assert.Contains("do not have access to student information", refused.Fields["student"].Errors.Single());
        Assert.Null(refused.Values["student"]);

        var (unknown, none2) = await Service(db).SubmitFormAsync(workflow, Pick("99999"), "nurse@school.org", "N Nurse", default);
        Assert.Null(none2);
        Assert.Contains("was not found", unknown.Fields["student"].Errors.Single());

        var (form, runId) = await Service(db).SubmitFormAsync(workflow, Pick("20451"), "nurse@school.org", "N Nurse", default);
        Assert.NotNull(runId);
        var student = Assert.IsType<Dictionary<string, object?>>(form.Values["student"]);
        Assert.Equal("Okafor, Maya", student["name"]);                 // the roster's, not the browser's
        Assert.Equal("10", student["grade"]);
        Assert.Equal("Teterboro", student["school"]);
        Assert.Equal(new[] { "id", "name", "first", "last", "school", "grade", "academy" }, student.Keys.ToArray());   // the six agreed facts (name is first + last) and nothing more
        Assert.Equal("Okafor (10)", form.Values["label"]);

        // Only the student number is stored (owner's rule); nothing else about the student, nor what was worked out from it.
        var runs = await db.Runs.AsNoTracking().ToListAsync();
        await new DefinitionStore().FillAsync(db, runs);
        var stored = runs.Single().ContextJson;
        Assert.Contains("\"student\":{\"id\":\"20451\"}", stored);
        foreach (var detail in new[] { "Okafor", "Maya", "Teterboro", "Aerospace", "label" }) Assert.DoesNotContain(detail, stored);

        // Someone with the flag sees the student, looked up again from the roster (with what is worked out from it).
        var context = Json.ToValues(stored);
        var calendar = new SchoolCalendar(db, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        await new StudentAnswers(new SampleStudentRoster(), calendar).RestoreAsync(Json.ParseDefinition(runs.Single().DefinitionJson), context, default);
        Assert.Equal("Okafor (10)", context["label"]);
        Assert.Contains("Okafor, Maya · ID 20451 · Teterboro · Grade 10 · Aerospace",
            RequestExport.ToCsv(runs, includeStudents: true, new Dictionary<int, Dictionary<string, object?>> { [runs.Single().Id] = context }));
        // A student who has left the roster still shows by number.
        var gone = Json.ToValues(stored);
        await new StudentAnswers(new NoStudentRoster(), calendar).RestoreAsync(Json.ParseDefinition(runs.Single().DefinitionJson), gone, default);
        Assert.Equal("ID 20451 (not on the current student roster)", Ui.Display(gone["student"], FieldTypes.Student, null, null));
        var masked = RequestExport.ToCsv(runs, includeStudents: false);
        Assert.Contains("\"(restricted)\"", masked);
        Assert.DoesNotContain("Maya", masked);
        Assert.DoesNotContain("Okafor", masked);                         // nor the calculated field worked out from the student
        Assert.True(await new AccessService(db).CanViewStudentsAsync("nurse@school.org"));
        Assert.False(await new AccessService(db).CanViewStudentsAsync("t@school.org"));
        Assert.False(await new AccessService(db).CanViewStudentsAsync("admin@example.org"));   // being an admin is not enough

        // Never offered when someone starts from a previous request.
        var copied = SchoolFlow.Core.Forms.FormCopy.Extract(Json.ParseDefinition(workflow.PublishedJson!).Form, Json.ToValues(runs.Single().ContextJson));
        Assert.DoesNotContain("student", copied.Keys);
    }

    [Fact]
    public async Task TravelTemplate_SpecialServices_Under150_TakesItsOwnRoute_AndSkipsTheBoard()
    {
        // A day trip: no hotel and no full meal days, so rooms and persons can be 0.
        var (runId, values) = await SubmitTravelAsync("Bergen County Special Services", input => { input["registration"] = 95m; input["parking"] = 20m; input["budget_account"] = "20-218-200"; input["lodging_rooms"] = 0m; input["meal_day_people"] = 0m; });
        Assert.Equal(115m, values["total"]);
        await ApproveInTurnAsync(runId, ("Final approval — Science", "principal@example.org"), ("Central Office Administrator approval", "admin@example.org"),
            ("Business Administrator approval", "admin@example.org"), ("Superintendent approval", "principal@example.org"));
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal(new[] { "builder@example.org" }, task.AssigneeList.ToArray());     // the Special Services business office
            Assert.Contains("not required", task.Instructions);
            // The task opens with the budget account the employee gave, and does not ask about the Board.
            var opened = await Service(db).EvaluateTaskAsync(task.Id, new Dictionary<string, object?>(), "builder@example.org", false, applyDefaults: true, default);
            Assert.Equal("20-218-200", opened!.Values["office_budget"]);
            Assert.False(opened.Fields["board_decision"].Visible);
            Assert.Null(await Service(db).EvaluateTaskAsync(task.Id, new Dictionary<string, object?>(), "t@school.org", false, applyDefaults: true, default));   // not theirs to see
        }
        Assert.True((await DoTaskAsync(runId, "builder@example.org", new() { ["office_budget"] = "20-218-200" })).Result.Ok);
        await using (var db = Db())
        {
            Assert.Equal(RunStatus.WaitingTask, (await db.Runs.SingleAsync(r => r.Id == runId)).Status);
            Assert.Contains(await db.Notifications.ToListAsync(), n => n.UserEmail == "t@school.org" && n.Title.StartsWith("Travel approved:"));

            var workflow = await db.Workflows.SingleAsync(w => w.Name == "Request for Work Related Travel");
            var (form, id) = await Service(db).SubmitFormAsync(workflow, new Dictionary<string, object?>
            {
                ["request_date"] = "2027-01-15", ["employee_name"] = "T Teacher",
                ["from_date"] = "2027-03-12", ["to_date"] = "2027-03-10", ["paid_by"] = new List<object?> { "Sponsor" }, ["misc"] = 30m,
            }, "t@school.org", "T Teacher", default);
            Assert.Null(id);
            Assert.Contains("before the start date", form.Fields["to_date"].Errors.Single());
            Assert.Single(form.Fields["sponsor_name"].Errors);          // a sponsor must be named
            Assert.Single(form.Fields["misc_description"].Errors);      // "be specific"
            Assert.Single(form.Fields["reason_a"].Errors);
        }
    }

    [Fact]
    public async Task TravelTemplate_RefusesToCostAnythingWithARateThatHasNotBeenSet()
    {
        await using (var db = Db())
        {
            (await db.Rates.SingleAsync(r => r.Key == "mileage")).Value = 0m;
            await db.SaveChangesAsync();
            var workflow = await db.Workflows.SingleAsync(w => w.Name == "Request for Work Related Travel");
            var (form, id) = await Service(db).SubmitFormAsync(workflow, new Dictionary<string, object?> { ["miles"] = 120m, ["meal_days"] = 2m }, "t@school.org", "T", default);
            Assert.Null(id);
            Assert.Contains("mileage rate has not been set", form.Fields["miles"].Errors.Single());
            Assert.Contains("enter the destination ZIP code", form.Fields["meal_days"].Errors.Single());   // meals come from GSA, by ZIP
        }
    }

    [Fact]
    public void TravelTemplate_OnARealSite_IsADraftWithEmptyTeams_AndNeedsNoHelpDesk()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options))
        {
            db.Database.EnsureCreated();
            SeedData.EnsureStarterWorkflows(db);                      // as on a real, new site: no example people or amounts
            var travel = db.Workflows.Single(w => w.Name == "Request for Work Related Travel");
            Assert.Null(travel.PublishedJson);                        // published by an admin once the teams have people
            Assert.Equal(5, travel.Id);                               // after the four existing starters
            Assert.Empty(DefinitionValidator.Validate(Json.ParseDefinition(travel.DraftJson)).Where(i => i.IsError));
            Assert.Equal(7, db.Teams.Count());
            Assert.Null(db.Workflows.Single(w => w.Name == "Field Trip Request").PublishedJson);   // waits for its Transportation team
            Assert.All(db.Teams.ToList(), t => Assert.Equal("", t.Members));
            // Only mileage is the district's to set; meals and hotels come from GSA, with GSA's standard rate kept as a fallback.
            Assert.Equal(new[] { ("gsa_standard_lodging", 110m), ("gsa_standard_meals", 68m), ("mileage", 0m) },
                db.Rates.OrderBy(r => r.Key).Select(r => new { r.Key, r.Value }).AsEnumerable().Select(r => (r.Key, r.Value)).ToArray());
        }

        var json = SeedData.Templates.Single(t => t.Name == "Travel").Json;
        Assert.False(Json.ParseDefinition(json).UsesHelpDesk());                                   // stays in house
        Assert.False(Json.ParseDefinition(SeedData.Templates.Single(t => t.Name == "FieldTrip").Json).UsesHelpDesk());
        Assert.True(Json.ParseDefinition(SeedData.Templates.Single(t => t.Name == "Purchase").Json).UsesHelpDesk());   // technology requests go to the help desk

        var def = Json.ParseDefinition(json);
        var sheet = def.Form.Fields.Single(f => f.Type == FieldTypes.Worksheet);
        sheet.Rows!.Add(new WorksheetRow { Description = "Again {miles} and {nowhere} and {documents}", Cost = "total" });
        // A task may not reuse a key the form or another step already has, or ask for a kind of field it cannot.
        var office = def.Flow.Nodes.Single(n => n.Id == "office");
        office.Fields!.Add(new FieldDef { Key = "total", Label = "Total again", Type = "currency" });
        office.Fields.Add(new FieldDef { Key = "who", Label = "Student", Type = FieldTypes.Student });
        def.Flow.Nodes.Single(n => n.Id == "reconcile").Fields!.Add(new FieldDef { Key = "office_po", Label = "PO", Type = "text" });
        var messages = string.Join("\n", DefinitionValidator.Validate(def).Where(i => i.IsError).Select(i => i.Message));
        Assert.Contains("'miles' is used more than once", messages);
        Assert.Contains("'nowhere', which is not a field", messages);
        Assert.Contains("'documents' cannot go inside a worksheet", messages);
        Assert.Contains("the key 'total' is already used", messages);
        Assert.Contains("the key 'office_po' is already used", messages);
        Assert.Contains("“Student” is a kind of field a task cannot ask for", messages);
    }

    // ---- retention -----------------------------------------------------------------------

    [Fact]
    public async Task Retention_DeletesFinishedRequestsAfterSevenYears_ButNeverOpenOnes_OrTheAuditLog()
    {
        var now = new DateTime(2034, 6, 1, 3, 0, 0, DateTimeKind.Utc);
        var oldDone = await SubmitTripAsync("Science");
        var oldWaiting = await SubmitTripAsync("Science");
        var recentDone = await SubmitTripAsync("Science");
        await DecideAsync(oldDone, "principal@example.org");
        await DecideAsync(recentDone, "principal@example.org");
        Directory.CreateDirectory(_uploads);
        var oldFile = Path.Combine(_uploads, "old.bin");
        var keptFile = Path.Combine(_uploads, "kept.bin");
        await File.WriteAllTextAsync(oldFile, "x");
        await File.WriteAllTextAsync(keptFile, "x");
        await using (var db = Db())
        {
            (await db.Runs.SingleAsync(r => r.Id == oldDone)).UpdatedAt = now.AddYears(-7).AddDays(-1);
            (await db.Runs.SingleAsync(r => r.Id == oldWaiting)).UpdatedAt = now.AddYears(-9);     // still waiting for approval
            (await db.Runs.SingleAsync(r => r.Id == recentDone)).UpdatedAt = now.AddYears(-7).AddDays(1);
            db.Attachments.Add(new Attachment { PublicId = "old", StoredName = "old.bin", FileName = "a.pdf", RunId = oldDone, OwnerEmail = "t@school.org", CreatedAt = now.AddYears(-8) });
            db.Attachments.Add(new Attachment { PublicId = "kept", StoredName = "kept.bin", FileName = "b.pdf", RunId = recentDone, OwnerEmail = "t@school.org", CreatedAt = now.AddYears(-8) });
            db.RunComments.Add(new RunComment { RunId = oldDone, AuthorEmail = "t@school.org", AuthorName = "T", Body = "old", CreatedAt = now.AddYears(-8) });
            foreach (var n in await db.Notifications.ToListAsync()) n.CreatedAt = now.AddYears(-8);
            db.Notifications.Add(new Notification { UserEmail = "t@school.org", Title = "Recent", CreatedAt = now.AddDays(-3) });
            db.FormDrafts.Add(new FormDraft { WorkflowId = 4, OwnerEmail = "t@school.org", CreatedAt = now.AddYears(-8), UpdatedAt = now.AddYears(-8) });
            db.FormDrafts.Add(new FormDraft { WorkflowId = 4, OwnerEmail = "t@school.org", CreatedAt = now.AddYears(-8), UpdatedAt = now.AddDays(-1) });
            db.AuditEntries.Add(new AuditEntry { At = now.AddYears(-30), Action = "role.changed", Target = "Someone" });
            await db.SaveChangesAsync();
        }
        RetentionService Retention(AppDbContext db, string? years = null) => new(db, Files(db),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Retention:Years"] = years }).Build());

        await using (var db = Db())
        {
            Assert.Equal(7, Retention(db).Years);                                   // the default is the owner's policy
            Assert.False((await Retention(db, "0").PurgeAsync(now, default)).Any);  // 0 = keep everything
            Assert.Equal(3, await db.Runs.CountAsync());

            var result = await Retention(db).PurgeAsync(now, default);
            Assert.Equal((1, 1, 1), (result.Requests, result.Files, result.Drafts));
            Assert.True(result.Notifications > 0);
        }
        await using (var db = Db())
        {
            Assert.Equal(new[] { oldWaiting, recentDone }, await db.Runs.OrderBy(r => r.Id).Select(r => r.Id).ToArrayAsync());
            Assert.Equal(0, await db.RunEvents.CountAsync(e => e.RunId == oldDone));
            Assert.Equal(0, await db.ApprovalTasks.CountAsync(t => t.RunId == oldDone));
            Assert.Equal(0, await db.RunComments.CountAsync());
            Assert.True(await db.RunEvents.AnyAsync(e => e.RunId == recentDone));
            Assert.True(await db.ApprovalTasks.AnyAsync(t => t.RunId == oldWaiting && t.Status == ApprovalStatus.Pending));
            Assert.Equal("kept", (await db.Attachments.SingleAsync()).PublicId);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(keptFile));
            Assert.Equal("Recent", (await db.Notifications.SingleAsync()).Title);
            Assert.Equal(1, await db.FormDrafts.CountAsync());

            var audit = await db.AuditEntries.OrderBy(a => a.At).ToListAsync();
            Assert.Equal(new[] { "role.changed", "retention.purged" }, audit.Select(a => a.Action).ToArray());   // 30-year-old entry is still there
            Assert.Contains("Deleted 1 request(s) and 1 file(s)", audit[1].Detail);

            Assert.False((await Retention(db).PurgeAsync(now, default)).Any);       // nothing left to do, and no empty audit entry
            Assert.Equal(2, await db.AuditEntries.CountAsync());
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
        var exported = await read.Runs.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        await new DefinitionStore().FillAsync(read, exported);
        var csv = RequestExport.ToCsv(exported);
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

    // ---- change tracking, approval restarts, task send-back, passing a step on ----------------

    private async Task<(FormResult? Form, ActionResult Result)> ResubmitAsync(int runId, Dictionary<string, object?> input)
    {
        await using var db = Db();
        return await Service(db).ResubmitAsync(runId, input, "t@school.org", "T Teacher", default);
    }

    private static Dictionary<string, object?> PreviousOf(WorkflowRun run) =>
        Json.ToValues(run.ContextJson).GetValueOrDefault("_previous") as Dictionary<string, object?> ?? new();

    [Fact]
    public async Task Resubmit_RecordsWhichAnswersChanged_AndHowTheyReadBefore()
    {
        var runId = await SubmitTripAsync("Science");
        Assert.True((await ActAsync(runId, "principal@example.org", ApprovalAction.Return, "Wrong place")).Ok);
        int deptId;
        await using (var db = Db()) deptId = (await db.Departments.SingleAsync(d => d.Name == "Science")).Id;
        var input = TripInput("Science", deptId, null);
        input["destination"] = "Planetarium";
        Assert.True((await ResubmitAsync(runId, input)).Result.Ok);

        await using var check = Db();
        var run = await check.Runs.SingleAsync(r => r.Id == runId);
        Assert.Equal("Science Museum", PreviousOf(run)["destination"]);
        Assert.Single(PreviousOf(run));                                      // only what changed
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(),
            e => e.Message == "Resubmitted with changes by T Teacher (adjusted: Where are you going?).");
        Assert.Equal(RunStatus.WaitingApproval, run.Status);                 // a different place does not restart anything
    }

    [Fact]
    public async Task Resubmit_WithAHigherCost_AfterApprovals_StartsTheApprovalsAgain()
    {
        var (runId, values) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await ApproveInTurnAsync(runId, TechApprovals[0], TechApprovals[1]);
        Assert.True((await ActAsync(runId, "admin@example.org", ApprovalAction.Return, "Add the hotel")).Ok);
        var input = new Dictionary<string, object?>(values) { ["registration"] = 400m };
        Assert.True((await ResubmitAsync(runId, input)).Result.Ok);

        await using var db = Db();
        var run = await db.Runs.SingleAsync(r => r.Id == runId);
        Assert.Equal("Final approval — Science", (await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending)).Title);
        Assert.DoesNotContain("\"approvals\"", run.ContextJson);
        Assert.Contains(await db.RunEvents.Where(e => e.RunId == runId).ToListAsync(),
            e => e.Message.Contains("approvals have been restarted from the beginning"));
        Assert.Equal("$100.00", PreviousOf(run)["registration"]);
    }

    [Fact]
    public async Task Resubmit_WithALowerCost_KeepsEarlierApprovals_UnlessTheApproverAskedToStartAgain()
    {
        var (runId, values) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await ApproveInTurnAsync(runId, TechApprovals[0], TechApprovals[1]);
        Assert.True((await ActAsync(runId, "admin@example.org", ApprovalAction.Return, "Cheaper please")).Ok);
        Assert.True((await ResubmitAsync(runId, new Dictionary<string, object?>(values) { ["registration"] = 80m })).Result.Ok);
        await using (var db = Db())
            Assert.Equal("Business Administrator approval", (await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending)).Title);

        // This time the approver asks for every approval to be given again.
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.True((await Service(db).DecideAsync(task.Id, "admin@example.org", "Admin", false, ApprovalAction.Return, "Start over", default, restartApprovals: true)).Ok);
        }
        Assert.True((await ResubmitAsync(runId, new Dictionary<string, object?>(values) { ["registration"] = 80m })).Result.Ok);
        await using var check = Db();
        Assert.Equal("Final approval — Science", (await check.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending)).Title);
        Assert.DoesNotContain("_restart", (await check.Runs.SingleAsync(r => r.Id == runId)).ContextJson);
    }

    [Fact]
    public async Task Resubmit_ChangingTheDistrict_AfterApprovals_StartsTheApprovalsAgain()
    {
        var (runId, values) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await ApproveInTurnAsync(runId, TechApprovals[0], TechApprovals[1]);
        Assert.True((await ActAsync(runId, "admin@example.org", ApprovalAction.Return, "Wrong district?")).Ok);
        Assert.True((await ResubmitAsync(runId, new Dictionary<string, object?>(values) { ["district"] = "Bergen County Special Services" })).Result.Ok);
        await using var db = Db();
        Assert.Equal("Final approval — Science", (await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending)).Title);
    }

    private async Task<int> TravelToReconcileAsync()
    {
        var (runId, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await ApproveInTurnAsync(runId, TechApprovals);
        Assert.True((await DoTaskAsync(runId, "admin@example.org", new() { ["office_budget"] = "11-000-223", ["office_po"] = "PO-1" })).Result.Ok);
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            foreach (var id in new[] { "r1", "p1" })
                db.Attachments.Add(new Attachment { PublicId = id, StoredName = id + ".bin", FileName = id + ".pdf", Size = 10, WorkflowId = run.WorkflowId, OwnerEmail = "t@school.org", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        object File(string id) => new List<object?> { new Dictionary<string, object?> { ["id"] = id } };
        Assert.True((await DoTaskAsync(runId, "t@school.org", new() { ["actual_total"] = 90m, ["receipts"] = File("r1"), ["pd_report"] = File("p1") })).Result.Ok);
        return runId;
    }

    private async Task<ActionResult> ReturnTaskAsync(int runId, string email, string? comment)
    {
        await using var db = Db();
        var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
        return await Service(db).ReturnTaskAsync(task.Id, email, email, false, comment, default);
    }

    [Fact]
    public async Task Task_SentBack_GoesToTheEarlierTask_ThenComesBack_WithTheChangesShown()
    {
        var runId = await TravelToReconcileAsync();
        Assert.False((await ReturnTaskAsync(runId, "admin@example.org", " ")).Ok);          // a reason is required
        Assert.False((await ReturnTaskAsync(runId, "t@school.org", "Not yours")).Ok);       // only the business office
        Assert.True((await ReturnTaskAsync(runId, "admin@example.org", "The hotel receipt is missing")).Ok);

        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.WaitingTask, run.Status);
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal("After the trip: actual costs and receipts", task.Title);
            Assert.StartsWith("Sent back by admin@example.org: The hotel receipt is missing", task.Instructions);
            Assert.Equal(1, await db.ApprovalTasks.CountAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Returned));
            Assert.Contains(await db.Notifications.ToListAsync(), n => n.UserEmail == "t@school.org" && n.Body.Contains("The hotel receipt is missing"));
        }
        object File(string id) => new List<object?> { new Dictionary<string, object?> { ["id"] = id } };
        Assert.True((await DoTaskAsync(runId, "t@school.org", new() { ["actual_total"] = 140m, ["receipts"] = File("r1"), ["pd_report"] = File("p1") })).Result.Ok);

        await using var check = Db();
        var after = await check.Runs.SingleAsync(r => r.Id == runId);
        Assert.Equal("Business office: reconcile travel costs", (await check.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending)).Title);
        Assert.Equal("$90.00", PreviousOf(after)["actual_total"]);
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("answers updated (adjusted: Actual total cost)"));

        // The reconciliation was sent back before it was ever done, so doing it now is not an "edit".
        Assert.True((await DoTaskAsync(runId, "admin@example.org", new() { ["reimbursement"] = 140m })).Result.Ok);
        await using var finished = Db();
        Assert.False(PreviousOf(await finished.Runs.SingleAsync(r => r.Id == runId)).ContainsKey("reimbursement"));
    }

    [Fact]
    public async Task Task_SentBack_WithNoEarlierTask_GoesToTheRequester_AndComesBackToTheTask()
    {
        var (runId, values) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await ApproveInTurnAsync(runId, TechApprovals);
        Assert.True((await ReturnTaskAsync(runId, "admin@example.org", "Which conference session?")).Ok);
        await using (var db = Db())
        {
            Assert.Equal(RunStatus.ChangesRequested, (await db.Runs.SingleAsync(r => r.Id == runId)).Status);
            Assert.Contains(await db.Notifications.ToListAsync(), n => n.UserEmail == "t@school.org" && n.Title.StartsWith("Changes needed"));
        }
        Assert.True((await ResubmitAsync(runId, new Dictionary<string, object?>(values) { ["reason_a"] = "Session 4B on lab safety" })).Result.Ok);
        await using var check = Db();
        Assert.Equal(RunStatus.WaitingTask, (await check.Runs.SingleAsync(r => r.Id == runId)).Status);
        Assert.Equal("Business office: budget, PO and Board approval", (await check.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending)).Title);
    }

    private async Task<ActionResult> ForwardAsync(int runId, string email, string to, bool admin = false, bool owner = false, IReadOnlyList<string>? domains = null)
    {
        await using var db = Db();
        var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
        return await Service(db).ForwardAsync(task.Id, email, email, admin, owner, to, "Please cover this", domains, default);
    }

    [Fact]
    public async Task Forward_HandsTheStepOn_RecordsIt_AndTellsTheNewPerson()
    {
        var runId = await SubmitTripAsync("Science");
        Assert.False((await ForwardAsync(runId, "someone@school.org", "deputy@school.org")).Ok);   // not theirs
        Assert.False((await ForwardAsync(runId, "principal@example.org", "not an email")).Ok);
        Assert.False((await ForwardAsync(runId, "principal@example.org", "t@school.org")).Ok);     // the requester
        Assert.False((await ForwardAsync(runId, "principal@example.org", "x@gmail.com", domains: new[] { "school.org" })).Ok);
        Assert.True((await ForwardAsync(runId, "principal@example.org", "Deputy@School.org", domains: new[] { "school.org" })).Ok);

        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal(new[] { "deputy@school.org" }, task.AssigneeList.ToArray());
            Assert.Contains(await db.Notifications.ToListAsync(), n => n.UserEmail == "deputy@school.org" && n.Body.Contains("Please cover this"));
            Assert.Equal("approval.forwarded", (await db.AuditEntries.SingleAsync()).Action);
            Assert.Contains(await db.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("passed from principal@example.org to deputy@school.org"));
            Assert.Contains("deputy@school.org", (await db.Runs.SingleAsync(r => r.Id == runId)).ContextJson);   // the chain record follows
        }
        await DecideAsync(runId, "principal@example.org", expectOk: false);                        // no longer theirs
        // A workflow owner can pass on a step that is not theirs.
        Assert.True((await ForwardAsync(runId, "owner@school.org", "head@school.org", owner: true)).Ok);
        await DecideAsync(runId, "head@school.org");
    }

    [Fact]
    public void Guard_FindsCostIncreases_AndTheNamesThatChoseTheRoute()
    {
        var def = Json.ParseDefinition(SeedData.Templates.Single(t => t.Name == "Travel").Json);
        var before = new Dictionary<string, object?> { ["total"] = 100m, ["tolls"] = null, ["miles"] = 5m, ["district"] = "A" };
        var after = new Dictionary<string, object?> { ["total"] = 100.00m, ["tolls"] = 10m, ["miles"] = 50m, ["district"] = "A" };
        Assert.Equal(new[] { "tolls" }, ResubmitGuard.Increased(def.Form.Fields, before, after).Select(f => f.Key).ToArray());
        Assert.DoesNotContain("district", ResubmitGuard.RoutingNames(def.Flow, "central"));   // the step itself asks again anyway
        var routing = ResubmitGuard.RoutingNames(def.Flow, "business");
        Assert.Contains("district", routing);
        Assert.Contains("department", routing);
        Assert.DoesNotContain("board_decision", routing);                    // only steps on the way to this one count
        Assert.Contains("board_decision", ResubmitGuard.RoutingNames(def.Flow, "tell"));
    }

    [Fact]
    public void ZipNames_NeverReplaceEachOther()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a.pdf", SchoolFlow.Web.Controllers.FilesController.UniqueName("a.pdf", used));
        Assert.Equal("A (2).pdf", SchoolFlow.Web.Controllers.FilesController.UniqueName("A.pdf", used));
        Assert.Equal("a (3).pdf", SchoolFlow.Web.Controllers.FilesController.UniqueName("a.pdf", used));
    }

    private static List<string> FileIdsOf(WorkflowRun run) =>
        (Json.ToValues(run.ContextJson).GetValueOrDefault("attachments") as List<object?> ?? new())
            .OfType<Dictionary<string, object?>>().Select(f => SchoolFlow.Core.Expressions.Values.ToText(f.GetValueOrDefault("id"))).ToList();

    [Fact]
    public async Task Task_DoneAgain_ListsEachFileOnce_AndDeletesFilesTakenOff()
    {
        var runId = await TravelToReconcileAsync();
        Assert.True((await ReturnTaskAsync(runId, "admin@example.org", "Wrong receipt")).Ok);
        Directory.CreateDirectory(_uploads);
        await using (var db = Db())
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            db.Attachments.Add(new Attachment { PublicId = "r2", StoredName = "r2.bin", FileName = "right.pdf", Size = 10, WorkflowId = run.WorkflowId, OwnerEmail = "t@school.org", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(Path.Combine(_uploads, "r1.bin"), "x");
        object Files(params string[] ids) => ids.Select(id => (object?)new Dictionary<string, object?> { ["id"] = id }).ToList();
        Assert.True((await DoTaskAsync(runId, "t@school.org", new() { ["actual_total"] = 90m, ["receipts"] = Files("r2"), ["pd_report"] = Files("p1") })).Result.Ok);

        await using var check = Db();
        var after = await check.Runs.SingleAsync(r => r.Id == runId);
        Assert.Equal(new[] { "p1", "r2" }, FileIdsOf(after).OrderBy(x => x).ToArray());          // no duplicates, the old receipt gone
        Assert.False(await check.Attachments.AnyAsync(a => a.PublicId == "r1"));
        Assert.False(File.Exists(Path.Combine(_uploads, "r1.bin")));
        Assert.Equal(runId, (await check.Attachments.SingleAsync(a => a.PublicId == "r2")).RunId);
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("Removed 1 file taken off the task: r1.pdf"));
    }

    [Fact]
    public async Task Task_DoneAgain_ByAnAdminOnTheirBehalf_KeepsTheFilesTheEmployeeAttached()
    {
        var runId = await TravelToReconcileAsync();
        Assert.True((await ReturnTaskAsync(runId, "admin@example.org", "Check the total")).Ok);
        object File(string id) => new List<object?> { new Dictionary<string, object?> { ["id"] = id } };
        var (form, result) = await DoTaskAsync(runId, "admin@example.org", new() { ["actual_total"] = 95m, ["receipts"] = File("r1"), ["pd_report"] = File("p1") }, admin: true);
        Assert.True(result.Ok, result.Error);
        await using var check = Db();
        Assert.Equal(new[] { "p1", "r1" }, FileIdsOf(await check.Runs.SingleAsync(r => r.Id == runId)).OrderBy(x => x).ToArray());
        Assert.Equal(2, await check.Attachments.CountAsync(a => a.RunId == runId));
    }

    [Fact]
    public async Task Forward_ByAStandIn_OnAStepEveryoneMustApprove_ReplacesOnlyThePersonTheyCover()
    {
        await using (var db = Db())
        {
            db.Delegations.Add(new Delegation { FromEmail = "principal@example.org", ToEmail = "deputy@school.org", StartsOn = DateTime.Today.AddDays(-1), EndsOn = DateTime.Today.AddDays(5), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var runId = await SubmitTripAsync("Mathematics");                                         // admin and principal must both approve
        await using (var db = Db())
        {
            var task = await db.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
            Assert.Equal(ApprovalRules.All, task.Rule);
            Assert.Equal("principal@example.org", task.StandsInFor("deputy@school.org"));
        }
        Assert.True((await ForwardAsync(runId, "deputy@school.org", "head@school.org")).Ok);
        await using var check = Db();
        var after = await check.ApprovalTasks.SingleAsync(t => t.RunId == runId && t.Status == ApprovalStatus.Pending);
        Assert.Equal(new[] { "admin@example.org", "head@school.org" }, after.AssigneeList.OrderBy(a => a).ToArray());
        Assert.Contains(await check.RunEvents.Where(e => e.RunId == runId).ToListAsync(), e => e.Message.Contains("passed from principal@example.org to head@school.org"));
    }

    // ---- the request's journey (train-stop track) and waiting ages ---------------------------

    private async Task<List<JourneyStop>> StopsAsync(int runId)
    {
        await using var db = Db();
        var run = await db.Runs.SingleAsync(r => r.Id == runId);
        await new DefinitionStore().FillAsync(db, run);
        var tasks = await db.ApprovalTasks.Where(t => t.RunId == runId).ToListAsync();
        var nameOf = await Journey.NamesAsync(db, tasks.SelectMany(t => t.AssigneeList));
        return Journey.Build(Json.ParseDefinition(run.DefinitionJson), run, Json.ToValues(run.ContextJson), tasks, DateTime.UtcNow,
            new HashSet<DateTime>(), nameOf, new HashSet<string>());
    }

    [Fact]
    public async Task Journey_ShowsEveryStop_WhereTheRequestIs_AndWhoItIsWaitingOn()
    {
        var (runId, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await ApproveInTurnAsync(runId, TechApprovals[0], TechApprovals[1]);
        var stops = await StopsAsync(runId);
        Assert.Equal(new[]
        {
            "Submitted", "Final approval — Science", "Central Office Administrator approval", "Business Administrator approval", "Superintendent approval",
            "Business office: budget, PO and Board approval", "After the trip: actual costs and receipts", "Business office: reconcile travel costs", "Completed",
        }, stops.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "done", "done", "done", "current", "upcoming", "upcoming", "upcoming", "upcoming", "upcoming" }, stops.Select(s => s.State).ToArray());
        var here = stops[3];
        Assert.StartsWith("Business Administrator team: ", here.Who);                    // the team, then the people in it
        Assert.Equal("fresh", here.Age!.Tone);
        Assert.Equal("Here now · today", here.Age.Label);
        Assert.Equal("Superintendent team", stops[4].Who);                               // who will handle what comes next
        Assert.StartsWith("Approved by ", stops[2].Who);

        // Sent back: the stop says who it is waiting on now.
        Assert.True((await ActAsync(runId, "admin@example.org", ApprovalAction.Return, "Add the hotel")).Ok);
        var returned = (await StopsAsync(runId))[3];
        Assert.Equal("returned", returned.State);
        Assert.Equal("Sent back — waiting on T Teacher to edit", returned.Who);
        Assert.Equal("Add the hotel", returned.Note);
    }

    [Fact]
    public async Task Journey_OfAFinishedRequest_EndsWhereItEnded()
    {
        var (runId, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await DecideAsync(runId, "principal@example.org", approved: false);
        var stops = await StopsAsync(runId);
        Assert.Equal(new[] { "done", "rejected", "rejected" }, stops.Select(s => s.State).ToArray());
        Assert.Equal("Not approved", stops[^1].Title);
    }

    [Fact]
    public void WaitAge_CountsSchoolDays_AndTurnsAmberThenRed()
    {
        var closed = new HashSet<DateTime>();
        DateTime At(int y, int m, int d) => new DateTime(y, m, d, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal(("fresh", 1), Pick(WaitAge.Of(At(2026, 11, 13), At(2026, 11, 16), closed)));     // Friday → Monday
        Assert.Equal(("aging", 2), Pick(WaitAge.Of(At(2026, 11, 13), At(2026, 11, 17), closed)));
        Assert.Equal(("delayed", 4), Pick(WaitAge.Of(At(2026, 11, 13), At(2026, 11, 19), closed)));
        Assert.Equal("Waiting 4 school days", WaitAge.Of(At(2026, 11, 13), At(2026, 11, 19), closed).Label);
        Assert.Equal(("delayed", 0), Pick(WaitAge.Of(At(2026, 11, 16), At(2026, 11, 16), closed, escalated: true)));
        var overdue = WaitAge.Of(At(2026, 11, 16), At(2026, 11, 16), closed, dueOn: new DateTime(2026, 11, 10));
        Assert.Equal(("delayed", "Overdue"), (overdue.Tone, overdue.Label));
        Assert.Equal("aging", WaitAge.Of(At(2026, 11, 16), At(2026, 11, 16), closed, dueOn: new DateTime(2026, 11, 17)).Tone);
        static (string, int) Pick(WaitAge a) => (a.Tone, a.Days);
    }

    [Theory]
    [InlineData("Request for Work Related Travel", "", "plane")]
    [InlineData("Field Trip Request", "", "bus")]
    [InlineData("Technology Purchase Request", "", "cart")]
    [InlineData("Device Repair", "Technology", "wrench")]
    [InlineData("Escalate a ticket", "", "laptop")]
    [InlineData("Three-day permit", "", "form")]
    public void WorkflowIcons_MatchWholeWords(string name, string category, string icon) =>
        Assert.Equal(icon, Ui.WorkflowIcon(name, category));

    // ---- GSA per diem rates on the travel form ---------------------------------------------------

    private async Task<SchoolFlow.Core.Forms.FormResult> EvaluateTravelAsync(Action<Dictionary<string, object?>> change, IPerDiemSource? source = null)
    {
        await using var db = Db();
        var workflow = await db.Workflows.SingleAsync(w => w.Name == "Request for Work Related Travel");
        var dept = await db.Departments.SingleAsync(d => d.Name == "Science");
        var input = new Dictionary<string, object?>
        {
            ["request_date"] = "2027-01-15", ["employee_name"] = "T Teacher", ["district"] = "Bergen County Technical Schools",
            ["department"] = new Dictionary<string, object?> { ["id"] = (decimal)dept.Id, ["name"] = "Science" },
            ["event_title"] = "Convention", ["location"] = "Princeton", ["dest_zip"] = "08540-1234",
            ["from_date"] = "2027-02-27", ["to_date"] = "2027-03-02", ["paid_by"] = new List<object?> { "BOE" }, ["reason_a"] = "x",
            ["meal_first_people"] = 1m, ["meal_days"] = 2m, ["meal_day_people"] = 1m, ["meal_last_people"] = 1m,
            ["lodging_nights"] = 3m, ["lodging_rate"] = 215m, ["lodging_rooms"] = 1m,
            ["gsa"] = new Dictionary<string, object?> { ["meals"] = 999m },          // the browser cannot set the rates
        };
        change(input);
        var (form, _) = await Service(db, perDiem: source).SubmitFormAsync(workflow, input, "t@school.org", "T Teacher", default);
        return form;
    }

    [Fact]
    public async Task PerDiem_FillsTheMealRates_AndCapsTheHotelAtTheHighestMonthOfTheStay()
    {
        var form = await EvaluateTravelAsync(_ => { });
        var gsa = (Dictionary<string, object?>)form.Values["gsa"]!;
        Assert.Equal(59m, gsa["meals"]);
        Assert.Equal(44.25m, gsa["first_last_day"]);                     // 75% on the first and last day
        Assert.Equal(220m, gsa["lodging"]);                              // nights in February ($200) and March ($220): the higher
        Assert.Equal("08540", gsa["zip"]);
        Assert.Equal(2027m, gsa["fiscal_year"]);
        Assert.Equal(44.25m, form.Values["meal_first_rate"]);
        Assert.Equal(59m, form.Values["meal_day_rate"]);
        Assert.Equal(44.25m, form.Values["meal_last"]);
        Assert.Empty(form.Fields["lodging_rate"].Errors);                // $215 is under $220

        var over = await EvaluateTravelAsync(i => i["lodging_rate"] = 230m);
        Assert.Contains("more than the GSA hotel limit", over.Fields["lodging_rate"].Errors.Single());
        var conference = await EvaluateTravelAsync(i => { i["lodging_rate"] = 230m; i["conference_hotel"] = true; });
        Assert.Empty(conference.Fields["lodging_rate"].Errors);          // the conference hotel may cost more…
        Assert.Contains("required", conference.Fields["documents"].Errors.Single());   // …with documentation

        var badZip = await EvaluateTravelAsync(i => i["dest_zip"] = "8540");
        Assert.Contains("5-digit", badZip.Fields["dest_zip"].Errors.Single());
        Assert.Null(badZip.Values["gsa"]);
    }

    [Fact]
    public async Task PerDiem_WhenGsaCannotBeReached_UsesTheStandardRate_AndFlagsTheRequest()
    {
        var (runId, values) = await SubmitTravelAsync("Bergen County Technical Schools", input => { input["dest_zip"] = "99999"; input["lodging_rate"] = 100m; });
        var gsa = (Dictionary<string, object?>)values["gsa"]!;
        Assert.Equal("fallback", gsa["source"]);
        Assert.Equal(68m, gsa["meals"]);                                // the standard rate kept under Admin → Rates
        Assert.Equal(110m, gsa["lodging"]);
        await using var db = Db();
        Assert.Contains(await db.RunEvents.Where(e => e.RunId == runId).ToListAsync(),
            e => e.Kind == "warning" && e.Message.Contains("standard rate was used"));
        Assert.StartsWith("Standard rate used", Ui.PerDiemText(gsa));
    }

    [Fact]
    public async Task PerDiem_LooksEachPlaceUpOnce()
    {
        var source = new FixedPerDiem();
        await EvaluateTravelAsync(_ => { }, source);
        Assert.Equal(1, source.Calls);
        // A stay across 30 September needs both fiscal years.
        var split = new FixedPerDiem();
        await EvaluateTravelAsync(i => { i["dest_zip"] = "10001"; i["from_date"] = "2027-09-29"; i["to_date"] = "2027-10-02"; }, split);
        Assert.Equal(2, split.Calls);
    }

    /// <summary>Like GSA: rates only up to a given fiscal year; later years are "not published" (null).</summary>
    private sealed class PublishedThrough : IPerDiemSource
    {
        private readonly int _last;
        public List<int> Asked { get; } = new();
        public PublishedThrough(int last) => _last = last;
        public string Source => "gsa";
        public Task<SchoolFlow.Core.Forms.PerDiemArea?> GetAsync(string zip, int fiscalYear, CancellationToken ct)
        {
            Asked.Add(fiscalYear);
            return Task.FromResult(fiscalYear > _last ? null : new SchoolFlow.Core.Forms.PerDiemArea("Princeton", "Mercer", "NJ", 59m + fiscalYear - 2026,
                Enumerable.Range(1, 12).ToDictionary(m => m, m => 200m), false));
        }
    }

    [Fact]
    public async Task PerDiem_ForAYearGsaHasNotPublished_BlocksTheRequest_OnlyWhenMealsOrAHotelAreClaimed()
    {
        var current = SchoolFlow.Core.Forms.PerDiemDirectory.FiscalYear(DateTime.Today);
        void NextOctober(Dictionary<string, object?> i) { i["from_date"] = $"{current}-10-12"; i["to_date"] = $"{current}-10-14"; }   // fiscal year current + 1

        // Meals and a hotel claimed: GSA's own rates for that year are needed, and it has not published them.
        var source = new PublishedThrough(current);
        var claimed = await EvaluateTravelAsync(NextOctober, source);
        var error = Assert.Single(claimed.Fields["gsa"].Errors);
        Assert.Contains($"fiscal year {current + 1} (travel from October 1, {current})", error);
        Assert.False(claimed.IsValid);
        var shown = (Dictionary<string, object?>)claimed.Values["gsa"]!;
        Assert.Equal((decimal)current, shown["rates_year"]);                 // last year's rates are shown for reference
        Assert.Contains("for reference", (string)shown["note"]!);
        Assert.Equal(new[] { current + 1, current }, source.Asked.ToArray());

        // Nothing claimed that needs GSA (no meals, no hotel): the trip can go ahead.
        var none = await EvaluateTravelAsync(i =>
        {
            NextOctober(i);
            foreach (var key in new[] { "meal_first_people", "meal_days", "meal_day_people", "meal_last_people", "lodging_nights", "lodging_rate", "lodging_rooms" }) i[key] = null;
        }, new PublishedThrough(current));
        Assert.Empty(none.Fields["gsa"].Errors);

        // Once GSA publishes the year, its own rates are used and the request can be submitted.
        var published = await EvaluateTravelAsync(NextOctober, new PublishedThrough(current + 1));
        Assert.Empty(published.Fields["gsa"].Errors);
        var own = (Dictionary<string, object?>)published.Values["gsa"]!;
        Assert.False(own.ContainsKey("rates_year"));
        Assert.Equal(59m + current + 1 - 2026, own["meals"]);
    }

    [Fact]
    public void GsaReply_IsRead()
    {
        var reply = System.Text.Json.JsonDocument.Parse("""
            { "request": null, "errors": [], "rates": [ { "oconus": false, "state": "NJ", "year": 2027, "isOconus": "false",
              "rate": [ { "city": "Princeton / Trenton", "county": "Mercer", "standardRate": "false", "meals": 74, "zip": "08540",
                "months": { "month": [ { "value": 165, "number": 1, "short": "Jan", "long": "January" }, { "value": 190, "number": 5, "short": "May", "long": "May" } ] } } ] } ] }
            """);
        var area = GsaPerDiemClient.Parse(reply.RootElement)!;
        Assert.Equal(("Princeton / Trenton", "Mercer", "NJ", 74m, false), (area.City, area.County, area.State, area.Meals, area.StandardRate));
        Assert.Equal(190m, area.LodgingByMonth[5]);
        Assert.Null(GsaPerDiemClient.Parse(System.Text.Json.JsonDocument.Parse("""{ "rates": [] }""").RootElement));
        Assert.Equal(2027, SchoolFlow.Core.Forms.PerDiemDirectory.FiscalYear(new DateTime(2026, 10, 1)));
        Assert.Equal(2026, SchoolFlow.Core.Forms.PerDiemDirectory.FiscalYear(new DateTime(2026, 9, 30)));
    }

    [Theory]
    [InlineData("dbo.BergenFormsStudents", true)]
    [InlineData("[Student Info].dbo.[Roster View]", true)]
    [InlineData("Roster", true)]
    [InlineData("dbo.Students; DROP TABLE x", false)]
    [InlineData("dbo.Students--", false)]
    public void RosterViewName_MustBeAPlainName(string view, bool ok)
    {
        var made = Record.Exception(() => new SqlServerStudentRoster("Server=x;Database=y;Integrated Security=True", view));
        Assert.Equal(ok, made is null);
    }

    [Fact]
    public void RosterColumns_CanBeNamedLikeTheDistrictsView()
    {
        var columns = new Dictionary<string, string?> { ["StudentId"] = "StudentNumber", ["School"] = "Location", ["Academy"] = "[Academy or Major]" };
        Assert.Null(Record.Exception(() => new SqlServerStudentRoster("Server=x;Database=y;Integrated Security=True", "dbo.ActiveStudents", columns)));
        columns["Grade"] = "Grade]; DROP TABLE x;--";
        Assert.NotNull(Record.Exception(() => new SqlServerStudentRoster("Server=x;Database=y;Integrated Security=True", "dbo.ActiveStudents", columns)));
    }

    // ---- who may see which students: location, district, all ---------------------------------

    private static StudentScopeService Scope(AppDbContext db) => new(db, new SampleStaffDirectory(),
        new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The staff list writes districts short; the roster writes them in full.
            ["Students:Match:Districts:BCTS"] = "Bergen County Technical Schools", ["Students:Match:Districts:BCSS"] = "Bergen County Special Services",
        }).Build());

    [Fact]
    public async Task StudentAccess_ReachesOwnLocation_WholeDistrict_OrEverywhere()
    {
        await using var db = Db();
        db.Users.AddRange(
            new AppUser { Email = "teacher@example.org", Name = "Taylor Teacher", StudentAccess = true },                                    // Teterboro (BCTS)
            new AppUser { Email = "builder@example.org", Name = "Bailey Builder", StudentAccess = true, StudentScope = StudentScopes.District }, // BCSS
            new AppUser { Email = "admin@example.org", Name = "Avery Admin", StudentAccess = true, StudentScope = StudentScopes.All },
            new AppUser { Email = "principal@example.org", Name = "Pat Principal" });                                                         // no access
        db.Users.Add(new AppUser { Email = "nobody@example.org", Name = "Not On Staff List", StudentAccess = true });
        await db.SaveChangesAsync();
        var scope = Scope(db);
        var roster = new SampleStudentRoster();
        async Task<string[]> Sees(string email) =>
            (await roster.SearchAsync(new[] { "o" }.Concat(new string[0]).ToList(), 15, await scope.FilterForAsync(email) ?? StudentFilter.Nobody, default))
            .Select(x => x.LastName).OrderBy(n => n).ToArray();
        // (the sample roster: Okafor at Teterboro and Okon at Paramus, both BCTS; Ruiz at Hackensack, BCSS)
        Assert.Equal(new[] { "Okafor" }, await Sees("teacher@example.org"));                 // own location only, not Okon at Paramus
        Assert.Empty(await Sees("builder@example.org"));                                     // whole BCSS district: Ruiz, who does not match "o"
        Assert.Equal(new[] { "Ruiz" }, (await roster.SearchAsync(new[] { "r" }, 15, (await scope.FilterForAsync("builder@example.org"))!, default)).Select(x => x.LastName).ToArray());
        Assert.Equal(new[] { "Okafor", "Okon" }, await Sees("admin@example.org"));            // all districts
        Assert.Null(await scope.FilterForAsync("principal@example.org"));                    // no access at all
        Assert.True((await scope.FilterForAsync("nobody@example.org"))!.IsEmpty);            // access, but not on the staff list: nobody

        // Submitting a student outside your reach is refused like an unknown one.
        var workflow = await StudentFormAsync();
        var pick = (string id) => new Dictionary<string, object?> { ["student"] = new Dictionary<string, object?> { ["id"] = id } };
        var (outside, none) = await Service(db, scope: scope).SubmitFormAsync(workflow, pick("20452"), "teacher@example.org", "Taylor Teacher", default);
        Assert.Null(none);
        Assert.Contains("was not found", outside.Fields["student"].Errors.Single());
        var (_, ok) = await Service(db, scope: scope).SubmitFormAsync(workflow, pick("20451"), "teacher@example.org", "Taylor Teacher", default);
        Assert.NotNull(ok);
    }

    [Fact]
    public void StudentOnARequest_IsSeenByThoseActingOnIt_TheRequester_OrAccessThatCoversIt()
    {
        var known = new Dictionary<string, SchoolFlow.Core.StudentInfo>
        {
            ["20452"] = new("20452", "Leo", "Okon", "Paramus", "11", "Culinary") { District = "Bergen County Technical Schools" },
        };
        var teterboro = new StudentFilter(false, new[] { "Teterboro" }, Array.Empty<string>());
        var bcts = new StudentFilter(false, Array.Empty<string>(), new[] { "Bergen County Technical Schools" });
        Assert.True(StudentVisibility.MaySee(actsOnIt: true, isRequester: false, filter: null, new[] { "20452" }, known));   // an approver, even without access
        Assert.False(StudentVisibility.MaySee(false, false, null, new[] { "20452" }, known));
        Assert.False(StudentVisibility.MaySee(false, false, teterboro, new[] { "20452" }, known));                       // another building
        Assert.True(StudentVisibility.MaySee(false, false, bcts, new[] { "20452" }, known));                             // the whole district
        Assert.True(StudentVisibility.MaySee(false, true, teterboro, new[] { "20452" }, known));                          // their own request
        Assert.False(StudentVisibility.MaySee(false, false, bcts, new[] { "99999" }, known));                             // not on the roster: cannot tell
    }

    // ---- shared definitions (stored once, not per request) --------------------------------------

    [Fact]
    public async Task Requests_ShareOneStoredDefinition_PerVersion()
    {
        var (first, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        var (second, _) = await SubmitTravelAsync("Bergen County Special Services", input => input["registration"] = 100m);
        await using var db = Db();
        var runs = await db.Runs.Where(r => r.Id == first || r.Id == second).ToListAsync();
        Assert.All(runs, r => Assert.NotNull(r.DefinitionId));
        Assert.Single(runs.Select(r => r.DefinitionId).Distinct());                 // one stored copy for both
        Assert.All(runs, r => Assert.Null(r.LegacyDefinitionJson));
        Assert.Throws<InvalidOperationException>(() => runs[0].DefinitionJson);     // never read without loading it
        await new DefinitionStore().FillAsync(db, runs);
        Assert.Contains(Json.ParseDefinition(runs[0].DefinitionJson).Form.Fields, f => f.Key == "dest_zip");
    }

    [Fact]
    public async Task OlderRequests_WithTheirOwnCopy_AreMovedToSharedDefinitions()
    {
        var (runId, _) = await SubmitTravelAsync("Bergen County Technical Schools", input => input["registration"] = 100m);
        await using (var db = Db())
        {
            // As a request from before the change looked: its own copy, no shared definition.
            var run = await db.Runs.SingleAsync(r => r.Id == runId);
            await new DefinitionStore().FillAsync(db, run);
            run.LegacyDefinitionJson = run.DefinitionJson;
            run.DefinitionId = null;
            await db.SaveChangesAsync();
            db.RunDefinitions.RemoveRange(db.RunDefinitions);
            await db.SaveChangesAsync();
        }
        await using (var db = Db())
            Assert.Equal(1, DefinitionStore.MoveLegacy(db, NullLogger.Instance));
        await using var check = Db();
        var moved = await check.Runs.SingleAsync(r => r.Id == runId);
        Assert.NotNull(moved.DefinitionId);
        Assert.Null(moved.LegacyDefinitionJson);
        await using (var again = Db()) Assert.Equal(0, DefinitionStore.MoveLegacy(again, NullLogger.Instance));   // once only
        // and the request still works: an approver can act on it.
        await DecideAsync(runId, "principal@example.org");
    }

    private sealed class CountingRoster : IStudentRoster
    {
        public int Finds;
        public bool IsConfigured => true;
        public Task<IReadOnlyList<SchoolFlow.Core.StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, StudentFilter filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<SchoolFlow.Core.StudentInfo>>(Array.Empty<SchoolFlow.Core.StudentInfo>());
        public Task<string> TestAsync(CancellationToken ct) => Task.FromResult("");
        public async Task<IReadOnlyDictionary<string, SchoolFlow.Core.StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
        {
            Finds++;
            return await new SampleStudentRoster().FindAsync(ids, ct);
        }
    }

    [Fact]
    public async Task StudentLookups_AreRememberedBriefly_SoTypingDoesNotAskTheServerEachTime()
    {
        var inner = new CountingRoster();
        var roster = new CachedStudentRoster(inner);
        for (var i = 0; i < 5; i++) Assert.Equal("Okafor", (await roster.FindAsync(new[] { "20451" }, default))["20451"].LastName);
        Assert.Equal(1, inner.Finds);
        Assert.Empty(await roster.FindAsync(new[] { "99999" }, default));
        Assert.Empty(await roster.FindAsync(new[] { "99999" }, default));
        Assert.Equal(2, inner.Finds);                                        // "not found" is remembered too
    }

    // ---- review fixes (Nov 19) -----------------------------------------------------------------

    [Fact]
    public void RecheckingAStoredRequest_KeepsItsPerDiemRates()
    {
        var form = new FormDefinition { Fields =
        {
            new FieldDef { Key = "gsa", Label = "GSA", Type = FieldTypes.PerDiem, PerDiem = new PerDiemSettings { Zip = "zip", From = "from" } },
            new FieldDef { Key = "zip", Label = "ZIP", Type = "text" },
            new FieldDef { Key = "from", Label = "From", Type = "date" },
            new FieldDef { Key = "meals", Label = "Meals", Type = "calculated", Formula = "gsa.meals * 2" },
        } };
        var stored = new Dictionary<string, object?>
        {
            ["zip"] = "08540", ["from"] = "2027-03-10",
            ["gsa"] = new Dictionary<string, object?> { ["meals"] = 74m, ["lodging"] = 165m, ["source"] = "gsa" },
        };
        // No rates directory: re-checking a request that is already stored (as when its students are restored).
        var result = FormEvaluator.Evaluate(form, stored);
        Assert.Equal(74m, ((Dictionary<string, object?>)result.Values["gsa"]!)["meals"]);
        Assert.Equal(148m, result.Values["meals"]);
        // A per diem field placed above the fields it reads is still worked out after them.
        Assert.Empty(DefinitionValidator.Validate(new WorkflowDefinition { Form = form }).Where(i => i.IsError && i.Message.Contains("circular")));
        form.Fields[1].Default = "gsa.zip";                                   // zip read from the per diem field: a loop
        Assert.Contains(DefinitionValidator.Validate(new WorkflowDefinition { Form = form }), i => i.IsError && i.Message.Contains("circular"));
    }

    [Fact]
    public void AStudentAnswer_IsUnchangedWhenTheStudentNumberIsTheSame()
    {
        var field = new FieldDef { Key = "student", Type = FieldTypes.Student };
        var numberOnly = new Dictionary<string, object?> { ["id"] = "20451" };
        var full = new Dictionary<string, object?> { ["id"] = "20451", ["name"] = "Okafor, Maya", ["grade"] = "11" };
        Assert.True(ResubmitGuard.SameAnswer(field, numberOnly, full));       // roster details read again, or not reachable
        Assert.False(ResubmitGuard.SameAnswer(field, numberOnly, new Dictionary<string, object?> { ["id"] = "20452" }));
        Assert.False(ResubmitGuard.Same(numberOnly, full));                   // other fields still compare in full
    }
}
