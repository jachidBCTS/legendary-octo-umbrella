namespace SchoolFlow.Core;

/// <summary>
/// People's names as Greenlight shows them: "First Last". The district's Entra display names are
/// "Last_First" (owner, Nov 23, 2026); "Last, First" is flipped too. Anything else is kept as given.
/// </summary>
public static class PersonName
{
    public static string Tidy(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Contains('@')) return text;
        var cut = text.IndexOf('_');
        if (cut < 0 && text.Count(c => c == ',') == 1) cut = text.IndexOf(',');
        if (cut <= 0 || cut == text.Length - 1) return text.Replace('_', ' ').Trim();
        var last = text[..cut].Trim();
        var first = text[(cut + 1)..].Replace('_', ' ').Trim();
        if (last.Length == 0 || first.Length == 0) return text.Replace('_', ' ').Trim();
        return first + " " + last;
    }

    /// <summary>Given name and surname when both are known, otherwise the tidied display name.</summary>
    public static string From(string? given, string? surname, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(given) && !string.IsNullOrWhiteSpace(surname)) return given.Trim() + " " + surname.Trim();
        return Tidy(displayName);
    }
}
