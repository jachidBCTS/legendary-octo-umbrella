using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using SchoolFlow.Core;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    public MigrationTests() => _connection.Open();
    public void Dispose() => _connection.Dispose();

    private SqliteAppDbContext Sqlite() => new(new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(_connection).Options);

    private static SqlServerAppDbContext SqlServer() =>
        new(new DbContextOptionsBuilder<SqlServerAppDbContext>().UseSqlServer("Server=unused;Database=unused").Options); // never connects

    /// <summary>Fails when someone changes the data model without adding a migration for it.</summary>
    private static void AssertNoPendingModelChanges(DbContext db)
    {
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;
        Assert.NotNull(snapshot);
        if (snapshot is IMutableModel mutable) snapshot = mutable.FinalizeModel();
        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot!);
        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshot.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.True(differences.Count == 0,
            $"The model has changed since the last {db.GetType().Name} migration. Add one with: dotnet ef migrations add <Name> --context {db.GetType().Name} " +
            $"({string.Join(", ", differences.Select(d => d.GetType().Name))})");
    }

    [Fact]
    public void Sqlite_Migrations_MatchTheModel()
    {
        using var db = Sqlite();
        AssertNoPendingModelChanges(db);
    }

    [Fact]
    public void SqlServer_Migrations_MatchTheModel()
    {
        using var db = SqlServer();
        AssertNoPendingModelChanges(db);
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void BothEngines_HaveTheSameMigrationNames()
    {
        static string[] Names(DbContext db) => db.Database.GetMigrations().Select(m => m[(m.IndexOf('_') + 1)..]).ToArray();
        using var a = Sqlite();
        using var b = SqlServer();
        Assert.Equal(Names(a), Names(b));
    }

    [Fact]
    public async Task FreshDatabase_IsCreatedByMigrations_AndIsUsable()
    {
        using (var db = Sqlite())
        {
            DatabaseSetup.Migrate(db, NullLogger.Instance);
            Assert.Empty(db.Database.GetPendingMigrations());
            SeedData.EnsureStarterWorkflows(db, sampleDepartments: true);
            db.SaveChanges();
        }
        using (var db = Sqlite())
        {
            DatabaseSetup.Migrate(db, NullLogger.Instance); // second start: nothing to do, nothing lost
            Assert.Equal(4, await db.Workflows.CountAsync());
            Assert.Equal(3, await db.Departments.CountAsync());
            Assert.Equal(3, await db.Locations.CountAsync());
            db.Notifications.Add(new Notification { UserEmail = "a@b.org", Title = "x", CreatedAt = DateTime.UtcNow });
            db.Runs.Add(new WorkflowRun { WorkflowName = "w", Status = "Running", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, DepartmentId = 1, WaitingTicketId = 5_000_000_000 });
            await db.SaveChangesAsync();
            Assert.Equal(5_000_000_000, (await db.Runs.SingleAsync()).WaitingTicketId);
        }
    }

    [Fact]
    public async Task UpgradingAnExistingDatabase_KeepsItsData()
    {
        // A site deployed at the first release…
        using (var db = Sqlite())
        {
            db.GetService<IMigrator>().Migrate("Initial");
            db.Users.Add(new AppUser { Email = "keep@school.org", Name = "Keep", Role = Roles.Admin });
            db.Departments.Add(new Department { Name = "Science", FinalApprovers = ";head@school.org;" });
            db.Runs.Add(new WorkflowRun { WorkflowName = "Trip", Status = "Completed", ContextJson = "{\"a\":1}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            Assert.NotEmpty(db.Database.GetPendingMigrations());
        }
        // …is upgraded in place by simply starting the new version.
        using (var db = Sqlite())
        {
            DatabaseSetup.Migrate(db, NullLogger.Instance);
            Assert.Empty(db.Database.GetPendingMigrations());
            Assert.Equal(Roles.Admin, (await db.Users.SingleAsync()).Role);
            Assert.Equal("Science", (await db.Departments.SingleAsync()).Name);
            Assert.Equal("{\"a\":1}", (await db.Runs.SingleAsync()).ContextJson);
            db.FormDrafts.Add(new FormDraft { WorkflowId = 1, OwnerEmail = "keep@school.org", ValuesJson = "{}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(); // the table added by the upgrade is usable
        }
    }

    [Fact]
    public void DatabaseFromBeforeMigrations_IsRefusedWithInstructions_NotCorrupted()
    {
        using (var old = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options))
        {
            old.Database.EnsureCreated(); // how early development builds made the database
            old.Users.Add(new AppUser { Email = "keep@school.org", Name = "Keep" });
            old.SaveChanges();
        }
        using var db = Sqlite();
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSetup.Migrate(db, NullLogger.Instance));
        Assert.Contains("had no migrations", ex.Message);
        Assert.Equal(1, db.Users.Count()); // untouched
    }
}

public sealed class LocationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public LocationTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FakeFreshservice : IFreshserviceClient
    {
        public List<LookupItem> Locations { get; set; } = new();
        public bool IsLive => true;
        public Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LookupItem>>(source == LookupSources.Locations ? Locations : throw new NotSupportedException());
        public Task<Dictionary<string, object?>> CreateTicketAsync(TicketCreate ticket, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, object?>> UpdateTicketAsync(long ticketId, TicketUpdate update, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddNoteAsync(long ticketId, string bodyHtml, bool isPrivate, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AttachFilesAsync(long ticketId, IReadOnlyList<TicketFile> files, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity, Dictionary<string, object?> customFields, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> TestConnectionAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Sync_Adds_Renames_SwitchesOff_AndLeavesManualOnesAlone()
    {
        var fs = new FakeFreshservice { Locations = { new("1", "Main Building"), new("2", "Gym"), new("x", "Bad id"), new("3", "  ") } };
        await using (var db = new AppDbContext(_options))
        {
            db.Locations.Add(new Location { Name = "Portable 4 (manual)" });
            await db.SaveChangesAsync();
            var first = await LocationSync.RunAsync(db, fs, default);
            Assert.Equal(new LocationSyncResult(2, 0, 0, 2), first);
        }

        await using (var db = new AppDbContext(_options))
        {
            // An admin switches the gym off; Freshservice renames building 1, drops nothing, adds a library.
            (await db.Locations.SingleAsync(l => l.Name == "Gym")).IsActive = false;
            await db.SaveChangesAsync();
            fs.Locations = new() { new("1", "Main Building (North)"), new("2", "Gym"), new("4", "Library") };
            var second = await LocationSync.RunAsync(db, fs, default);
            Assert.Equal(new LocationSyncResult(1, 1, 0, 3), second);
            Assert.False((await db.Locations.SingleAsync(l => l.Name == "Gym")).IsActive); // stays off
        }

        await using (var db = new AppDbContext(_options))
        {
            fs.Locations = new() { new("1", "Main Building (North)"), new("2", "Gym") }; // library removed in Freshservice
            var third = await LocationSync.RunAsync(db, fs, default);
            Assert.Equal(1, third.SwitchedOff);
            Assert.Contains("1 switched off", third.Describe());
            var all = await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync();
            Assert.Equal(new[] { "Gym", "Library", "Main Building (North)", "Portable 4 (manual)" }, all.Select(l => l.Name).ToArray());
            Assert.Equal(new[] { false, false, true, true }, all.Select(l => l.IsActive).ToArray());
            Assert.Null(all[3].FreshserviceId);

            Assert.Equal("Already up to date (2 locations in Freshservice).", (await LocationSync.RunAsync(db, fs, default)).Describe());
        }
    }

    [Fact]
    public async Task Sync_WithAnEmptyAnswer_ChangesNothing()
    {
        await using var db = new AppDbContext(_options);
        var fs = new FakeFreshservice { Locations = { new("1", "Main Building") } };
        await LocationSync.RunAsync(db, fs, default);
        fs.Locations = new();
        var result = await LocationSync.RunAsync(db, fs, default);
        Assert.NotNull(result.Skipped);
        Assert.True((await db.Locations.SingleAsync()).IsActive);
    }

    [Fact]
    public void FreshserviceLocations_GetFullPathNames()
    {
        var items = new List<LookupItem>
        {
            new("1", "North Campus", null, null), new("2", "Science Wing", null, "1"), new("3", "Room 101", null, "2"),
            new("4", "Loop A", null, "5"), new("5", "Loop B", null, "4"), new("6", "Orphan", null, "999"),
        };
        var names = FreshserviceClient.WithFullPaths(items).ToDictionary(i => i.Id, i => i.Name);
        Assert.Equal("North Campus › Science Wing › Room 101", names["3"]);
        Assert.Equal("North Campus", names["1"]);
        Assert.Equal("Loop B › Loop A", names["4"]); // a cycle in the data ends instead of looping forever
        Assert.Equal("Orphan", names["6"]);
    }

    [Fact]
    public void LocationField_OnlyAcceptsActiveLocations()
    {
        var form = new FormDefinition
        {
            Fields =
            {
                new FieldDef { Key = "where", Label = "Where", Type = "location", Required = true },
                new FieldDef { Key = "label", Label = "Label", Type = "calculated", Formula = "'At ' & where.name" },
            },
        };
        var known = new Dictionary<long, string> { [7] = "Main Building › Library" };
        var ok = FormEvaluator.Evaluate(form, Json.ToValues("{\"where\":{\"id\":7,\"name\":\"spoofed\"}}"), locations: known);
        Assert.Equal("At Main Building › Library", ok.Values["label"]);
        Assert.True(ok.IsValid);
        var gone = FormEvaluator.Evaluate(form, Json.ToValues("{\"where\":{\"id\":8,\"name\":\"Old gym\"}}"), locations: known);
        Assert.Contains("That location is no longer available", gone.Fields["where"].Errors.Single());
    }
}
