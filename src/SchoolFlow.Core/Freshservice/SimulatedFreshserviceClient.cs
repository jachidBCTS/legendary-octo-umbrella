using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Freshservice;

/// <summary>
/// Stand-in used until a Freshservice domain and API key are configured. Nothing leaves the server;
/// tickets get made-up numbers and every timeline entry is labelled "[Simulated]".
/// </summary>
public sealed class SimulatedFreshserviceClient : IFreshserviceClient
{
    private readonly object _gate = new();
    private long _nextId = 90000;
    private readonly Dictionary<long, Dictionary<string, object?>> _tickets = new();

    public bool IsLive => false;

    /// <summary>Everything "sent" so far, for tests and diagnostics.</summary>
    public List<(string Action, object Payload)> Calls { get; } = new();

    public Task<Dictionary<string, object?>> CreateTicketAsync(TicketCreate ticket, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var record = Values.NewDict();
            record["id"] = (decimal)(++_nextId);
            record["subject"] = ticket.Subject;
            record["description"] = ticket.DescriptionHtml;
            record["email"] = ticket.RequesterEmail;
            record["priority"] = (decimal)ticket.Priority;
            record["status"] = (decimal)ticket.Status;
            record["type"] = ticket.Type ?? "Incident";
            record["group_id"] = ticket.GroupId is long g ? (decimal)g : null;
            record["department_id"] = ticket.DepartmentId is long d ? (decimal)d : null;
            record["custom_fields"] = ToRecord(ticket.CustomFields);
            _tickets[_nextId] = record;
            Calls.Add(("create_ticket", ticket));
            return Task.FromResult(Copy(record));
        }
    }

    public Task<Dictionary<string, object?>> UpdateTicketAsync(long ticketId, TicketUpdate update, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(ticketId, out var record))
            {
                // Tickets that originate in Freshservice are unknown to the simulator; accept them.
                record = Values.NewDict();
                record["id"] = (decimal)ticketId;
                _tickets[ticketId] = record;
            }
            if (update.Priority is int p) record["priority"] = (decimal)p;
            if (update.Status is int s) record["status"] = (decimal)s;
            if (update.GroupId is long g) record["group_id"] = (decimal)g;
            if (update.ResponderId is long r) record["responder_id"] = (decimal)r;
            if (update.CustomFields.Count > 0)
            {
                var existing = record.GetValueOrDefault("custom_fields") as Dictionary<string, object?> ?? Values.NewDict();
                foreach (var kv in update.CustomFields) existing[kv.Key] = Values.Normalize(kv.Value);
                record["custom_fields"] = existing;
            }
            Calls.Add(("update_ticket", (ticketId, update)));
            return Task.FromResult(Copy(record));
        }
    }

    public Task AddNoteAsync(long ticketId, string bodyHtml, bool isPrivate, CancellationToken ct = default)
    {
        lock (_gate) Calls.Add(("add_note", (ticketId, bodyHtml, isPrivate)));
        return Task.CompletedTask;
    }

    public Task AttachFilesAsync(long ticketId, IReadOnlyList<TicketFile> files, CancellationToken ct = default)
    {
        lock (_gate) Calls.Add(("attach_files", (ticketId, files)));
        return Task.CompletedTask;
    }

    public Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_tickets.TryGetValue(ticketId, out var record) ? Copy(record) : null);
    }

    public Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default)
    {
        var person = Values.NewDict();
        var local = email.Split('@')[0];
        person["id"] = 5000m + Math.Abs(StableHash(email) % 1000);
        person["name"] = string.Join(' ', local.Split('.', '_', '-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        person["email"] = email;
        person["department_names"] = new List<object?> { "Sample Department" };
        person["is_agent"] = false;
        return Task.FromResult<Dictionary<string, object?>?>(person);
    }

    public Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity,
        Dictionary<string, object?> customFields, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var record = Values.NewDict();
            record["id"] = (decimal)(++_nextId);
            record["subject"] = $"Request for catalog item {itemDisplayId}";
            record["email"] = requesterEmail;
            record["status"] = 2m;
            record["priority"] = 2m;
            record["type"] = "Service Request";
            record["quantity"] = (decimal)quantity;
            _tickets[_nextId] = record;
            Calls.Add(("service_request", (itemDisplayId, requesterEmail, quantity, customFields)));
            return Task.FromResult(Copy(record));
        }
    }

    public Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default)
    {
        LookupItem[] items = source switch
        {
            LookupSources.Departments => new[]
            {
                new LookupItem("101", "Athletics"), new LookupItem("102", "English"), new LookupItem("103", "Main Office"),
                new LookupItem("104", "Mathematics"), new LookupItem("105", "Science"), new LookupItem("106", "Technology"),
            },
            LookupSources.Groups => new[]
            {
                new LookupItem("201", "Help Desk"), new LookupItem("202", "Network & Systems"),
                new LookupItem("203", "Purchasing"), new LookupItem("204", "Facilities"),
            },
            LookupSources.Locations => new[]
            {
                new LookupItem("301", "Main Building"), new LookupItem("302", "Library"), new LookupItem("303", "Gymnasium"),
            },
            LookupSources.Agents => new[]
            {
                new LookupItem("401", "Sample Agent", "agent@example.org"),
            },
            LookupSources.Requesters => new[]
            {
                new LookupItem("501", "Alex Rivera", "arivera@example.org"), new LookupItem("502", "Jordan Lee", "jlee@example.org"),
                new LookupItem("503", "Sam Patel", "spatel@example.org"),
            },
            LookupSources.MyAssets => new[]
            {
                new LookupItem("7001", "Chromebook 3100", null, "CB-004512"), new LookupItem("7002", "iPad (9th gen)", null, "IP-001877"),
            },
            _ => throw new FreshserviceException($"Unknown lookup source '{source}'."),
        };
        var q = query?.Trim();
        IReadOnlyList<LookupItem> result = string.IsNullOrEmpty(q)
            ? items
            : items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || (i.Email?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                               || (i.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
        return Task.FromResult(result);
    }

    public Task<string> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult("Simulation mode: no Freshservice domain and API key are configured, so nothing is sent.");

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s.ToLowerInvariant()) h = h * 31 + c;
            return h == int.MinValue ? 0 : h;
        }
    }

    private static Dictionary<string, object?> ToRecord(Dictionary<string, object?> source)
    {
        var record = Values.NewDict();
        foreach (var kv in source) record[kv.Key] = Values.Normalize(kv.Value);
        return record;
    }

    private static Dictionary<string, object?> Copy(Dictionary<string, object?> source)
    {
        var copy = Values.NewDict();
        foreach (var kv in source)
            copy[kv.Key] = kv.Value is Dictionary<string, object?> inner ? Copy(inner) : kv.Value;
        return copy;
    }
}
