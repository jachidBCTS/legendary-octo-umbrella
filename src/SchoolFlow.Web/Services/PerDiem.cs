using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Forms;

namespace SchoolFlow.Web.Services;

/// <summary>Where GSA per diem rates come from: GSA itself, or example rates on a development site.</summary>
public interface IPerDiemSource
{
    /// <summary>"gsa" or "simulated".</summary>
    string Source { get; }
    /// <summary>The rates for a ZIP code in a fiscal year; null when GSA has none for it. Throws when GSA cannot be reached.</summary>
    Task<PerDiemArea?> GetAsync(string zip, int fiscalYear, CancellationToken ct);
}

/// <summary>
/// GSA's per diem API (https://api.gsa.gov/travel/perdiem, key in the X-API-KEY header). Only the
/// "rates by ZIP code and year" call is used: every US ZIP code maps to exactly one rate area.
/// </summary>
public sealed class GsaPerDiemClient : IPerDiemSource
{
    private readonly HttpClient _http;
    public GsaPerDiemClient(HttpClient http) => _http = http;
    public string Source => "gsa";

    public async Task<PerDiemArea?> GetAsync(string zip, int fiscalYear, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"v2/rates/zip/{Uri.EscapeDataString(zip)}/year/{fiscalYear}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new HttpRequestException("GSA refused the API key (check Gsa:ApiKey in the settings file).");
        // A year GSA has not published yet (or a ZIP it does not know) is "no rates", not an outage.
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return Parse(doc.RootElement);
    }

    /// <summary>
    /// Reads GSA's reply: { "rates": [ { "state": "NJ", "rate": [ { "city", "county", "meals",
    /// "standardRate", "months": { "month": [ { "number": 1, "value": 157 }, … ] } } ] } ] }.
    /// </summary>
    public static PerDiemArea? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in rates.EnumerateArray())
        {
            if (!entry.TryGetProperty("rate", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var rate in list.EnumerateArray())
            {
                var meals = Number(rate, "meals");
                if (meals is not decimal m || m <= 0) continue;
                var months = new Dictionary<int, decimal>();
                if (rate.TryGetProperty("months", out var mo) && mo.TryGetProperty("month", out var monthList) && monthList.ValueKind == JsonValueKind.Array)
                    foreach (var month in monthList.EnumerateArray())
                        if (Number(month, "number") is decimal n && Number(month, "value") is decimal v && n is >= 1 and <= 12)
                            months[(int)n] = v;
                var standard = rate.TryGetProperty("standardRate", out var s)
                    && (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.String && string.Equals(s.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
                return new PerDiemArea(Text(rate, "city"), Text(rate, "county"), Text(entry, "state"), m, months, standard);
            }
        }
        return null;
    }

    private static decimal? Number(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var t)) return t;
        return null;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
}

/// <summary>Example rates for a development site without a GSA key. Never used on a real site.</summary>
public sealed class SimulatedPerDiemSource : IPerDiemSource
{
    public string Source => "simulated";

    public Task<PerDiemArea?> GetAsync(string zip, int fiscalYear, CancellationToken ct)
    {
        if (zip == "00000") return Task.FromResult<PerDiemArea?>(null);                      // "no rate"
        if (zip == "99999") throw new HttpRequestException("Simulated outage.");             // GSA unreachable
        // Like GSA: next fiscal year's rates appear in August; later years are not published.
        var today = DateTime.Today;
        var published = PerDiemDirectory.FiscalYear(today) + (today.Month is >= 8 and <= 9 ? 1 : 0);
        if (fiscalYear > published) return Task.FromResult<PerDiemArea?>(null);
        // A few places, so tests and demos show different amounts; anything else is the standard rate.
        var (city, county, state, meals, lodging, standard) = zip switch
        {
            "08540" => ("Princeton", "Mercer", "NJ", 74m, 165m, false),
            "08401" => ("Atlantic City", "Atlantic", "NJ", 74m, 132m, false),
            "10001" => ("New York City", "Bronx / Kings / New York / Queens / Richmond", "NY", 92m, 286m, false),
            "02108" => ("Boston", "Suffolk", "MA", 92m, 305m, false),
            _ => ("Standard area", "", "", 68m, 110m, true),
        };
        // Busier months cost more in the big cities.
        var months = Enumerable.Range(1, 12).ToDictionary(m => m, m => standard ? lodging : m is >= 4 and <= 6 or >= 9 and <= 11 ? lodging + 25m : lodging);
        return Task.FromResult<PerDiemArea?>(new PerDiemArea(city, county, state, meals, months, standard));
    }
}

/// <summary>
/// Looks up GSA per diem rates for the ZIP codes and dates on a form before it is evaluated. Answers
/// are cached for a day (GSA sets them once a year); a failed lookup is retried after two minutes.
/// </summary>
public sealed class PerDiemService
{
    public const string StandardMealsKey = "gsa_standard_meals", StandardLodgingKey = "gsa_standard_lodging";
    private readonly IPerDiemSource? _source;
    private readonly IMemoryCache _cache;
    private readonly SchoolCalendar _calendar;
    private readonly ILogger<PerDiemService> _log;

