using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Tests;

public sealed class PermissionTests : IDisposable
{
    private const string Staff = "00000000-0000-0000-0000-00000000000a";
    private const string Science = "00000000-0000-0000-0000-00000000000b";
    private const string BuildersGroup = "00000000-0000-0000-0000-00000000000c";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public PermissionTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static ClaimsPrincipal Person(string email, string role = Roles.Staff, params string[] groups)
    {
        var claims = new List<Claim> { new("preferred_username", email), new("name", email.Split('@')[0]), new(ClaimTypes.Role, role) };
        claims.AddRange(groups.Select(g => new Claim("groups", g.ToUpperInvariant()))); // Entra's casing must not matter
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", ClaimTypes.Role));
    }

    private static readonly Dictionary<string, (string Role, string Name)> Grants = new()
    {
        [BuildersGroup] = (Roles.Builder, "Workflow Builders"),
        ["00000000-0000-0000-0000-00000000000d"] = (Roles.Admin, "IT Admins"),
    };

    [Fact]
    public void Role_IsTheHighestOfAllSources_AndSaysWhereItCameFrom()
    {
        var none = Array.Empty<string>();
        Assert.Equal(new RoleResolver.Result(Roles.Staff, "default"), RoleResolver.Resolve(Roles.Staff, false, "", none, Grants));
        Assert.Equal(new RoleResolver.Result(Roles.Builder, "set by an admin"), RoleResolver.Resolve(Roles.Builder, false, "", none, Grants));
        Assert.Equal(new RoleResolver.Result(Roles.Builder, "Entra group “Workflow Builders”"), RoleResolver.Resolve(Roles.Staff, false, "", new[] { " " + BuildersGroup.ToUpperInvariant() }, Grants));
        Assert.Equal(Roles.Admin, RoleResolver.Resolve(Roles.Builder, false, "", new[] { BuildersGroup, "00000000-0000-0000-0000-00000000000d" }, Grants).Role);
        Assert.Equal(new RoleResolver.Result(Roles.Admin, "Entra app role"), RoleResolver.Resolve(Roles.Staff, false, "Admin", none, Grants));
        Assert.Equal(new RoleResolver.Result(Roles.Admin, "server settings"), RoleResolver.Resolve(Roles.Staff, true, "", none, Grants));
        // A hand-set Admin is not downgraded by belonging to a Builder group, and junk never grants anything.
        Assert.Equal(new RoleResolver.Result(Roles.Admin, "set by an admin"), RoleResolver.Resolve(Roles.Admin, false, "Builder", new[] { BuildersGroup }, Grants));
        Assert.Equal(Roles.Staff, RoleResolver.Resolve("Superuser", false, "Owner", new[] { "not-a-registered-group" }, Grants).Role);
    }

