using System.Text.RegularExpressions;

namespace SchoolFlow.Core.Expressions;

public sealed record FunctionInfo(string Name, string Signature, string Description, string Category);

/// <summary>Built-in formula functions. Names are case-insensitive.</summary>
public static class Functions
{
    private sealed record Def(int Min, int Max, Func<IReadOnlyList<Node>, Scope, object?> Impl, FunctionInfo Info);

    private static readonly Dictionary<string, Def> Table = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<FunctionInfo> Catalog { get; }

    public static bool Exists(string name) => Table.ContainsKey(name);

    internal static object? Invoke(CallNode call, Scope scope)
    {
        if (!Table.TryGetValue(call.Name, out var def))
            throw new ExpressionException($"Unknown function {call.Name}()");
        CheckArity(call.Name, call.Args.Count);
        try
        {
            return def.Impl(call.Args, scope);
        }
        catch (ExpressionException ex) when (!ex.Message.Contains("()"))
        {
            throw new ExpressionException($"{call.Name}(): {ex.Message}");
        }
    }

    /// <summary>Throws when the argument count is wrong; used at design time as well as at run time.</summary>
    public static void CheckArity(string name, int count)
    {
        if (!Table.TryGetValue(name, out var def))
            throw new ExpressionException($"Unknown function {name.ToUpperInvariant()}()");
        if (count < def.Min || count > def.Max)
        {
            var expected = def.Min == def.Max ? $"{def.Min}" : def.Max == int.MaxValue ? $"at least {def.Min}" : $"{def.Min} to {def.Max}";
            throw new ExpressionException($"{name.ToUpperInvariant()}() takes {expected} value(s) — usage: {def.Info.Signature}");
        }
    }

    // ---- registration helpers ---------------------------------------------------------

    private static void Lazy(string name, int min, int max, string sig, string desc, string cat,
        Func<IReadOnlyList<Node>, Scope, object?> impl) =>
        Table[name] = new Def(min, max, impl, new FunctionInfo(name, sig, desc, cat));

    private static void Eager(string name, int min, int max, string sig, string desc, string cat,
        Func<object?[], Scope, object?> impl) =>
        Lazy(name, min, max, sig, desc, cat, (args, scope) =>
            impl(args.Select(a => Values.Normalize(Evaluator.Eval(a, scope))).ToArray(), scope));

    private static List<decimal> Numbers(object?[] args) =>
        Values.Flatten(args).Where(v => !Values.IsBlank(v)).Select(Values.ToNumber).ToList();

    private static object? Arg(object?[] args, int i) => i < args.Length ? args[i] : null;

    private static int ToInt(object? v, string what)
    {
        var d = Values.ToNumber(v);
        if (d != Math.Floor(d) || d < -1_000_000 || d > 1_000_000)
            throw new ExpressionException($"{what} must be a whole number");
        return (int)d;
    }

    private static decimal RoundTo(decimal value, int digits, MidpointRounding mode)
    {
        if (digits is < -10 or > 20) throw new ExpressionException("Decimal places must be between -10 and 20");
        if (digits >= 0) return Math.Round(value, digits, mode);
        var factor = (decimal)Math.Pow(10, -digits);
        return Math.Round(value / factor, 0, mode) * factor;
    }

    private static DateTime AddUnits(DateTime date, int amount, string unit, Scope scope) => NormalizeUnit(unit) switch
    {
        "day" => date.AddDays(amount),
        "week" => date.AddDays(7.0 * amount),
        "month" => date.AddMonths(amount),
        "year" => date.AddYears(amount),
        "hour" => date.AddHours(amount),
        "minute" => date.AddMinutes(amount),
        "schoolday" => AddWeekdays(date, amount, scope),
        _ => throw new ExpressionException($"Unknown unit '{unit}' — use days, weekdays, weeks, months, years, hours or minutes"),
    };

    private static string NormalizeUnit(string unit)
    {
        var u = unit.Trim().ToLowerInvariant();
        return u switch
        {
            "d" or "day" or "days" => "day",
            "w" or "week" or "weeks" => "week",
            "m" or "month" or "months" => "month",
            "y" or "year" or "years" => "year",
            "h" or "hour" or "hours" => "hour",
            "min" or "minute" or "minutes" => "minute",
            "weekday" or "weekdays" or "workday" or "workdays" or "schoolday" or "schooldays" => "schoolday",
            _ => u,
        };
    }

