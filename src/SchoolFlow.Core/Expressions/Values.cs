using System.Globalization;

namespace SchoolFlow.Core.Expressions;

/// <summary>Raised for any problem parsing or evaluating a formula. Message is safe to show to builders.</summary>
public class ExpressionException : Exception
{
    public ExpressionException(string message) : base(message) { }
}

/// <summary>
/// Runtime value model for formulas: null, bool, decimal, string, DateTime,
/// List&lt;object?&gt; and Dictionary&lt;string, object?&gt; (case-insensitive keys).
/// </summary>
public static class Values
{
    public static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-US");

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
        "M/d/yyyy", "MM/dd/yyyy",
    };

    public static Dictionary<string, object?> NewDict() => new(StringComparer.OrdinalIgnoreCase);

    public static bool IsBlank(object? v) => v switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        List<object?> l => l.Count == 0,
        _ => false,
    };

    /// <summary>Normalises numeric CLR types to decimal so the rest of the engine sees one number type.</summary>
    public static object? Normalize(object? v) => v switch
    {
        int i => (decimal)i,
        long l => (decimal)l,
        double d => (decimal)d,
        float f => (decimal)f,
        _ => v,
    };

    public static bool TryToNumber(object? v, out decimal result)
    {
        switch (v)
        {
            case null:
                result = 0; return true;
            case decimal d:
                result = d; return true;
            case int i:
                result = i; return true;
            case long l:
                result = l; return true;
            case double db:
                try { result = (decimal)db; return true; } catch (OverflowException) { result = 0; return false; }
            case bool b:
                result = b ? 1 : 0; return true;
            case string s:
                if (string.IsNullOrWhiteSpace(s)) { result = 0; return true; }
                var cleaned = s.Trim().Replace(",", "").TrimStart('$');
                return decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0; return false;
        }
    }

    public static decimal ToNumber(object? v)
    {
        if (TryToNumber(v, out var d)) return d;
        throw new ExpressionException($"'{Describe(v)}' is not a number");
    }

    public static bool ToBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        decimal d => d != 0,
        string s => s.Trim().ToLowerInvariant() switch
        {
            "" or "false" or "no" or "0" or "off" => false,
            _ => true,
        },
        List<object?> l => l.Count > 0,
        _ => true,
    };

    public static string ToText(object? v)
    {
        switch (v)
        {
            case null: return "";
            case string s: return s;
            case bool b: return b ? "true" : "false";
            case decimal d: return FormatNumber(d);
            case DateTime dt:
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            case List<object?> l: return string.Join(", ", l.Select(ToText));
            case Dictionary<string, object?> dict:
                // Lookup values are {id, name}; show the human-readable part.
                if (dict.TryGetValue("name", out var n) && !IsBlank(n)) return ToText(n);
                if (dict.TryGetValue("id", out var id)) return ToText(id);
                return string.Join(", ", dict.Select(kv => kv.Key + ": " + ToText(kv.Value)));
            default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }
    }

    /// <summary>Like <see cref="ToText"/>, but a record with an email (a Staff answer) gives the email, and a list gives each one's.</summary>
    public static string ToAddressText(object? v) => v switch
    {
        Dictionary<string, object?> dict when dict.TryGetValue("email", out var e) && !IsBlank(e) => ToText(e),
        List<object?> l => string.Join(", ", l.Select(ToAddressText)),
        _ => ToText(v),
    };

    public static string FormatNumber(decimal d)
    {
        var s = d.ToString("0.############################", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    public static bool TryToDate(object? v, out DateTime result)
    {
        switch (v)
        {
            case DateTime dt:
                result = dt; return true;
            case string s when !string.IsNullOrWhiteSpace(s):
                s = s.Trim();
                if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                    return true;
                // Full ISO-8601 with offset / Z (Freshservice timestamps). Convert to server-local time.
                if (s.Length >= 20 && s[4] == '-' && s[10] == 'T' &&
                    DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                {
                    result = DateTime.SpecifyKind(dto.LocalDateTime, DateTimeKind.Unspecified);
                    return true;
                }
                result = default; return false;
            default:
                result = default; return false;
        }
    }

    public static DateTime ToDate(object? v)
    {
        if (TryToDate(v, out var d)) return d;
        throw new ExpressionException($"'{Describe(v)}' is not a date");
    }

    private static bool IsStrictIsoDate(object? v) =>
        v is string s && s.Length >= 10 && s[4] == '-' && s[7] == '-' && TryToDate(s, out _);

    public static bool AreEqual(object? a, object? b)
    {
        a = Normalize(a); b = Normalize(b);
        var aBlank = a is null || (a is string sa && sa.Length == 0);
        var bBlank = b is null || (b is string sb && sb.Length == 0);
        if (aBlank || bBlank) return aBlank && bBlank;

        if (a is bool ba && b is bool bb) return ba == bb;
        if (a is DateTime || b is DateTime)
            return TryToDate(a, out var da) && TryToDate(b, out var db) && da == db;
        if (a is decimal || b is decimal)
        {
            if (a is bool || b is bool) return ToBool(a) == ToBool(b);
            return TryToNumber(a, out var na) && TryToNumber(b, out var nb) && na == nb;
        }
        if (a is List<object?> la && b is List<object?> lb)
            return la.Count == lb.Count && la.Zip(lb).All(p => AreEqual(p.First, p.Second));
        if (a is List<object?> || b is List<object?>) return false;
        return string.Equals(ToText(a).Trim(), ToText(b).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ordering comparison. Returns null when the comparison is undefined because a date
    /// side is blank (so "end &gt; start" is simply false until both are filled in).
    /// </summary>
    public static int? Compare(object? a, object? b)
    {
        a = Normalize(a); b = Normalize(b);

        if (a is DateTime || b is DateTime || (IsStrictIsoDate(a) && IsStrictIsoDate(b)))
        {
            if (IsBlank(a) || IsBlank(b)) return null;
            return ToDate(a).CompareTo(ToDate(b));
        }
        if (a is decimal || b is decimal || a is bool || b is bool)
            return ToNumber(a).CompareTo(ToNumber(b));
        if (a is List<object?> || b is List<object?> || a is Dictionary<string, object?> || b is Dictionary<string, object?>)
            throw new ExpressionException("Lists and records cannot be compared with < or >");

        // Both are text or blank.
        var ta = ToText(a).Trim(); var tb = ToText(b).Trim();
        if (ta.Length > 0 && tb.Length > 0 && TryToNumber(ta, out var na) && TryToNumber(tb, out var nb))
            return na.CompareTo(nb);
        return Math.Sign(string.Compare(ta, tb, StringComparison.OrdinalIgnoreCase));
    }

    public static string Describe(object? v)
    {
        var t = ToText(v);
        return t.Length > 40 ? t[..40] + "…" : t;
    }

    /// <summary>Flattens nested lists into a single sequence of scalar values.</summary>
    public static IEnumerable<object?> Flatten(IEnumerable<object?> items)
    {
        foreach (var item in items)
        {
            if (item is List<object?> inner)
                foreach (var x in Flatten(inner)) yield return x;
            else
                yield return item;
        }
    }
}
