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

    public static IReadOnlyList<string> From(IEnumerable<(string Staff, string? Student)> matches)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var (staff, student) in matches)
        {
            var name = (student ?? "").Trim();
            if (name.Length == 0 || name.All(char.IsDigit)) name = Readable(staff);
            if (name.Length is > 0 and <= 200 && seen.Add(name)) list.Add(name);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
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
