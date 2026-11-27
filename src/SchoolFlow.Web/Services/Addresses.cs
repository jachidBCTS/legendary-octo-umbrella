using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace SchoolFlow.Web.Services;

/// <summary>One suggested address, as shown under the box and put into it when picked.</summary>
public sealed record AddressSuggestion(string Label);

/// <summary>Where address suggestions come from (queue #15, owner chose OpenStreetMap, Nov 24, 2026).</summary>
public interface IAddressSource
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string query, CancellationToken ct);
}

/// <summary>Address suggestions switched off (Addresses:Provider = "off"): the box is a plain text box.</summary>
public sealed class NoAddressSource : IAddressSource
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AddressSuggestion>>(Array.Empty<AddressSuggestion>());
}

/// <summary>Development and tests: a few made-up local addresses, no network.</summary>
public sealed class SampleAddressSource : IAddressSource
{
    private static readonly string[] All =
    {
        "540 Main Street, Hackensack, NJ 07601",
        "540 Kinderkamack Road, Oradell, NJ 07649",
        "200 Hackensack Avenue, Hackensack, NJ 07601",
        "1 Liberty Science Center Drive, Jersey City, NJ 07305",
        "300 Paramus Road, Paramus, NJ 07652",
        "75 Prospect Avenue, Teaneck, NJ 07666",
    };
    public bool IsConfigured => true;
    public Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string query, CancellationToken ct)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<AddressSuggestion> hits = All
            .Where(a => words.All(w => a.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(a => new AddressSuggestion(a)).ToList();
        return Task.FromResult(hits);
    }
}

/// <summary>
/// OpenStreetMap search through Photon (https://photon.komoot.io): free, no key. Results near Bergen County
/// come first. Only the typed text is sent, never who is asking.
/// </summary>
public sealed class PhotonAddressSource : IAddressSource
{
    // Hackensack, NJ: the middle of the districts' area, so nearby addresses rank first.
    private const string Bias = "&lat=40.886&lon=-74.043&zoom=10";
    private readonly HttpClient _http;
    public PhotonAddressSource(HttpClient http) => _http = http;
    public bool IsConfigured => true;

    public async Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string query, CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/?limit=8&lang=en" + Bias + "&q=" + Uri.EscapeDataString(query), ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Photon answers with GeoJSON; each feature's properties make one line: "540 Main Street, Hackensack, NJ 07601".</summary>
    public static IReadOnlyList<AddressSuggestion> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<AddressSuggestion>();
        if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return list;
        foreach (var f in features.EnumerateArray())
        {
            if (!f.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            string Get(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            if (Get("countrycode") is { Length: > 0 } cc && !cc.Equals("US", StringComparison.OrdinalIgnoreCase)) continue;
            var street = string.Join(" ", new[] { Get("housenumber"), Get("street") }.Where(s => s.Length > 0));
            var name = Get("name");
            var first = street.Length == 0 ? name : name.Length > 0 && !street.Contains(name, StringComparison.OrdinalIgnoreCase) && Get("housenumber").Length == 0 ? name + ", " + street : street;
            var town = new[] { Get("city"), Get("town"), Get("village"), Get("district"), Get("county") }.FirstOrDefault(s => s.Length > 0) ?? "";
            var state = StateCode(Get("state"));
            var tail = string.Join(" ", new[] { state, Get("postcode") }.Where(s => s.Length > 0));
            var label = string.Join(", ", new[] { first, town, tail }.Where(s => s.Length > 0));
            if (label.Length == 0 || list.Any(a => a.Label.Equals(label, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new AddressSuggestion(label));
        }
        return list;
    }

    private static readonly Dictionary<string, string> States = new(StringComparer.OrdinalIgnoreCase)
    {
        ["New Jersey"] = "NJ", ["New York"] = "NY", ["Pennsylvania"] = "PA", ["Connecticut"] = "CT", ["Delaware"] = "DE",
        ["Maryland"] = "MD", ["Massachusetts"] = "MA", ["Rhode Island"] = "RI", ["Vermont"] = "VT", ["New Hampshire"] = "NH",
        ["Maine"] = "ME", ["Virginia"] = "VA", ["District of Columbia"] = "DC", ["Washington, D.C."] = "DC",
    };
    private static string StateCode(string state) => States.TryGetValue(state, out var code) ? code : state;
}

/// <summary>
/// What the API uses: remembers answers for a day and keeps to about one search a second for the whole site
/// (the public service's fair-use limit). A failure gives "not connected", and the box stays a plain text box.
/// </summary>
public sealed class AddressLookup
{
    private readonly IAddressSource _source;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AddressLookup> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    public AddressLookup(IAddressSource source, IMemoryCache cache, ILogger<AddressLookup> log)
    {
        _source = source;
        _cache = cache;
        _log = log;
    }

    public bool IsConfigured => _source.IsConfigured;

    /// <summary>Null when the service could not be reached.</summary>
    public async Task<IReadOnlyList<AddressSuggestion>?> SearchAsync(string query, CancellationToken ct)
    {
        var key = "addr:" + string.Join(' ', query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (_cache.TryGetValue(key, out IReadOnlyList<AddressSuggestion>? hit)) return hit;
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(3), ct)) return Array.Empty<AddressSuggestion>();
        try
        {
            var wait = _last.AddSeconds(1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;
            var found = await _source.SearchAsync(query, ct);
            _cache.Set(key, found, TimeSpan.FromDays(1));
            return found;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("Address search failed: {Message}", e.Message);
            return null;
        }
        finally { _gate.Release(); }
    }
}