    private static DateTime AddWeekdays(DateTime date, int amount, Scope scope)
    {
        if (Math.Abs(amount) > 20000) throw new ExpressionException("Too many weekdays to add");
        var step = amount < 0 ? -1 : 1;
        var remaining = Math.Abs(amount);
        while (remaining > 0)
        {
            date = date.AddDays(step);
            if (scope.IsSchoolDay(date)) remaining--;
        }
        return date;
    }

    /// <summary>Whole Monday–Friday days from start to end, inclusive of both ends. Negative if end is before start.</summary>
    private static decimal Weekdays(DateTime start, DateTime end, Scope scope)
    {
        start = start.Date; end = end.Date;
        if (end < start) return -Weekdays(end, start, scope);
        // Days the school calendar marks as closed do not count (weekend entries in it are ignored).
        var closed = scope.ClosedDays?.Count(d => d >= start && d <= end && d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) ?? 0;
        return WeekdaysIgnoringCalendar(start, end) - closed;
    }

    private static decimal WeekdaysIgnoringCalendar(DateTime start, DateTime end)
    {
        var totalDays = (end - start).Days + 1;
        var fullWeeks = totalDays / 7;
        var count = fullWeeks * 5;
        var cursor = start.AddDays(fullWeeks * 7);
        while (cursor <= end)
        {
            if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) count++;
            cursor = cursor.AddDays(1);
        }
        return count;
    }

    /// <summary>Weekdays that pass going from start to end (start itself is not counted).</summary>
    private static decimal WeekdaysElapsed(DateTime start, DateTime end, Scope scope)
    {
        if (start == end) return 0;
        return end > start ? Weekdays(start.AddDays(1), end, scope) : -Weekdays(end.AddDays(1), start, scope);
    }

    private static int MonthsBetween(DateTime a, DateTime b)
    {
        var months = (b.Year - a.Year) * 12 + b.Month - a.Month;
        if (months > 0 && a.AddMonths(months) > b) months--;
        else if (months < 0 && a.AddMonths(months) < b) months++;
        return months;
    }

    static Functions()
    {
        const string Logic = "Logic", Math_ = "Math", Text = "Text", Date = "Date", Lists = "Lists";

        // ---- Logic -------------------------------------------------------------------
        Lazy("IF", 2, 3, "IF(condition, then, else)", "Returns one value when the condition is true and another when it is false.", Logic,
            (a, s) => Values.ToBool(Evaluator.Eval(a[0], s)) ? Evaluator.Eval(a[1], s) : a.Count > 2 ? Evaluator.Eval(a[2], s) : null);
        Lazy("IFS", 2, int.MaxValue, "IFS(cond1, value1, cond2, value2, …, fallback)", "Returns the value for the first condition that is true. A final unpaired value is the fallback.", Logic,
            (a, s) =>
            {
                var i = 0;
                for (; i + 1 < a.Count; i += 2)
                    if (Values.ToBool(Evaluator.Eval(a[i], s))) return Evaluator.Eval(a[i + 1], s);
                return i < a.Count ? Evaluator.Eval(a[i], s) : null;
            });
        Lazy("SWITCH", 3, int.MaxValue, "SWITCH(value, match1, result1, …, fallback)", "Compares a value against each match and returns the paired result.", Logic,
            (a, s) =>
            {
                var subject = Evaluator.Eval(a[0], s);
                var i = 1;
                for (; i + 1 < a.Count; i += 2)
                    if (Values.AreEqual(subject, Evaluator.Eval(a[i], s))) return Evaluator.Eval(a[i + 1], s);
                return i < a.Count ? Evaluator.Eval(a[i], s) : null;
            });
        Lazy("AND", 1, int.MaxValue, "AND(a, b, …)", "True when every value is true.", Logic,
            (a, s) => a.All(n => Values.ToBool(Evaluator.Eval(n, s))));
        Lazy("OR", 1, int.MaxValue, "OR(a, b, …)", "True when at least one value is true.", Logic,
            (a, s) => a.Any(n => Values.ToBool(Evaluator.Eval(n, s))));
        Eager("NOT", 1, 1, "NOT(value)", "Flips true to false and false to true.", Logic, (a, _) => !Values.ToBool(a[0]));
        Eager("ISBLANK", 1, 1, "ISBLANK(value)", "True when the value is empty.", Logic, (a, _) => Values.IsBlank(a[0]));
        Eager("ISNUMBER", 1, 1, "ISNUMBER(value)", "True when the value is a number or numeric text.", Logic,
            (a, _) => !Values.IsBlank(a[0]) && a[0] is not bool && a[0] is not DateTime && Values.TryToNumber(a[0], out var _n));
        Lazy("COALESCE", 1, int.MaxValue, "COALESCE(a, b, …)", "Returns the first value that is not empty.", Logic,
            (a, s) =>
            {
                foreach (var n in a)
                {
                    var v = Evaluator.Eval(n, s);
                    if (!Values.IsBlank(v)) return v;
                }
                return null;
            });
        Eager("IN", 2, int.MaxValue, "IN(value, option1, option2, …)", "True when the value equals any of the options (a list also works).", Logic,
            (a, _) => Values.Flatten(a.Skip(1)).Any(o => Values.AreEqual(a[0], o)));

        // ---- Math --------------------------------------------------------------------
        Eager("SUM", 1, int.MaxValue, "SUM(a, b, …)", "Adds numbers. Give it a table column, e.g. SUM(items.total).", Math_,
            (a, _) => Numbers(a).Sum());
        Eager("AVG", 1, int.MaxValue, "AVG(a, b, …)", "Average of the numbers, ignoring blanks.", Math_,
            (a, _) => { var n = Numbers(a); return n.Count == 0 ? 0m : n.Average(); });
        Eager("MIN", 1, int.MaxValue, "MIN(a, b, …)", "Smallest number.", Math_,
            (a, _) => { var n = Numbers(a); return n.Count == 0 ? null : n.Min(); });
        Eager("MAX", 1, int.MaxValue, "MAX(a, b, …)", "Largest number.", Math_,
            (a, _) => { var n = Numbers(a); return n.Count == 0 ? null : n.Max(); });
        Eager("COUNT", 1, int.MaxValue, "COUNT(list)", "How many non-empty values. COUNT(items) counts table rows.", Math_,
            (a, _) => (decimal)Values.Flatten(a).Count(v => !Values.IsBlank(v)));
        Eager("ROUND", 1, 2, "ROUND(number, places)", "Rounds to the given decimal places (halves round up).", Math_,
            (a, _) => RoundTo(Values.ToNumber(a[0]), a.Length > 1 ? ToInt(a[1], "Places") : 0, MidpointRounding.AwayFromZero));
        Eager("ROUNDUP", 1, 2, "ROUNDUP(number, places)", "Rounds away from zero.", Math_,
            (a, _) =>
            {
                var n = Values.ToNumber(a[0]);
                var digits = a.Length > 1 ? ToInt(a[1], "Places") : 0;
                return RoundTo(n, digits, n >= 0 ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToNegativeInfinity);
            });
        Eager("ROUNDDOWN", 1, 2, "ROUNDDOWN(number, places)", "Rounds toward zero.", Math_,
            (a, _) => RoundTo(Values.ToNumber(a[0]), a.Length > 1 ? ToInt(a[1], "Places") : 0, MidpointRounding.ToZero));
        Eager("CEILING", 1, 1, "CEILING(number)", "Rounds up to the next whole number.", Math_, (a, _) => Math.Ceiling(Values.ToNumber(a[0])));
        Eager("FLOOR", 1, 1, "FLOOR(number)", "Rounds down to the previous whole number.", Math_, (a, _) => Math.Floor(Values.ToNumber(a[0])));
        Eager("ABS", 1, 1, "ABS(number)", "Removes the sign from a number.", Math_, (a, _) => Math.Abs(Values.ToNumber(a[0])));
        Eager("MOD", 2, 2, "MOD(number, divisor)", "Remainder after division.", Math_,
            (a, _) =>
            {
                var d = Values.ToNumber(a[1]);
                if (d == 0) throw new ExpressionException("Cannot divide by zero");
                return Values.ToNumber(a[0]) % d;
            });
        Eager("POWER", 2, 2, "POWER(number, exponent)", "Raises a number to a power.", Math_,
            (a, _) => Evaluator.Power(Values.ToNumber(a[0]), Values.ToNumber(a[1])));
        Eager("SQRT", 1, 1, "SQRT(number)", "Square root.", Math_,
            (a, _) =>
            {
                var n = Values.ToNumber(a[0]);
                if (n < 0) throw new ExpressionException("Cannot take the square root of a negative number");
                return (decimal)Math.Sqrt((double)n);
            });
        Eager("NUMBER", 1, 1, "NUMBER(value)", "Converts text such as \"12.50\" to a number.", Math_, (a, _) => Values.ToNumber(a[0]));
        Eager("PERCENT", 2, 3, "PERCENT(part, whole, places)", "Part as a percentage of whole (0 when whole is 0).", Math_,
            (a, _) =>
            {
                var whole = Values.ToNumber(a[1]);
                if (whole == 0) return 0m;
                return RoundTo(Values.ToNumber(a[0]) / whole * 100, a.Length > 2 ? ToInt(a[2], "Places") : 1, MidpointRounding.AwayFromZero);
            });

        // ---- Text --------------------------------------------------------------------
        Eager("CONCAT", 1, int.MaxValue, "CONCAT(a, b, …)", "Joins values into one piece of text.", Text,
            (a, _) => string.Concat(Values.Flatten(a).Select(Values.ToText)));
        Eager("JOIN", 2, 2, "JOIN(list, separator)", "Joins a list with a separator, skipping blanks.", Text,
            (a, _) => string.Join(Values.ToText(a[1]),
                Values.Flatten(new[] { a[0] }).Where(v => !Values.IsBlank(v)).Select(Values.ToText)));
        Eager("LEN", 1, 1, "LEN(text)", "Number of characters (or items in a list).", Text,
            (a, _) => a[0] is List<object?> l ? l.Count : (decimal)Values.ToText(a[0]).Length);
        Eager("UPPER", 1, 1, "UPPER(text)", "UPPER CASE.", Text, (a, _) => Values.ToText(a[0]).ToUpperInvariant());
        Eager("LOWER", 1, 1, "LOWER(text)", "lower case.", Text, (a, _) => Values.ToText(a[0]).ToLowerInvariant());
        Eager("TRIM", 1, 1, "TRIM(text)", "Removes spaces from both ends.", Text, (a, _) => Values.ToText(a[0]).Trim());
        Eager("LEFT", 2, 2, "LEFT(text, count)", "First characters of the text.", Text,
            (a, _) => { var t = Values.ToText(a[0]); return t[..Math.Clamp(ToInt(a[1], "Count"), 0, t.Length)]; });
        Eager("RIGHT", 2, 2, "RIGHT(text, count)", "Last characters of the text.", Text,
            (a, _) => { var t = Values.ToText(a[0]); return t[(t.Length - Math.Clamp(ToInt(a[1], "Count"), 0, t.Length))..]; });
        Eager("MID", 3, 3, "MID(text, start, count)", "Characters from the middle; start is 1 for the first character.", Text,
            (a, _) =>
            {
                var t = Values.ToText(a[0]);
                var start = Math.Clamp(ToInt(a[1], "Start") - 1, 0, t.Length);
                var count = Math.Clamp(ToInt(a[2], "Count"), 0, t.Length - start);
                return t.Substring(start, count);
            });
        Eager("CONTAINS", 2, 2, "CONTAINS(text or list, value)", "True when the text contains the value, or the list (e.g. a multi-select) includes it.", Text,
            (a, _) => a[0] is List<object?> l
                ? Values.Flatten(l).Any(i => Values.AreEqual(i, a[1]))
                : !Values.IsBlank(a[1]) && Values.ToText(a[0]).Contains(Values.ToText(a[1]), StringComparison.OrdinalIgnoreCase));
        Eager("STARTSWITH", 2, 2, "STARTSWITH(text, prefix)", "True when the text begins with the prefix.", Text,
            (a, _) => Values.ToText(a[0]).StartsWith(Values.ToText(a[1]), StringComparison.OrdinalIgnoreCase));
        Eager("ENDSWITH", 2, 2, "ENDSWITH(text, suffix)", "True when the text ends with the suffix.", Text,
            (a, _) => Values.ToText(a[0]).EndsWith(Values.ToText(a[1]), StringComparison.OrdinalIgnoreCase));
        Eager("REPLACE", 3, 3, "REPLACE(text, find, replacement)", "Replaces every occurrence of find.", Text,
            (a, _) =>
            {
                var find = Values.ToText(a[1]);
                return find.Length == 0 ? Values.ToText(a[0])
                    : Values.ToText(a[0]).Replace(find, Values.ToText(a[2]), StringComparison.OrdinalIgnoreCase);
            });
        Eager("MATCHES", 2, 2, "MATCHES(text, pattern)", "True when the text matches a regular expression, e.g. MATCHES(id, \"^[0-9]{6}$\").", Text,
            (a, _) =>
            {
                try
                {
                    return Regex.IsMatch(Values.ToText(a[0]), Values.ToText(a[1]),
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    throw new ExpressionException("The pattern is not a valid regular expression");
                }
                catch (RegexMatchTimeoutException)
                {
                    throw new ExpressionException("The pattern took too long to check");
                }
            });
        Eager("TEXT", 1, 2, "TEXT(value, format)", "Formats a number or date, e.g. TEXT(total, \"C2\") → $1,234.50 or TEXT(date, \"MMM d, yyyy\").", Text,
            (a, _) =>
            {
                if (a.Length < 2 || Values.IsBlank(a[1])) return Values.ToText(a[0]);
                var format = Values.ToText(a[1]);
                try
                {
                    return a[0] switch
                    {
                        DateTime dt => dt.ToString(format, Values.DisplayCulture),
                        decimal d => d.ToString(format, Values.DisplayCulture),
                        _ when Values.TryToDate(a[0], out var parsed) => parsed.ToString(format, Values.DisplayCulture),
                        _ when !Values.IsBlank(a[0]) && Values.TryToNumber(a[0], out var num) => num.ToString(format, Values.DisplayCulture),
                        _ => Values.ToText(a[0]),
                    };
                }
                catch (FormatException)
                {
                    throw new ExpressionException($"'{format}' is not a valid format");
                }
            });

        // ---- Date --------------------------------------------------------------------
        Eager("TODAY", 0, 0, "TODAY()", "Today's date.", Date, (_, s) => s.Now().Date);
        Eager("NOW", 0, 0, "NOW()", "The current date and time.", Date, (_, s) => s.Now());
        Eager("DATE", 3, 3, "DATE(year, month, day)", "Builds a date.", Date,
            (a, _) =>
            {
                try { return new DateTime(ToInt(a[0], "Year"), ToInt(a[1], "Month"), ToInt(a[2], "Day")); }
                catch (ArgumentOutOfRangeException) { throw new ExpressionException("That is not a real calendar date"); }
            });
        Eager("DATEADD", 2, 3, "DATEADD(date, amount, unit)", "Adds days (default), weekdays (school days, skipping closed days), weeks, months or years to a date. Blank date gives blank.", Date,
            (a, s) =>
            {
                if (Values.IsBlank(a[0])) return null;
                try { return AddUnits(Values.ToDate(a[0]), ToInt(a[1], "Amount"), a.Length > 2 ? Values.ToText(a[2]) : "days", s); }
                catch (ArgumentOutOfRangeException) { throw new ExpressionException("The resulting date is out of range"); }
            });
        Eager("DATEDIFF", 2, 3, "DATEDIFF(start, end, unit)", "Whole days (default), weekdays, weeks, months, years or hours from start to end. Blank when either date is blank.", Date,
            (a, s) =>
            {
                if (Values.IsBlank(a[0]) || Values.IsBlank(a[1])) return null;
                var start = Values.ToDate(a[0]); var end = Values.ToDate(a[1]);
                var unit = a.Length > 2 ? Values.ToText(a[2]) : "days";
                return NormalizeUnit(unit) switch
                {
                    "day" => (decimal)(end.Date - start.Date).Days,
                    "week" => Math.Truncate((decimal)(end.Date - start.Date).Days / 7),
                    "month" => (decimal)MonthsBetween(start, end),
                    "year" => (decimal)(MonthsBetween(start, end) / 12),
                    "hour" => Math.Truncate((decimal)(end - start).TotalHours),
                    "minute" => Math.Truncate((decimal)(end - start).TotalMinutes),
                    "schoolday" => WeekdaysElapsed(start.Date, end.Date, s),
                    _ => throw new ExpressionException($"Unknown unit '{unit}' — use days, weekdays, weeks, months, years, hours or minutes"),
                };
            });
        Eager("WEEKDAYS", 2, 2, "WEEKDAYS(start, end)", "Number of school days (Monday–Friday, minus days the school calendar marks closed) from start to end, counting both.", Date,
            (a, s) => Values.IsBlank(a[0]) || Values.IsBlank(a[1]) ? null : Weekdays(Values.ToDate(a[0]), Values.ToDate(a[1]), s));
        Eager("ISSCHOOLDAY", 1, 1, "ISSCHOOLDAY(date)", "True when the date is a weekday the school is open.", Date,
            (a, s) => !Values.IsBlank(a[0]) && s.IsSchoolDay(Values.ToDate(a[0])));
        Eager("YEAR", 1, 1, "YEAR(date)", "The year of a date.", Date, (a, _) => Values.IsBlank(a[0]) ? null : (decimal)Values.ToDate(a[0]).Year);
        Eager("MONTH", 1, 1, "MONTH(date)", "The month number (1–12).", Date, (a, _) => Values.IsBlank(a[0]) ? null : (decimal)Values.ToDate(a[0]).Month);
        Eager("DAY", 1, 1, "DAY(date)", "The day of the month.", Date, (a, _) => Values.IsBlank(a[0]) ? null : (decimal)Values.ToDate(a[0]).Day);
        Eager("WEEKDAY", 1, 1, "WEEKDAY(date)", "Day name, e.g. \"Monday\".", Date,
            (a, _) => Values.IsBlank(a[0]) ? null : Values.ToDate(a[0]).DayOfWeek.ToString());
        Eager("SCHOOLYEAR", 0, 1, "SCHOOLYEAR(date)", "School year for a date (July–June), e.g. \"2026-2027\". Uses today when no date is given.", Date,
            (a, s) =>
            {
                var d = a.Length == 0 || Values.IsBlank(a[0]) ? s.Now() : Values.ToDate(a[0]);
                var start = d.Month >= 7 ? d.Year : d.Year - 1;
                return $"{start}-{start + 1}";
            });

        // ---- Lists -------------------------------------------------------------------
        Eager("FIRST", 1, 1, "FIRST(list)", "First item of a list.", Lists,
            (a, _) => a[0] is List<object?> l ? l.FirstOrDefault() : a[0]);
        Eager("LAST", 1, 1, "LAST(list)", "Last item of a list.", Lists,
            (a, _) => a[0] is List<object?> l ? l.LastOrDefault() : a[0]);
        Eager("UNIQUE", 1, 1, "UNIQUE(list)", "Removes duplicates and blanks from a list.", Lists,
            (a, _) =>
            {
                var result = new List<object?>();
                foreach (var item in Values.Flatten(new[] { a[0] }))
                    if (!Values.IsBlank(item) && !result.Any(r => Values.AreEqual(r, item))) result.Add(item);
                return result;
            });
        Eager("COUNTIF", 2, 2, "COUNTIF(list, value)", "How many items in the list equal the value, e.g. COUNTIF(items.category, \"Software\").", Lists,
            (a, _) => (decimal)Values.Flatten(new[] { a[0] }).Count(i => Values.AreEqual(i, a[1])));
        Eager("SUMIF", 3, 3, "SUMIF(matchList, value, sumList)", "Adds numbers from sumList where the same row of matchList equals the value.", Lists,
            (a, _) =>
            {
                if (a[0] is not List<object?> keys || a[2] is not List<object?> nums) return 0m;
                decimal total = 0;
                for (var i = 0; i < Math.Min(keys.Count, nums.Count); i++)
                    if (Values.AreEqual(keys[i], a[1]) && !Values.IsBlank(nums[i])) total += Values.ToNumber(nums[i]);
                return total;
            });

        Catalog = Table.Values.Select(d => d.Info).OrderBy(i => i.Category).ThenBy(i => i.Name).ToList();
    }
}
