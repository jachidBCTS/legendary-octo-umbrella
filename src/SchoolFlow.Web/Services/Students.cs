using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>
/// The student roster. It is read live from the district's own SQL Server (never copied into this
/// app), through a view that offers only the six details the owner allows: student ID, first name,
/// last name, school, grade and academy.
/// </summary>
public interface IStudentRoster
{
    /// <summary>False when no roster is connected (the Student field then finds nobody).</summary>
    bool IsConfigured { get; }
    /// <summary>
    /// Students whose last name, first name or ID starts with every one of the words (at most
    /// <paramref name="max"/>), among those <paramref name="filter"/> allows.
    /// </summary>
    Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, StudentFilter filter, CancellationToken ct);
    /// <summary>The students with these IDs (unknown IDs are simply missing).</summary>
    Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct);
    /// <summary>A short check for Admin → Student access: how many students the roster offers, or why it cannot be read.</summary>
    Task<string> TestAsync(CancellationToken ct);
}

/// <summary>
/// Which students someone may see: everyone, or those at certain locations or in certain districts
/// (values as the student roster writes them). Empty = nobody.
/// </summary>
public sealed record StudentFilter(bool All, IReadOnlyCollection<string> Locations, IReadOnlyCollection<string> Districts)
{
    public static readonly StudentFilter Everyone = new(true, Array.Empty<string>(), Array.Empty<string>());
    public static readonly StudentFilter Nobody = new(false, Array.Empty<string>(), Array.Empty<string>());
    public bool IsEmpty => !All && Locations.Count == 0 && Districts.Count == 0;

    private static bool In(IReadOnlyCollection<string> list, string value) =>
        value.Trim().Length > 0 && list.Any(v => string.Equals(v.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool Allows(StudentInfo student) => All || In(Locations, student.School) || In(Districts, student.District);
}

/// <summary>
/// Reads the roster from SQL Server: <c>Students:ConnectionString</c> (Windows sign-in as the web
/// server's account), <c>Students:View</c>, and the view's column names under <c>Students:Columns</c>
/// (StudentId, FirstName, LastName, School, Grade, Academy, District — each defaults to that name). No
/// other column is ever read; District only decides who may see a student. See docs/DEPLOY-IIS.md.
/// </summary>
public sealed class SqlServerStudentRoster : IStudentRoster
{
    public const string DefaultView = "dbo.BergenFormsStudents";
    private static readonly Regex SafeName = new(@"^(\[[A-Za-z0-9_ ]+\]|[A-Za-z0-9_]+)(\.(\[[A-Za-z0-9_ ]+\]|[A-Za-z0-9_]+)){0,2}$");
    private static readonly Regex SafeColumn = new(@"^[A-Za-z0-9_ ]{1,128}$");
    public static readonly string[] Parts = { "StudentId", "FirstName", "LastName", "School", "Grade", "Academy", "District" };
    private readonly string _connectionString;
    private readonly string _view;
    private readonly Dictionary<string, string> _col;   // part → [column]
    private readonly string _columns;

    public SqlServerStudentRoster(string connectionString, string? view, IReadOnlyDictionary<string, string?>? columns = null)
    {
        _connectionString = connectionString;
        _view = string.IsNullOrWhiteSpace(view) ? DefaultView : view.Trim();
        // The view and column names are put into the SQL text, so only plain names are accepted.
        if (!SafeName.IsMatch(_view)) throw new InvalidOperationException($"Students:View \"{_view}\" is not a valid view name (expected e.g. dbo.BergenFormsStudents).");
        _col = new Dictionary<string, string>();
        foreach (var part in Parts)
        {
            var name = columns is not null && columns.TryGetValue(part, out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim().Trim('[', ']') : part;
            if (!SafeColumn.IsMatch(name)) throw new InvalidOperationException($"Students:Columns:{part} \"{name}\" is not a valid column name.");
            _col[part] = "[" + name + "]";
        }
        _columns = $"CAST({_col["StudentId"]} AS nvarchar(40)), CAST({_col["FirstName"]} AS nvarchar(100)), CAST({_col["LastName"]} AS nvarchar(100)), " +
                   $"CAST({_col["School"]} AS nvarchar(200)), CAST({_col["Grade"]} AS nvarchar(20)), CAST({_col["Academy"]} AS nvarchar(200)), CAST({_col["District"]} AS nvarchar(200))";
    }

    public bool IsConfigured => true;

    /// <summary>Makes typed text safe inside LIKE: %, _ and [ match only themselves.</summary>
    private static string Like(string word) => word.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    public async Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, StudentFilter filter, CancellationToken ct)
    {
        if (words.Count == 0 || filter.IsEmpty) return Array.Empty<StudentInfo>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var where = string.Join(" AND ", words.Select((_, i) => $"({_col["LastName"]} LIKE @w{i} OR {_col["FirstName"]} LIKE @w{i} OR CAST({_col["StudentId"]} AS nvarchar(40)) LIKE @w{i})"));
        if (!filter.All)
        {
            // Only students at the person's own location(s), or in their district(s).
            var places = new List<string>();
            var locations = filter.Locations.Take(20).ToList();
            var districts = filter.Districts.Take(20).ToList();
            if (locations.Count > 0) places.Add($"{_col["School"]} IN ({string.Join(", ", locations.Select((_, i) => "@l" + i))})");
            if (districts.Count > 0) places.Add($"{_col["District"]} IN ({string.Join(", ", districts.Select((_, i) => "@d" + i))})");
            where += " AND (" + string.Join(" OR ", places) + ")";
            for (var i = 0; i < locations.Count; i++) command.Parameters.Add(new SqlParameter("@l" + i, System.Data.SqlDbType.NVarChar, 200) { Value = locations[i] });
            for (var i = 0; i < districts.Count; i++) command.Parameters.Add(new SqlParameter("@d" + i, System.Data.SqlDbType.NVarChar, 200) { Value = districts[i] });
        }
        command.CommandText = $"SELECT TOP (@max) {_columns} FROM {_view} WHERE {where} ORDER BY {_col["LastName"]}, {_col["FirstName"]}";
        command.CommandTimeout = 10;
        command.Parameters.AddWithValue("@max", Math.Clamp(max, 1, 50));
        for (var i = 0; i < words.Count; i++) command.Parameters.Add(new SqlParameter($"@w{i}", System.Data.SqlDbType.NVarChar, 120) { Value = Like(words[i]) });
        return await ReadAsync(command, ct);
    }

    public async Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, StudentInfo>(StringComparer.OrdinalIgnoreCase);
        var wanted = ids.Where(i => i.Length is > 0 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return result;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        // In batches of 100, so an export of many requests stays within SQL Server's parameter limits.
        foreach (var batch in wanted.Chunk(100))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {_columns} FROM {_view} WHERE CAST({_col["StudentId"]} AS nvarchar(40)) IN ({string.Join(", ", batch.Select((_, i) => "@id" + i))})";
            command.CommandTimeout = 15;
            for (var i = 0; i < batch.Length; i++) command.Parameters.Add(new SqlParameter("@id" + i, System.Data.SqlDbType.NVarChar, 40) { Value = batch[i] });
            foreach (var student in await ReadAsync(command, ct)) result.TryAdd(student.Id, student);
        }
        return result;
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
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            return $"Connected: {count:N0} students in {_view} on {connection.DataSource} / {connection.Database}.";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return "Could not read the roster: " + ex.Message;
        }
    }

