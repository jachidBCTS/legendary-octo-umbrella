using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>
/// The school's closed days (holidays, breaks), cached briefly. "School days" are Monday to Friday
/// minus these; they are what date formulas, reminders and escalation count.
/// </summary>
public sealed class SchoolCalendar
{
    private const string CacheKey = "closed-days";
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    private readonly IConfiguration? _config;

    public SchoolCalendar(AppDbContext db, IMemoryCache cache, IConfiguration? config = null)
    {
        _db = db;
        _cache = cache;
        _config = config;
    }

    /// <summary>
    /// Which district each active location is in, and the names districts go by: each code, and the full
    /// name the settings file matches it to (<c>Students:Match:Districts</c>, e.g. BCTS → "Bergen County
    /// Technical Schools"), so a form's district question can use either.
    /// </summary>
    public async Task<PlaceDistricts> PlacesAsync(CancellationToken ct = default)
    {
        var districts = (await _cache.GetOrCreateAsync("location-districts", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return (await _db.Locations.AsNoTracking().Where(l => l.IsActive && l.District != null && l.District != "")
                .Select(l => new { l.Id, l.District }).ToListAsync(ct)).ToDictionary(l => (long)l.Id, l => l.District!);
        }))!;
        return new PlaceDistricts(districts, DistrictAliases(_config));
    }

    /// <summary>Full name → code, and code → code, from the settings file's district matches.</summary>
    public static Dictionary<string, string> DistrictAliases(IConfiguration? config)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in config?.GetSection("Students:Match:Districts").GetChildren() ?? Enumerable.Empty<IConfigurationSection>())
        {
            var code = match.Key.Trim();
            if (code.Length == 0) continue;
            aliases[code] = code;
            if (!string.IsNullOrWhiteSpace(match.Value)) aliases[match.Value.Trim()] = code;
        }
        return aliases;
    }

    public async Task<IReadOnlySet<DateTime>> ClosedDaysAsync(CancellationToken ct = default) =>
        (await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            return (await _db.ClosedDays.AsNoTracking().Select(d => d.Date).ToListAsync(ct)).Select(d => d.Date).ToHashSet();
        }))!;

    public void Invalidate() => _cache.Remove(CacheKey);

    /// <summary>
    /// The admin-kept rates as a record for formulas (<c>rates.mileage</c>). It lives here because it is
    /// the same kind of thing as the closed days: school-wide reference data that forms calculate with.
    /// </summary>
    public async Task<Dictionary<string, object?>> RatesAsync(CancellationToken ct = default)
    {
        var rows = (await _cache.GetOrCreateAsync("rates", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            return await _db.Rates.AsNoTracking().Select(r => new KeyValuePair<string, decimal>(r.Key, r.Value)).ToListAsync(ct);
        }))!;
        var record = SchoolFlow.Core.Expressions.Values.NewDict();
        foreach (var (key, value) in rows) record[key] = value;
        return record;
    }

    public void InvalidateRates() => _cache.Remove("rates");

    /// <summary>
    /// Active departments and locations (id → name), the lists Department and Location fields accept. Asked
    /// for on every check of a form while someone types, so they are kept for 30 seconds; the admin pages
    /// that change them call <see cref="InvalidateLists"/>.
    /// </summary>
    public async Task<Dictionary<long, string>> ActiveDepartmentsAsync(CancellationToken ct = default) =>
        (await _cache.GetOrCreateAsync("departments", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return (await _db.Departments.AsNoTracking().Where(d => d.IsActive).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
                .ToDictionary(d => (long)d.Id, d => d.Name);
        }))!;

    public async Task<Dictionary<long, string>> ActiveLocationsAsync(CancellationToken ct = default) =>
        (await _cache.GetOrCreateAsync("locations", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return (await _db.Locations.AsNoTracking().Where(l => l.IsActive).Select(l => new { l.Id, l.Name }).ToListAsync(ct))
                .ToDictionary(l => (long)l.Id, l => l.Name);
        }))!;

    public void InvalidateLists()
    {
        _cache.Remove("departments");
        _cache.Remove("locations");
        _cache.Remove("location-districts");
    }

    /// <summary>School days that have passed going from one moment to another (the starting day is not counted).</summary>
    public async Task<int> SchoolDaysBetweenAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
        SchoolDaysBetween(from, to, await ClosedDaysAsync(ct));

    public static int SchoolDaysBetween(DateTime from, DateTime to, IReadOnlySet<DateTime> closed)
    {
        var count = 0;
        // Bounded: nothing here waits anywhere near ten years.
        for (var day = from.Date.AddDays(1); day <= to.Date && count < 3650; day = day.AddDays(1))
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !closed.Contains(day)) count++;
        return count;
    }
}
