using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SchoolFlow.Core;

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
    /// <summary>Students whose last name, first name or ID starts with every one of the words (at most <paramref name="max"/>).</summary>
    Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct);
    /// <summary>The students with these IDs (unknown IDs are simply missing).</summary>
    Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct);
    /// <summary>A short check for Admin → Student access: how many students the roster offers, or why it cannot be read.</summary>
    Task<string> TestAsync(CancellationToken ct);
}

/// <summary>
/// Reads the roster from SQL Server: <c>Students:ConnectionString</c> (Windows sign-in as the web
/// server's account) and <c>Students:View</c>, a view with the columns StudentId, FirstName, LastName,
/// School, Grade and Academy (see docs/DEPLOY-IIS.md).
/// </summary>
public sealed class SqlServerStudentRoster : IStudentRoster
{
    public const string DefaultView = "dbo.BergenFormsStudents";
    private static readonly Regex SafeName = new(@"^(\[[A-Za-z0-9_ ]+\]|[A-Za-z0-9_]+)(\.(\[[A-Za-z0-9_ ]+\]|[A-Za-z0-9_]+)){0,2}$");
    private readonly string _connectionString;
    private readonly string _view;

    public SqlServerStudentRoster(string connectionString, string? view)
    {
        _connectionString = connectionString;
        _view = string.IsNullOrWhiteSpace(view) ? DefaultView : view.Trim();
        // The view name is put into the SQL text, so only a plain [schema.]name is accepted.
        if (!SafeName.IsMatch(_view)) throw new InvalidOperationException($"Students:View \"{_view}\" is not a valid view name (expected e.g. dbo.BergenFormsStudents).");
    }

    public bool IsConfigured => true;
    private const string Columns = "CAST(StudentId AS nvarchar(40)), CAST(FirstName AS nvarchar(100)), CAST(LastName AS nvarchar(100)), " +
                                   "CAST(School AS nvarchar(200)), CAST(Grade AS nvarchar(20)), CAST(Academy AS nvarchar(200))";

    /// <summary>Makes typed text safe inside LIKE: %, _ and [ match only themselves.</summary>
    private static string Like(string word) => word.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    public async Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct)
    {
        if (words.Count == 0) return Array.Empty<StudentInfo>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var where = string.Join(" AND ", words.Select((_, i) => $"(LastName LIKE @w{i} OR FirstName LIKE @w{i} OR CAST(StudentId AS nvarchar(40)) LIKE @w{i})"));
        command.CommandText = $"SELECT TOP (@max) {Columns} FROM {_view} WHERE {where} ORDER BY LastName, FirstName";
        command.CommandTimeout = 10;
        command.Parameters.AddWithValue("@max", Math.Clamp(max, 1, 50));
        for (var i = 0; i < words.Count; i++) command.Parameters.Add(new SqlParameter($"@w{i}", System.Data.SqlDbType.NVarChar, 120) { Value = Like(words[i]) });
        return await ReadAsync(command, ct);
    }

    public async Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, StudentInfo>(StringComparer.OrdinalIgnoreCase);
        var wanted = ids.Where(i => i.Length is > 0 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (wanted.Count == 0) return result;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM {_view} WHERE CAST(StudentId AS nvarchar(40)) IN ({string.Join(", ", wanted.Select((_, i) => "@id" + i))})";
        command.CommandTimeout = 10;
        for (var i = 0; i < wanted.Count; i++) command.Parameters.Add(new SqlParameter("@id" + i, System.Data.SqlDbType.NVarChar, 40) { Value = wanted[i] });
        foreach (var student in await ReadAsync(command, ct)) result.TryAdd(student.Id, student);
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
            if (id.Length > 0) list.Add(new StudentInfo(id, Text(1), Text(2), Text(3), Text(4), Text(5)));
        }
        return list;
    }
}

/// <summary>A few made-up students for a development site. Never used on a real site.</summary>
public sealed class SampleStudentRoster : IStudentRoster
{
    private static readonly StudentInfo[] Students =
    {
        new("20451", "Maya", "Okafor", "Teterboro", "10", "Aerospace"),
        new("20452", "Leo", "Okon", "Paramus", "11", "Culinary"),
        new("20453", "Ana", "Ruiz", "Hackensack", "9", "Health Sciences"),
    };

    public bool IsConfigured => true;

    public Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StudentInfo>>(Students
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
    public Task<IReadOnlyList<StudentInfo>> SearchAsync(IReadOnlyList<string> words, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<StudentInfo>>(Array.Empty<StudentInfo>());
    public Task<IReadOnlyDictionary<string, StudentInfo>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, StudentInfo>>(new Dictionary<string, StudentInfo>());
    public Task<string> TestAsync(CancellationToken ct) => Task.FromResult("Not connected: Students:ConnectionString is not set in the settings file.");
}