    private static async Task<IReadOnlyList<StudentInfo>> ReadAsync(SqlCommand command, CancellationToken ct)
    {
        var list = new List<StudentInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        string Text(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i).Trim();
        while (await reader.ReadAsync(ct))
        {
            var id = Text(0);
            if (id.Length > 0) list.Add(new StudentInfo(id, Text(1), Text(2), Text(3), Text(4), Text(5)) { District = Text(6) });
        }
        return list;
    }
}

/// <summary>A few made-up students for a development site. Never used on a real site.</summary>
public sealed class SampleStudentRoster : IStudentRoster
{
    private static readonly StudentInfo[] Students =
    {
        new("20451", "Maya", "Okafor", "Teterboro", "10", "Aerospace") { District = "Bergen County Technical Schools" },
        new("20452", "Leo", "Okon", "Paramus", "11", "Culinary") { District = "Bergen County Technical Schools" },
        new("20453", "Ana", "Ruiz", "Hackensack", "9", "Health Sciences") { District = "Bergen County Special Services" },
    };

    public bool IsConfigured => true;

    public Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, StudentFilter filter, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StudentInfo>>(Students
            .Where(filter.Allows)
            .Where(s => words.All(w => s.LastName.StartsWith(w, StringComparison.OrdinalIgnoreCase) || s.FirstName.StartsWith(w, StringComparison.OrdinalIgnoreCase) || s.Id.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.LastName).ThenBy(s => s.FirstName).Take(max).ToList());

    public Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, StudentInfo>>(Students.Where(s => ids.Contains(s.Id, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase));

    public Task<string> TestAsync(CancellationToken ct) => Task.FromResult($"Example roster ({Students.Length} made-up students): this is a test site.");
}

/// <summary>No roster connected: the Student field finds nobody, and Admin → Student access says how to connect one.</summary>
public sealed class NoStudentRoster : IStudentRoster
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, StudentFilter filter, CancellationToken ct) => Task.FromResult<IReadOnlyList<StudentInfo>>(Array.Empty<StudentInfo>());
    public Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, StudentInfo>>(new Dictionary<string, StudentInfo>());
    public Task<string> TestAsync(CancellationToken ct) => Task.FromResult("Not connected: Students:ConnectionString is not set in the settings file.");
}