    [Fact]
    public void TokenClaims_AreReadForGroupsAndAppRoles()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("preferred_username", "A@School.org"), new Claim("groups", Science.ToUpperInvariant()), new Claim("groups", " "), new Claim("roles", "builder"),
        }, "test"));
        Assert.Equal(new[] { Science }, user.Groups().ToArray());
        Assert.Equal(Roles.Builder, user.EntraRole());
        Assert.False(user.HasGroupOverage());
        var overage = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("_claim_names", "{\"groups\":\"src1\"}") }, "test"));
        Assert.True(overage.HasGroupOverage());
        Assert.Empty(overage.Groups());
    }

    [Fact]
    public void Submitting_FollowsTheFormsAudience()
    {
        var open = new Workflow { Name = "Open" };
        var science = new Workflow { Name = "Science only", AudienceGroups = ApprovalTask.Pack(new[] { Science }), Owners = ApprovalTask.Pack(new[] { "owner@school.org" }) };

        Assert.True(AccessService.CanSubmit(Person("anyone@school.org"), open));
        Assert.True(AccessService.CanSubmit(Person("sci@school.org", Roles.Staff, Staff, Science), science));
        Assert.False(AccessService.CanSubmit(Person("art@school.org", Roles.Staff, Staff), science));
        Assert.False(AccessService.CanSubmit(Person("nogroups@school.org"), science));
        // The people who run the form can always open it to test it; a builder who does not own it cannot.
        Assert.True(AccessService.CanSubmit(Person("owner@school.org", Roles.Builder), science));
        Assert.True(AccessService.CanSubmit(Person("admin@school.org", Roles.Admin), science));
        Assert.False(AccessService.CanSubmit(Person("otherbuilder@school.org", Roles.Builder), science));
    }

    [Fact]
    public void Managing_IsForTheOwnersAnAdminNamed_AndAdmins_ButOnlyAdminsEditForms()
    {
        var workflow = new Workflow { Name = "Trips", Owners = ApprovalTask.Pack(new[] { "Owner@School.org", "staff.owner@school.org" }) };
        Assert.True(AccessService.CanManage(Person("owner@school.org", Roles.Builder), workflow));
        Assert.True(AccessService.CanManage(Person("staff.owner@school.org", Roles.Staff), workflow));   // owning is enough; no Builder role needed
        Assert.True(AccessService.CanManage(Person("admin@school.org", Roles.Admin), workflow));
        Assert.False(AccessService.CanManage(Person("other@school.org", Roles.Builder), workflow));     // the Builder role alone grants nothing
        Assert.False(AccessService.CanManage(Person("wner@school.org", Roles.Builder), workflow));      // no partial-email matches
        Assert.False(AccessService.CanManage(Person("builder@school.org", Roles.Builder), new Workflow { Name = "Unowned" }));

        Assert.True(AccessService.CanEditForm(Person("admin@school.org", Roles.Admin)));
        Assert.False(AccessService.CanEditForm(Person("owner@school.org", Roles.Builder)));
        Assert.False(AccessService.CanEditForm(Person("staff.owner@school.org", Roles.Staff)));
    }

    [Fact]
    public async Task ViewingARequest_IsLimitedToThePeopleInvolved()
    {
        int tripId, otherId, runId;
        await using (var db = new AppDbContext(_options))
        {
            var trips = new Workflow { Name = "Trips", Owners = ApprovalTask.Pack(new[] { "owner@school.org" }) };
            var other = new Workflow { Name = "Other", Owners = ApprovalTask.Pack(new[] { "otherowner@school.org" }) };
            var dept = new Department { Name = "Science", FinalApprovers = ApprovalTask.Pack(new[] { "head@school.org" }) };
            var elsewhere = new Department { Name = "Art", FinalApprovers = ApprovalTask.Pack(new[] { "arthead@school.org" }) };
            db.AddRange(trips, other, dept, elsewhere);
            await db.SaveChangesAsync();
            var run = new WorkflowRun { WorkflowId = trips.Id, WorkflowName = "Trips", Status = "WaitingApproval", SubmitterEmail = "t@school.org", DepartmentId = dept.Id, DepartmentName = "Science", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            db.ApprovalTasks.Add(new ApprovalTask { RunId = run.Id, NodeId = "n", Title = "Lead", Assignees = ApprovalTask.Pack(new[] { "lead@school.org" }), Status = ApprovalStatus.Approved });
            await db.SaveChangesAsync();
            (tripId, otherId, runId) = (trips.Id, other.Id, run.Id);
        }

        await using var check = new AppDbContext(_options);
        var access = new AccessService(check);
        var theRun = await check.Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
        async Task<(bool View, bool Manage)> Can(ClaimsPrincipal p) => (await access.CanViewRunAsync(p, theRun), await access.CanManageRunAsync(p, theRun));

        Assert.Equal((true, false), await Can(Person("t@school.org")));                       // the requester
        Assert.Equal((true, false), await Can(Person("lead@school.org")));                    // an approver, even after deciding
        Assert.Equal((true, false), await Can(Person("head@school.org")));                    // final approver of its department
        Assert.Equal((true, true), await Can(Person("owner@school.org", Roles.Builder)));     // owns the workflow
        Assert.Equal((true, true), await Can(Person("admin@school.org", Roles.Admin)));
        Assert.Equal((false, false), await Can(Person("stranger@school.org")));
        Assert.Equal((false, false), await Can(Person("arthead@school.org")));                // another department's approver
        Assert.Equal((false, false), await Can(Person("otherowner@school.org", Roles.Builder))); // builder of a different workflow
        Assert.Equal((false, false), await Can(Person("", Roles.Staff)));                     // an account with no email matches nothing

        Assert.Null(await access.ManagedWorkflowIdsAsync(Person("admin@school.org", Roles.Admin)));   // null = everything
        Assert.Equal(new[] { tripId }, (await access.ManagedWorkflowIdsAsync(Person("owner@school.org", Roles.Builder)))!.ToArray());
        Assert.Equal(new[] { otherId }, (await access.ManagedWorkflowIdsAsync(Person("otherowner@school.org", Roles.Builder)))!.ToArray());
        Assert.Equal(new[] { tripId }, (await access.ManagedWorkflowIdsAsync(Person("owner@school.org", Roles.Staff)))!.ToArray());   // an owner without the Builder role
        Assert.Empty((await access.ManagedWorkflowIdsAsync(Person("stranger@school.org", Roles.Builder)))!);
        Assert.True(await access.OwnsAnyWorkflowAsync(Person("owner@school.org", Roles.Staff)));
        Assert.False(await access.OwnsAnyWorkflowAsync(Person("stranger@school.org", Roles.Builder)));
        Assert.Single(await access.DepartmentsApprovedByAsync(Person("head@school.org")));
        Assert.Empty(await access.DepartmentsApprovedByAsync(Person("t@school.org")));
    }
}
