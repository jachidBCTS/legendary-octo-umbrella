using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Freshservice;

/// <summary>
/// Freshservice REST API v2 client. Expects an HttpClient whose BaseAddress is
/// https://{domain}.freshservice.com/ and authenticates with an API key (Basic auth, key as user name).
/// </summary>
public sealed class FreshserviceClient : IFreshserviceClient
{
    private const int MaxAttempts = 3;
    private const int MaxPages = 10;
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;

    public FreshserviceClient(HttpClient http, string apiKey)
    {
        _http = http;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(apiKey + ":X")));
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public bool IsLive => true;

    public static Uri BaseAddressFor(string domain)
    {
        domain = domain.Trim().TrimEnd('/');
        if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new FreshserviceException("The Freshservice domain must use https.");
        if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) domain = domain[8..];
        if (!domain.Contains('.')) domain += ".freshservice.com";
        if (!Uri.TryCreate("https://" + domain + "/", UriKind.Absolute, out var uri) || uri.AbsolutePath != "/")
            throw new FreshserviceException($"'{domain}' is not a valid Freshservice domain.");
        return uri;
    }

    public async Task<Dictionary<string, object?>> CreateTicketAsync(TicketCreate ticket, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["subject"] = ticket.Subject,
            ["description"] = ticket.DescriptionHtml,
            ["email"] = ticket.RequesterEmail,
            ["priority"] = ticket.Priority,
            ["status"] = ticket.Status,
            ["source"] = 2, // Portal
            ["type"] = ticket.Type,
            ["group_id"] = ticket.GroupId,
            ["department_id"] = ticket.DepartmentId,
            ["category"] = ticket.Category,
            ["sub_category"] = ticket.SubCategory,
            ["tags"] = ticket.Tags.Count > 0 ? ticket.Tags : null,
            ["custom_fields"] = ticket.CustomFields.Count > 0 ? ticket.CustomFields : null,
        };
        using var doc = await SendAsync(HttpMethod.Post, "api/v2/tickets", body, ct);
        return Unwrap(doc, "ticket");
    }

    public async Task<Dictionary<string, object?>> UpdateTicketAsync(long ticketId, TicketUpdate update, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["priority"] = update.Priority,
            ["status"] = update.Status,
            ["group_id"] = update.GroupId,
            ["responder_id"] = update.ResponderId,
            ["custom_fields"] = update.CustomFields.Count > 0 ? update.CustomFields : null,
        };
        using var doc = await SendAsync(HttpMethod.Put, $"api/v2/tickets/{ticketId}", body, ct);
        return Unwrap(doc, "ticket");
    }

    public async Task AddNoteAsync(long ticketId, string bodyHtml, bool isPrivate, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["body"] = bodyHtml, ["private"] = isPrivate };
        using var _ = await SendAsync(HttpMethod.Post, $"api/v2/tickets/{ticketId}/notes", body, ct);
    }

    /// <summary>
    /// Freshservice takes files as a multipart form on the ticket update call
    /// (PUT /api/v2/tickets/{id} with one "attachments[]" part per file).
    /// </summary>
    public async Task AttachFilesAsync(long ticketId, IReadOnlyList<TicketFile> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return;
        using var form = new MultipartFormDataContent();
        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Content);
            part.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
            // Quotes and line breaks in a file name would break the multipart header.
            var safeName = new string(file.Name.Where(c => c != '"' && c != '\\' && !char.IsControl(c)).ToArray());
            form.Add(part, "attachments[]", safeName.Length == 0 ? "file" : safeName);
        }
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v2/tickets/{ticketId}") { Content = form };
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        throw new FreshserviceException(DescribeError(response.StatusCode, text), (int)response.StatusCode);
    }

    public async Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"api/v2/tickets/{ticketId}", null, ct, notFoundIsNull: true);
        return doc is null ? null : Unwrap(doc, "ticket");
    }

    public async Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default)
    {
        // Staff are often agents rather than requesters in Freshservice, so check both.
        foreach (var (path, property) in new[] { ("api/v2/requesters", "requesters"), ("api/v2/agents", "agents") })
        {
            using var doc = await SendAsync(HttpMethod.Get, $"{path}?email={Uri.EscapeDataString(email)}", null, ct);
            if (doc!.RootElement.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            {
                var person = Json.ToValues(list[0]);
                person["name"] = PersonName(list[0]);
                person["email"] = person.GetValueOrDefault("primary_email") ?? person.GetValueOrDefault("email") ?? email;
                person["is_agent"] = property == "agents";
                return person;
            }
        }
        return null;
    }

    public async Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity,
        Dictionary<string, object?> customFields, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["quantity"] = quantity,
            ["email"] = requesterEmail,
            ["custom_fields"] = customFields.Count > 0 ? customFields : null,
        };
        using var doc = await SendAsync(HttpMethod.Post, $"api/v2/service_catalog/items/{itemDisplayId}/place_request", body, ct);
        return Unwrap(doc, "service_request");
    }

    public async Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default)
    {
        switch (source)
        {
            case LookupSources.Departments:
                return Filter(await ListAllAsync("api/v2/departments", "departments", e => Item(e, "name"), ct), query);
            case LookupSources.Groups:
                return Filter(await GroupsAsync(ct), query);
            case LookupSources.Locations:
            {
                // Locations nest (campus › building › room); show the whole path so "Room 101" is unambiguous.
                var raw = await ListAllAsync("api/v2/locations", "locations",
                    e => new LookupItem(Id(e), Str(e, "name") ?? "(unnamed)", null, Str(e, "parent_location_id")), ct);
                return Filter(WithFullPaths(raw), query);
            }
            case LookupSources.Agents:
                return Filter(await ListAllAsync("api/v2/agents?active=true", "agents",
                    e => new LookupItem(Id(e), PersonName(e), Str(e, "email")), ct), query);

            case LookupSources.Requesters:
            {
                var q = (query ?? "").Trim();
                if (q.Length < 2) return Array.Empty<LookupItem>();
                // Freshservice filter syntax: ~[first_name|last_name|primary_email]:'text' is a prefix search.
                var safe = q.Replace("'", "").Replace("\"", "");
                var filter = Uri.EscapeDataString($"\"~[first_name|last_name|primary_email]:'{safe}'\"");
                using var doc = await SendAsync(HttpMethod.Get, $"api/v2/requesters?query={filter}&per_page=20", null, ct);
                return Items(doc!, "requesters", e => new LookupItem(Id(e), PersonName(e), Str(e, "primary_email")));
            }

            case LookupSources.MyAssets:
            {
                if (string.IsNullOrWhiteSpace(userEmail)) return Array.Empty<LookupItem>();
                var person = await FindRequesterAsync(userEmail, ct);
                if (person is null || !Values.TryToNumber(person.GetValueOrDefault("id"), out var userId)) return Array.Empty<LookupItem>();
                var filter = Uri.EscapeDataString($"\"user_id:{(long)userId}\"");
                using var doc = await SendAsync(HttpMethod.Get, $"api/v2/assets?filter={filter}&per_page=100", null, ct);
                return Filter(Items(doc!, "assets", e => new LookupItem(
                    Str(e, "display_id") ?? Id(e), Str(e, "name") ?? "(unnamed asset)", null, Str(e, "asset_tag"))), query);
            }

            default:
                throw new FreshserviceException($"Unknown lookup source '{source}'.");
        }
    }

    /// <summary>
    /// Checks the connection in two steps, so a failure says which part is wrong: first the ticket list
    /// (every Freshservice account has it, so "not found" there means the address is wrong), then the
    /// agent group list (used by group lookups and "assign to group" settings).
    /// </summary>
    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var host = _http.BaseAddress?.Host ?? "?";
        try
        {
            using var _ = await SendAsync(HttpMethod.Get, "api/v2/tickets?per_page=1", null, ct);
        }
        catch (FreshserviceException ex) when (ex.StatusCode == 404)
        {
            throw new FreshserviceException($"{host} answered \"not found\" for the Freshservice API, so the address is probably not your Freshservice account. "
                + "Set Freshservice:Domain to the address you see when you sign in to Freshservice as an agent (for example bergen.freshservice.com) — "
                + "not a custom web address, the self-service portal, or a link with a path after the name. Then restart the site.", 404, ex);
        }
        catch (FreshserviceException ex) when (ex.StatusCode is 401 or 403)
        {
            throw new FreshserviceException($"{host} was reached, but it refused the API key: {ex.Message}", ex.StatusCode, ex);
        }
        try
        {
            var groups = await GroupsAsync(ct);
            return $"Connected to {host}: tickets can be read, and {groups.Count}{(groups.Count >= 100 ? "+" : "")} agent group{(groups.Count == 1 ? "" : "s")} were found.";
        }
        catch (FreshserviceException ex)
        {
            return $"Connected to {host}: tickets can be read. The agent group list could not be read ({ex.Message}), so group lookups may not work — tickets themselves will.";
        }
    }

    /// <summary>
    /// Agent groups. Freshservice serves them at api/v2/groups; accounts on the newer API also have
    /// api/v2/agent_groups, which is tried when the first answers "not found".
    /// </summary>
    private async Task<List<LookupItem>> GroupsAsync(CancellationToken ct)
    {
        try
        {
            return await ListAllAsync("api/v2/groups", "groups", e => Item(e, "name"), ct);
        }
        catch (FreshserviceException ex) when (ex.StatusCode == 404)
        {
            return await ListAllAsync("api/v2/agent_groups", "agent_groups", e => Item(e, "name"), ct);
        }
    }

    // ---- plumbing --------------------------------------------------------------------

    private async Task<List<LookupItem>> ListAllAsync(string path, string property, Func<JsonElement, LookupItem> map, CancellationToken ct)
    {
        var all = new List<LookupItem>();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; page <= MaxPages; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, $"{path}{separator}per_page=100&page={page}", null, ct);
            var items = Items(doc!, property, map);
            all.AddRange(items);
            if (items.Count < 100) break;
        }
        return all.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Turns items whose Detail holds a parent ID into items named "Parent › Child".</summary>
    public static List<LookupItem> WithFullPaths(IReadOnlyList<LookupItem> items)
    {
        var byId = new Dictionary<string, LookupItem>();
        foreach (var item in items) byId.TryAdd(item.Id, item);
        var result = new List<LookupItem>();
        foreach (var item in items)
        {
            var parts = new List<string> { item.Name };
            var seen = new HashSet<string> { item.Id };
            var parentId = item.Detail;
            // Bounded and cycle-safe, in case the data is odd.
            while (!string.IsNullOrEmpty(parentId) && parts.Count < 6 && seen.Add(parentId) && byId.TryGetValue(parentId, out var parent))
            {
                parts.Insert(0, parent.Name);
                parentId = parent.Detail;
            }
            result.Add(new LookupItem(item.Id, string.Join(" › ", parts)));
        }
        return result.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<LookupItem> Items(JsonDocument doc, string property, Func<JsonElement, LookupItem> map) =>
        doc.RootElement.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(map).ToList()
            : new List<LookupItem>();

    private static IReadOnlyList<LookupItem> Filter(IReadOnlyList<LookupItem> items, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return items;
        return items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || (i.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                || (i.Email?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    private static LookupItem Item(JsonElement e, string nameProperty) => new(Id(e), Str(e, nameProperty) ?? "(unnamed)");

    private static string Id(JsonElement e) =>
        e.TryGetProperty("id", out var id) ? (id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? "") : "";

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static string PersonName(JsonElement e)
    {
        var name = $"{Str(e, "first_name")} {Str(e, "last_name")}".Trim();
        return name.Length > 0 ? name : Str(e, "primary_email") ?? Str(e, "email") ?? "(no name)";
    }

    private static Dictionary<string, object?> Unwrap(JsonDocument? doc, string property)
    {
        if (doc is null) throw new FreshserviceException("Freshservice returned an empty response.");
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var inner) && inner.ValueKind == JsonValueKind.Object)
            return Json.ToValues(inner);
        if (root.ValueKind == JsonValueKind.Object) return Json.ToValues(root);
        throw new FreshserviceException("Freshservice returned an unexpected response.");
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool notFoundIsNull = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                // Leave unset fields out entirely rather than sending null (which would clear them on update).
                var payload = body is Dictionary<string, object?> map
                    ? map.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value)
                    : body;
                request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
            {
                // Respect the rate limiter, but never stall a page for long.
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                if (wait > TimeSpan.FromSeconds(10)) wait = TimeSpan.FromSeconds(10);
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                await Task.Delay(wait, ct);
                continue;
            }
            // Only idempotent reads are retried on server errors; a retried POST could create a duplicate ticket.
            if ((int)response.StatusCode >= 500 && method == HttpMethod.Get && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull) return null;
            if (!response.IsSuccessStatusCode)
                throw new FreshserviceException(DescribeError(response.StatusCode, text), (int)response.StatusCode);
            if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}");
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new FreshserviceException("Freshservice returned a response that is not JSON (check the domain).", (int)response.StatusCode, ex);
            }
        }
    }

    private static string DescribeError(HttpStatusCode status, string body)
    {
        var summary = status switch
        {
            HttpStatusCode.Unauthorized => "The API key was rejected (401). Check the key and that the account is active.",
            HttpStatusCode.Forbidden => "The API key does not have permission for this action (403).",
            HttpStatusCode.NotFound => "Not found (404). The ticket or item may have been deleted, or the ID is wrong.",
            HttpStatusCode.TooManyRequests => "Freshservice rate limit reached (429). Try again in a minute.",
            _ => $"Request failed ({(int)status}).",
        };
        // Freshservice validation errors: {"description":"Validation failed","errors":[{"field":"email","message":"…"}]}
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array)
            {
                var details = errors.EnumerateArray().Take(5).Select(e =>
                    e.ValueKind == JsonValueKind.Object ? $"{Str(e, "field")}: {Str(e, "message")}".Trim(' ', ':') : e.ToString());
                var joined = string.Join("; ", details);
                if (joined.Length > 0) summary += " " + (joined.Length > 500 ? joined[..500] : joined);
            }
        }
        catch (JsonException)
        {
            // Body was not JSON; the status summary is enough.
        }
        return summary;
    }
}
