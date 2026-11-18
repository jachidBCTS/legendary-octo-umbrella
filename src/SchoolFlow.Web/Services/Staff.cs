using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>One row of the district's staff list (a person working at several locations has several rows).</summary>
public sealed record StaffMember(string StaffId, string FirstName, string LastName, string Title, string District, string Location, string Department, string Email)
{
    public string Name => string.Join(" ", new[] { FirstName, LastName }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// The district's list of active staff, read live from its SQL Server. Used to know where a person works
/// (their location and district decide which students they may see) and to suggest staff email addresses.
/// </summary>
public interface IStaffDirectory
{
    bool IsConfigured { get; }
    /// <summary>The staff rows for a work email (empty when the person is not on the list).</summary>
    Task<IReadOnlyList<StaffMember>> FindByEmailAsync(string email, CancellationToken ct);
    /// <summary>Staff whose first name, last name or email starts with every one of the words.</summary>
    Task<IReadOnlyList<StaffMember>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct);
    Task<string> TestAsync(CancellationToken ct);
}

/// <summary>
/// Reads <c>Staff:View</c> (default dbo.ActiveStaff) through <c>Staff:ConnectionString</c> (default: the
/// student roster's). Column names under <c>Staff:Columns</c>; the defaults are the district's own.
/// </summary>
public sealed class SqlServerStaffDirectory : IStaffDirectory
{
    public const string DefaultView = "dbo.ActiveStaff";
    public static readonly (string Part, string Default)[] Parts =
    {
        ("StaffId", "StaffID"), ("FirstName", "FirstName"), ("LastName", "LastName"), ("Title", "Title"),
        ("District", "DistrictShort"), ("Location", "Location"), ("Department", "Department"), ("Email", "WorkEmail"),
    };
    private static readonly Regex SafeName = new(@"^(\[[A-Za-z0-9_ ]+\]|[A-Za-z0-9_]+)(\.(\[[A-Za-z0-9_ ]+\]|[A-Za-z0-9_]+)){0,2}$");
    private static readonly Regex SafeColumn = new(@"^[A-Za-z0-9_ ]{1,128}$");
    private readonly string _connectionString;
    private readonly string _view;
    private readonly Dictionary<string, string> _col = new();
    private readonly string _columns;

    public SqlServerStaffDirectory(string connectionString, string? view, IReadOnlyDictionary<string, string?>? columns = null)
    {
        _connectionString = connectionString;
        _view = string.IsNullOrWhiteSpace(view) ? DefaultView : view.Trim();
        if (!SafeName.IsMatch(_view)) throw new InvalidOperationException($"Staff:View \"{_view}\" is not a valid view name (expected e.g. dbo.ActiveStaff).");
        foreach (var (part, fallback) in Parts)
        {
            var name = columns is not null && columns.TryGetValue(part, out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim().Trim('[', ']') : fallback;
            if (!SafeColumn.IsMatch(name)) throw new InvalidOperationException($"Staff:Columns:{part} \"{name}\" is not a valid column name.");
            _col[part] = "[" + name + "]";
        }
        _columns = string.Join(", ", Parts.Select(p => $"CAST({_col[p.Part]} AS nvarchar(320))"));
    }

    public bool IsConfigured => true;

    public async Task<IReadOnlyList<StaffMember>> FindByEmailAsync(string email, CancellationToken ct)
    {
        email = email.Trim();
        if (email.Length is 0 or > 320) return Array.Empty<StaffMember>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {_columns} FROM {_view} WHERE {_col["Email"]} = @email";
        command.CommandTimeout = 10;
        command.Parameters.Add(new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 320) { Value = email });
        return await ReadAsync(command, ct);
    }

    public async Task<IReadOnlyList<StaffMember>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct)
    {
        if (words.Count == 0) return Array.Empty<StaffMember>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var where = string.Join(" AND ", words.Select((_, i) => $"({_col["LastName"]} LIKE @w{i} OR {_col["FirstName"]} LIKE @w{i} OR {_col["Email"]} LIKE @w{i})"));
        command.CommandText = $"SELECT TOP (@max) {_columns} FROM {_view} WHERE {where} AND {_col["Email"]} LIKE '%@%' ORDER BY {_col["LastName"]}, {_col["FirstName"]}";
        command.CommandTimeout = 10;
        command.Parameters.AddWithValue("@max", Math.Clamp(max, 1, 50));
        for (var i = 0; i < words.Count; i++)
            command.Parameters.Add(new SqlParameter($"@w{i}", System.Data.SqlDbType.NVarChar, 120)
            { Value = words[i].Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%" });
        return await ReadAsync(command, ct);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {_view}";
            command.CommandTimeout = 15;
            return $"Connected: {Convert.ToInt64(await command.ExecuteScalarAsync(ct)):N0} staff rows in {_view}.";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return "Could not read the staff list: " + ex.Message;
        }
    }

    private static async Task<IReadOnlyList<StaffMember>> ReadAsync(SqlCommand command, CancellationToken ct)
    {
        var list = new List<StaffMember>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        string T(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i).Trim();
        while (await reader.ReadAsync(ct))
            list.Add(new StaffMember(T(0), T(1), T(2), T(3), T(4), T(5), T(6), T(7).ToLowerInvariant()));
        return list;
    }
}

/// <summary>Example staff for a development site (matching its sign-in people). Never used on a real site.</summary>
public sealed class SampleStaffDirectory : IStaffDirectory
{
    private static readonly StaffMember[] Staff =
    {
        new("1001", "Taylor", "Teacher", "Science Teacher", "BCTS", "Teterboro", "Science", "teacher@example.org"),
        new("1002", "Pat", "Principal", "Principal", "BCTS", "Teterboro", "Administration", "principal@example.org"),
        new("1003", "Avery", "Admin", "Technology Coordinator", "BCTS", "Paramus", "Technology", "admin@example.org"),
        new("1004", "Bailey", "Builder", "Athletic Director", "BCSS", "Hackensack", "Athletics", "builder@example.org"),
    };

