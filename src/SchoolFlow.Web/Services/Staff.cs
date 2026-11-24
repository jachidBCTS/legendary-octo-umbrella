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
    /// <summary>The distinct, non-blank departments on the staff list (for Admin → Departments' starting list).</summary>
    Task<IReadOnlyList<string>> DepartmentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    /// <summary>The distinct (district, location) pairs on the staff list: which district each staff location is in.</summary>
    Task<IReadOnlyList<(string District, string Location)>> PlacesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(string, string)>>(Array.Empty<(string, string)>());
}

/// <summary>
/// Looks up the people a form's Staff fields name, before the form is checked (so the form check itself
/// stays free of database calls). A person working at several places is one entry, with every location
/// and district listed. Lookups are remembered for two minutes.
/// </summary>
public sealed class StaffLookup
{
    private readonly IStaffDirectory _staff;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StaffLookup> _log;

    public StaffLookup(IStaffDirectory staff, IMemoryCache cache, ILogger<StaffLookup> log)
    {
        _staff = staff;
        _cache = cache;
        _log = log;
    }

    public bool IsConfigured => _staff.IsConfigured;

    /// <summary>The staff entries the form's staff fields name in these answers (null when the form has no staff field).</summary>
    /// <param name="also">Addresses a default value may give (the person filling in the form, the requester), looked up too.</param>
    public async Task<SchoolFlow.Core.StaffDirectory?> ForAsync(IEnumerable<SchoolFlow.Core.FieldDef> fields, IDictionary<string, object?> input, CancellationToken ct,
        params string?[] also)
    {
        var keys = fields.Where(f => f.Type == SchoolFlow.Core.FieldTypes.Staff && !string.IsNullOrWhiteSpace(f.Key)).Select(f => f.Key).ToList();
        if (keys.Count == 0) return null;
        var known = new Dictionary<string, SchoolFlow.Core.StaffInfo>(StringComparer.OrdinalIgnoreCase);
        if (!_staff.IsConfigured) return new SchoolFlow.Core.StaffDirectory(false, known);
        var emails = keys.Select(k => input.FirstOrDefault(kv => string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase)).Value)
            .Select(SchoolFlow.Core.Forms.FormEvaluator.StaffEmailOf)
            .Concat(also.Select(a => (a ?? "").Trim().ToLowerInvariant()))
            .Where(e => e.Length is > 3 and <= 320).Distinct().Take(20);
        foreach (var email in emails)
            if (await FindAsync(email, ct) is { } info) known[email] = info;
        return new SchoolFlow.Core.StaffDirectory(true, known);
    }

    public async Task<SchoolFlow.Core.StaffInfo?> FindAsync(string email, CancellationToken ct)
    {
        email = email.Trim().ToLowerInvariant();
        if (_cache.TryGetValue("staff:" + email, out SchoolFlow.Core.StaffInfo? hit)) return hit;
        IReadOnlyList<StaffMember> rows;
        try { rows = await _staff.FindByEmailAsync(email, ct); }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not read the staff list for a staff field");
            return null;   // not cached: try again next time
        }
        var info = Merge(rows);
        _cache.Set("staff:" + email, info, TimeSpan.FromMinutes(2));
        return info;
    }

    /// <summary>One person from their staff rows (one row per place they work).</summary>
    public static SchoolFlow.Core.StaffInfo? Merge(IReadOnlyList<StaffMember> rows)
    {
        if (rows.Count == 0) return null;
        static string Join(IEnumerable<string> parts) => string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
        var first = rows[0];
        return new SchoolFlow.Core.StaffInfo(first.Email, first.Name, Join(rows.Select(r => r.Title)), Join(rows.Select(r => r.Location)),
            Join(rows.Select(r => r.Department)), Join(rows.Select(r => r.District)));
    }
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

    public async Task<IReadOnlyList<string>> DepartmentsAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var column = _col["Department"];
        command.CommandText = $"SELECT DISTINCT TOP (500) LTRIM(RTRIM(CAST({column} AS nvarchar(200)))) FROM {_view} WHERE {column} IS NOT NULL AND LTRIM(RTRIM(CAST({column} AS nvarchar(200)))) <> '' ORDER BY 1";
        command.CommandTimeout = 15;
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) if (!reader.IsDBNull(0)) list.Add(reader.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<(string District, string Location)>> PlacesAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        string Col(string part) => $"LTRIM(RTRIM(CAST({_col[part]} AS nvarchar(200))))";
        command.CommandText = $"SELECT DISTINCT TOP (2000) {Col("District")}, {Col("Location")} FROM {_view} WHERE {_col["Location"]} IS NOT NULL";
        command.CommandTimeout = 15;
        var list = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((reader.IsDBNull(0) ? "" : reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return list;
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

    public Task<IReadOnlyList<string>> DepartmentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Staff.Select(s => s.Department).Distinct().OrderBy(d => d).ToList());

    // The example people's places, plus a few of the district's real staff locations (they match the
    // example location matches in appsettings.Development.json).
    public Task<IReadOnlyList<(string District, string Location)>> PlacesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(string, string)>>(Staff.Select(s => (s.District, s.Location))
            .Concat(new[] { ("BCSS", "BLESHMAN"), ("BCTS", "ACADEMY"), ("BCSS", "VENTURE") }).Distinct().ToList());
}

public sealed class NoStaffDirectory : IStaffDirectory
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<StaffMember>> FindByEmailAsync(string email, CancellationToken ct) => Task.FromResult<IReadOnlyList<StaffMember>>(Array.Empty<StaffMember>());
    public Task<IReadOnlyList<StaffMember>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<StaffMember>>(Array.Empty<StaffMember>());
    public Task<string> TestAsync(CancellationToken ct) => Task.FromResult("Not connected: Staff:ConnectionString (or Students:ConnectionString) is not set in the settings file.");
}
