using System.Globalization;

namespace SchoolFlow.Web.Services;

/// <summary>
/// A starting list of locations from the settings file's location matches (<c>Students:Match:Locations</c>,
/// staff name → student-side name). The student-side names are the readable ones; where the student side
/// uses a number code (the Technical Schools' 20, 300…), the staff list's name is used instead.
/// </summary>
public static class StartingLocations
{
    public static IReadOnlyList<string> From(IConfiguration config) =>
        From(config.GetSection("Students:Match:Locations").GetChildren().Select(c => (c.Key, c.Value)));

    public static IReadOnlyList<string> From(IEnumerable<(string Staff, string? Student)> matches) =>
        WithStaffNames(matches).Select(p => p.Name).ToList();

    /// <summary>Each location name with the staff list's names for it (several staff places can share one name).</summary>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> StaffNames)> WithStaffNames(IConfiguration config) =>
        WithStaffNames(config.GetSection("Students:Match:Locations").GetChildren().Select(c => (c.Key, c.Value)));

    public static IReadOnlyList<(string Name, IReadOnlyList<string> StaffNames)> WithStaffNames(IEnumerable<(string Staff, string? Student)> matches)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var (staff, student) in matches)
        {
            var name = (student ?? "").Trim();
            if (name.Length == 0 || name.All(char.IsDigit)) name = Readable(staff);
            if (name.Length is 0 or > 200) continue;
            if (!byName.TryGetValue(name, out var keys)) { byName[name] = keys = new List<string>(); order.Add(name); }
            keys.Add(staff.Trim());
        }
        order.Sort(StringComparer.OrdinalIgnoreCase);
        return order.Select(n => (n, (IReadOnlyList<string>)byName[n])).ToList();
    }

    /// <summary>"APPLIED TECHNOLOGY HS (BCC)" → "Applied Technology HS (BCC)": longer words in title case, short ones (HS, BCC, TEC) kept.</summary>
    public static string Readable(string staffName)
    {
        var words = (staffName ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w =>
        {
            var letters = w.Count(char.IsLetter);
            return letters > 3 && w == w.ToUpperInvariant() ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w.ToLowerInvariant()) : w;
        }));
    }
}