    public bool IsConfigured => true;

    public Task<IReadOnlyList<StaffMember>> FindByEmailAsync(string email, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StaffMember>>(Staff.Where(s => string.Equals(s.Email, email.Trim(), StringComparison.OrdinalIgnoreCase)).ToList());

    public Task<IReadOnlyList<StaffMember>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StaffMember>>(Staff.Where(s => words.All(w => s.FirstName.StartsWith(w, StringComparison.OrdinalIgnoreCase)
            || s.LastName.StartsWith(w, StringComparison.OrdinalIgnoreCase) || s.Email.StartsWith(w, StringComparison.OrdinalIgnoreCase))).Take(max).ToList());

    public Task<string> TestAsync(CancellationToken ct) => Task.FromResult($"Example staff list ({Staff.Length} made-up people): this is a test site.");
}

public sealed class NoStaffDirectory : IStaffDirectory
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<StaffMember>> FindByEmailAsync(string email, CancellationToken ct) => Task.FromResult<IReadOnlyList<StaffMember>>(Array.Empty<StaffMember>());
    public Task<IReadOnlyList<StaffMember>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<StaffMember>>(Array.Empty<StaffMember>());
    public Task<string> TestAsync(CancellationToken ct) => Task.FromResult("Not connected: Staff:ConnectionString (or Students:ConnectionString) is not set in the settings file.");
}

/// <summary>
/// Which students a person may see (owner, Nov 18, 2026): nobody without the student access switch; with
/// it, the students at their own location(s) from the staff list, or their whole district, or every
/// district, as an admin sets on Admin → Student access. The staff list and the student roster spell
/// places differently, so <c>Students:Match:Locations</c> and <c>Students:Match:Districts</c> translate
/// the staff list's values into the roster's (a value with no entry is used as it is).
/// </summary>
public sealed class StudentScopeService
{
    private readonly AppDbContext _db;
    private readonly IStaffDirectory _staff;
    private readonly IMemoryCache _cache;
    private readonly Dictionary<string, string> _locations;
    private readonly Dictionary<string, string> _districts;

    public StudentScopeService(AppDbContext db, IStaffDirectory staff, IMemoryCache cache, IConfiguration config)
    {
        _db = db;
        _staff = staff;
        _cache = cache;
        Dictionary<string, string> Map(string section) => config.GetSection(section).GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key.Trim(), c => c.Value!.Trim(), StringComparer.OrdinalIgnoreCase);
        _locations = Map("Students:Match:Locations");
        _districts = Map("Students:Match:Districts");
    }

    public string RosterLocation(string staffLocation) => _locations.TryGetValue(staffLocation.Trim(), out var v) ? v : staffLocation.Trim();
    public string RosterDistrict(string staffDistrict) => _districts.TryGetValue(staffDistrict.Trim(), out var v) ? v : staffDistrict.Trim();

    /// <summary>Where the staff list says this person works (cached for 10 minutes).</summary>
    public async Task<IReadOnlyList<StaffMember>> PlacesAsync(string email, CancellationToken ct = default)
    {
        email = email.Trim().ToLowerInvariant();
        if (email.Length == 0) return Array.Empty<StaffMember>();
        if (_cache.TryGetValue("staff:" + email, out IReadOnlyList<StaffMember>? cached) && cached is not null) return cached;
        var rows = await _staff.FindByEmailAsync(email, ct);
        _cache.Set("staff:" + email, rows, TimeSpan.FromMinutes(10));
        return rows;
    }

    /// <summary>The students this person may see, or null when they have no student access at all.</summary>
    public async Task<StudentFilter?> FilterForAsync(string email, CancellationToken ct = default)
    {
        if (email.Length == 0) return null;
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !user.StudentAccess) return null;
        var scope = StudentScopes.Normalize(user.StudentScope);
        if (scope == StudentScopes.All) return StudentFilter.Everyone;
        var places = await PlacesAsync(email, ct);
        return scope == StudentScopes.District
            ? new StudentFilter(false, Array.Empty<string>(), places.Select(p => RosterDistrict(p.District)).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            : new StudentFilter(false, places.Select(p => RosterLocation(p.Location)).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Array.Empty<string>());
    }
}

/// <summary>The rule for seeing the student on a request (shared by the request page and exports).</summary>
public static class StudentVisibility
{
    /// <summary>Whether this person was asked to act on the request (approver, task owner, stand-in, escalation) or did.</summary>
    public static bool Involves(ApprovalTask task, string email) =>
        email.Length > 0 && (task.IsAssignedTo(email) || task.StandsInFor(email) is not null || task.IsEscalatedTo(email)
            || string.Equals(task.DecidedBy, email, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// People asked to act on the request see its students (owner's choice), as does the requester if they
    /// have student access; anyone else needs student access covering every student on it.
    /// </summary>
    public static bool MaySee(bool actsOnIt, bool isRequester, StudentFilter? filter, IReadOnlyCollection<string> ids,
        IReadOnlyDictionary<string, SchoolFlow.Core.StudentInfo> known)
    {
        if (actsOnIt) return true;
        if (filter is null) return false;
        if (isRequester || filter.All) return true;
        return ids.Count > 0 && ids.All(id => known.TryGetValue(id, out var student) && filter.Allows(student));
    }
}