    public PerDiemService(IMemoryCache cache, SchoolCalendar calendar, ILogger<PerDiemService> log, IPerDiemSource? source = null)
    {
        _source = source;
        _cache = cache;
        _calendar = calendar;
        _log = log;
    }

    private sealed record Cached(PerDiemArea? Area, bool Failed);

    /// <summary>The rates a form's per diem fields need, or null when the form has none.</summary>
    public async Task<PerDiemDirectory?> DirectoryForAsync(FormDefinition form, IDictionary<string, object?> input, CancellationToken ct)
    {
        var fields = form.Fields.Where(f => f.Type == FieldTypes.PerDiem && f.PerDiem is not null).ToList();
        if (fields.Count == 0) return null;
        var rates = await _calendar.RatesAsync(ct);
        decimal Rate(string key) => rates.TryGetValue(key, out var v) && Values.TryToNumber(v, out var d) ? d : 0m;
        var directory = new PerDiemDirectory
        {
            StandardMeals = Rate(StandardMealsKey),
            StandardLodging = Rate(StandardLodgingKey),
            Source = _source?.Source ?? "gsa",
            Problem = _source is null ? "GSA is not set up on this site (no API key)" : null,
        };
        if (_source is null) return directory;

        object? Raw(string? key) => !string.IsNullOrWhiteSpace(key) && input.TryGetValue(key.Trim(), out var v) ? v : null;
        foreach (var field in fields)
        {
            var zip = PerDiemDirectory.CleanZip(Raw(field.PerDiem!.Zip));
            if (zip is null || !Values.TryToDate(Raw(field.PerDiem.From), out var from)) continue;
            DateTime? to = Values.TryToDate(Raw(field.PerDiem.To), out var end) ? end : null;
            foreach (var key in PerDiemDirectory.Needed(zip, from, to).Take(3))
            {
                if (directory.Areas.ContainsKey(key)) continue;
                var (area, failed) = await LookupAsync(key.Zip, key.FiscalYear, ct);
                // Travel in a fiscal year GSA has not published yet (its rates come out each August): use the
                // latest year it has, back to the current one, and say so on the answer. Not after an outage.
                for (var year = key.FiscalYear - 1; area is null && !failed && year >= PerDiemDirectory.FiscalYear(DateTime.Today); year--)
                {
                    (var earlier, failed) = await LookupAsync(key.Zip, year, ct);
                    if (earlier is not null) area = earlier with { RatesYear = year };
                }
                directory.Areas[key] = area;
            }
        }
        return directory;
    }

    private async Task<(PerDiemArea? Area, bool Failed)> LookupAsync(string zip, int fiscalYear, CancellationToken ct)
    {
        var cacheKey = $"gsa:{zip}:{fiscalYear}";
        if (_cache.TryGetValue(cacheKey, out Cached? hit) && hit is not null) return (hit.Area, hit.Failed);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var area = await _source!.GetAsync(zip, fiscalYear, timeout.Token);
            // "Not published yet" is asked again after a few hours, so a new year's rates show up soon after GSA adds them.
            _cache.Set(cacheKey, new Cached(area, false), area is null ? TimeSpan.FromHours(4) : TimeSpan.FromHours(24));
            return (area, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "GSA per diem lookup failed for ZIP {Zip}, fiscal year {Year}", zip, fiscalYear);
            _cache.Set(cacheKey, new Cached(null, true), TimeSpan.FromMinutes(2));
            return (null, true);
        }
    }

    /// <summary>The timeline warning for a request whose per diem rates need checking, or null.</summary>
    public static string? WarningFor(FormDefinition form, IDictionary<string, object?> values)
    {
        if (UsedFallback(form, values))
            return "GSA per diem rates could not be looked up, so the standard rate was used. Please check the meal and hotel amounts.";
        var early = form.Fields.Where(f => f.Type == FieldTypes.PerDiem).Select(f => values.TryGetValue(f.Key, out var v) ? v as Dictionary<string, object?> : null)
            .FirstOrDefault(r => r is not null && !Values.IsBlank(r.GetValueOrDefault("rates_year")));
        return early is null ? null
            : $"GSA has not published per diem rates for fiscal year {Values.ToText(early.GetValueOrDefault("fiscal_year"))} yet, so fiscal year {Values.ToText(early.GetValueOrDefault("rates_year"))} rates were used. Please check the meal and hotel amounts.";
    }

    /// <summary>True when an answer used the standard rate because GSA could not give one.</summary>
    public static bool UsedFallback(FormDefinition form, IDictionary<string, object?> values) =>
        form.Fields.Any(f => f.Type == FieldTypes.PerDiem && values.TryGetValue(f.Key, out var v)
            && v is Dictionary<string, object?> record && Values.ToText(record.GetValueOrDefault("source")) == "fallback");
}