/// <summary>
/// Requests store only a student's number (<see cref="SchoolFlow.Core.Forms.StudentData.Minimize"/>).
/// This puts the details back, from the live roster, when they are needed: to show the request to
/// someone with student access, or to let a later step's formulas use them. Nothing is saved back.
/// </summary>
public sealed class StudentAnswers
{
    private readonly IStudentRoster _roster;
    private readonly SchoolCalendar _calendar;

    public StudentAnswers(IStudentRoster roster, SchoolCalendar calendar)
    {
        _roster = roster;
        _calendar = calendar;
    }

    private static List<string> StudentKeys(WorkflowDefinition def) =>
        def.Form.Fields.Where(f => f.Type == FieldTypes.Student && !string.IsNullOrWhiteSpace(f.Key)).Select(f => f.Key).ToList();

    /// <summary>The student numbers stored on a request.</summary>
    public static IEnumerable<string> IdsOf(WorkflowDefinition def, IDictionary<string, object?> context) =>
        StudentKeys(def).Select(k => SchoolFlow.Core.Forms.FormEvaluator.StudentIdOf(context.TryGetValue(k, out var v) ? v : null)).Where(id => id.Length > 0);

    public Task<IReadOnlyDictionary<string, StudentInfo>> LookupAsync(IEnumerable<string> ids, CancellationToken ct) =>
        _roster.FindAsync(ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct);

    /// <summary>Looks the request's students up and puts their details (and anything worked out from them) back into <paramref name="context"/>.</summary>
    public async Task RestoreAsync(WorkflowDefinition def, Dictionary<string, object?> context, CancellationToken ct)
    {
        var ids = IdsOf(def, context).ToList();
        if (ids.Count == 0) return;
        await RestoreAsync(def, context, await LookupAsync(ids, ct), ct);
    }

    /// <summary>The same, with students already looked up (for many requests at once).</summary>
    public async Task RestoreAsync(WorkflowDefinition def, Dictionary<string, object?> context, IReadOnlyDictionary<string, StudentInfo> known, CancellationToken ct)
    {
        var keys = StudentKeys(def);
        if (keys.Count == 0) return;
        // Re-evaluating the form gives each student field its full record from the roster, and works
        // out again whatever was calculated from it; only those answers are taken from the result.
        var tainted = SchoolFlow.Core.Forms.StudentData.KeysOf(def);
        var ambient = SchoolFlow.Core.Expressions.Values.NewDict();
        if (context.TryGetValue("submitter", out var submitter)) ambient["submitter"] = submitter;
        ambient["rates"] = await _calendar.RatesAsync(ct);
        var result = SchoolFlow.Core.Forms.FormEvaluator.Evaluate(def.Form, context, ambient, closedDays: await _calendar.ClosedDaysAsync(ct),
            students: new StudentDirectory(true, known), locked: context);
        foreach (var key in tainted.Where(k => def.Form.Fields.Any(f => string.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase))))
        {
            var isStudent = keys.Contains(key, StringComparer.OrdinalIgnoreCase);
            // A student no longer on the roster keeps their number, so the request still says who it was about.
            if (isStudent && (!result.Values.TryGetValue(key, out var full) || full is null)) continue;
            if (result.Values.TryGetValue(key, out var value)) context[key] = value;
        }
    }
}

/// <summary>
/// Remembers students looked up by number for two minutes. A form with a Student field is checked again
/// every time the person pauses while typing, and each check looks its student up; this keeps those
/// checks from going to the district's SQL Server every time. Searches are never cached (they are the
/// person's own typing), and nothing is written anywhere: the memory goes when the site restarts.
/// </summary>
public sealed class CachedStudentRoster : IStudentRoster
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);
    private const int MaxEntries = 5000;
    private readonly IStudentRoster _inner;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (StudentInfo? Student, DateTime Until)> _byId = new(StringComparer.OrdinalIgnoreCase);

    public CachedStudentRoster(IStudentRoster inner) => _inner = inner;

    public bool IsConfigured => _inner.IsConfigured;
    public Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, StudentFilter filter, CancellationToken ct) => _inner.SearchAsync(words, max, filter, ct);
    public Task<string> TestAsync(CancellationToken ct) => _inner.TestAsync(ct);

    public async Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var result = new Dictionary<string, StudentInfo>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_byId.TryGetValue(id, out var hit) && hit.Until > now) { if (hit.Student is not null) result[id] = hit.Student; }
            else missing.Add(id);
        }
        if (missing.Count == 0) return result;
        var found = await _inner.FindAsync(missing, ct);
        if (_byId.Count > MaxEntries) _byId.Clear();
        foreach (var id in missing)
        {
            found.TryGetValue(id, out var student);
            _byId[id] = (student, now + Keep);     // "not on the roster" is remembered too, briefly
            if (student is not null) result[id] = student;
        }
        return result;
    }
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
