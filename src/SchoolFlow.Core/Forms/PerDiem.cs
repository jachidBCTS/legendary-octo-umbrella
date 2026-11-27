using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Forms;

/// <summary>
/// GSA's rates for one place in one federal fiscal year (October to September). <paramref name="RatesYear"/> is set when
/// the travel's year is not published yet and an earlier year's rates stand in for it.
/// </summary>
public sealed record PerDiemArea(string City, string County, string State, decimal Meals, IReadOnlyDictionary<int, decimal> LodgingByMonth, bool StandardRate,
    int? RatesYear = null);

/// <summary>
/// What the server looked up for the per diem fields of a form: GSA's rates by (ZIP code, fiscal year),
/// with null for a lookup that failed, and the standard rate the district keeps for when GSA cannot answer.
/// Built by the web app before the form is evaluated, so evaluating stays free of network calls.
/// </summary>
public sealed class PerDiemDirectory
{
    public Dictionary<(string Zip, int FiscalYear), PerDiemArea?> Areas { get; } = new();
    public decimal StandardMeals { get; init; }
    public decimal StandardLodging { get; init; }
    /// <summary>"gsa", or "simulated" when example rates stand in for GSA (development only).</summary>
    public string Source { get; init; } = "gsa";
    /// <summary>Why GSA was not asked at all (e.g. no API key), shown when the standard rate is used.</summary>
    public string? Problem { get; init; }

    /// <summary>The federal fiscal year a date falls in: October 1 starts the next year's.</summary>
    public static int FiscalYear(DateTime date) => date.Month >= 10 ? date.Year + 1 : date.Year;

    /// <summary>The ZIP code in what was typed ("08540", "08540-1234", " 08540 "), or null.</summary>
    public static string? CleanZip(object? raw)
    {
        var text = Values.ToText(raw).Trim();
        if (text.Length >= 5 && text[..5].All(char.IsAsciiDigit) && (text.Length == 5 || (text.Length == 10 && text[5] == '-' && text[6..].All(char.IsAsciiDigit))))
            return text[..5];
        return null;
    }

    /// <summary>The nights of a stay: from the first day up to the night before the last day (at least one).</summary>
    public static IEnumerable<DateTime> Nights(DateTime from, DateTime? to)
    {
        var last = to is DateTime t && t.Date > from.Date ? t.Date.AddDays(-1) : from.Date;
        for (var night = from.Date; night <= last && night < from.Date.AddDays(400); night = night.AddDays(1)) yield return night;
    }

    /// <summary>
    /// The (ZIP, fiscal year) pairs a stay needs, so the web app can look them up before evaluating.
    /// </summary>
    public static IEnumerable<(string Zip, int FiscalYear)> Needed(string zip, DateTime from, DateTime? to) =>
        Nights(from, to).Select(FiscalYear).Append(FiscalYear(from)).Distinct().Select(fy => (zip, fy));

    /// <summary>
    /// The answer stored for a per diem field: where it is, the meals rate (and 75% for the first and last
    /// day of travel), the highest nightly hotel limit over the months of the stay, and where it came from.
    /// </summary>
    public Dictionary<string, object?> Quote(string zip, DateTime from, DateTime? to)
    {
        var record = Values.NewDict();
        record["zip"] = zip;
        record["fiscal_year"] = (decimal)FiscalYear(from);
        Areas.TryGetValue((zip, FiscalYear(from)), out var area);
        if (area is null)
        {
            record["meals"] = StandardMeals;
            record["first_last_day"] = Math.Round(StandardMeals * 0.75m, 2);
            record["lodging"] = StandardLodging;
            record["city"] = ""; record["county"] = ""; record["state"] = "";
            record["standard"] = true;
            record["source"] = "fallback";
            record["note"] = (Problem ?? "GSA's rates could not be looked up") +
                ", so the standard rate was used. The business office will check it.";
            return record;
        }
        var lodging = 0m;
        foreach (var night in Nights(from, to))
        {
            var yearArea = Areas.TryGetValue((zip, FiscalYear(night)), out var other) && other is not null ? other : area;
            if (yearArea.LodgingByMonth.TryGetValue(night.Month, out var limit)) lodging = Math.Max(lodging, limit);
        }
        record["meals"] = area.Meals;
        record["first_last_day"] = Math.Round(area.Meals * 0.75m, 2);
        record["lodging"] = lodging;
        record["city"] = area.City;
        record["county"] = area.County;
        record["state"] = area.State;
        record["standard"] = area.StandardRate;
        record["source"] = Source;
        record["note"] = Source == "simulated" ? "Example rates: GSA is not connected on this test site." : "";
        if (area.RatesYear is int ratesYear && ratesYear != FiscalYear(from))
        {
            // GSA publishes a fiscal year's rates in August, shortly before it starts on October 1.
            record["rates_year"] = (decimal)ratesYear;
            record["note"] = $"GSA has not published its rates for fiscal year {FiscalYear(from)} (October {FiscalYear(from) - 1} to September {FiscalYear(from)}) yet; "
                + $"the fiscal year {ratesYear} rates are shown for reference. A request that needs these rates (meals or a hotel) can be submitted once GSA publishes them, usually in August."
                + (Source == "simulated" ? " (Example rates: GSA is not connected on this test site.)" : "");
        }
        return record;
    }
}
