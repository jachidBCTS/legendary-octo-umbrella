namespace SchoolFlow.Core.Freshservice;

public sealed class FreshserviceException : Exception
{
    public int? StatusCode { get; }
    public FreshserviceException(string message, int? statusCode = null, Exception? inner = null) : base(message, inner) =>
        StatusCode = statusCode;
}

public sealed class TicketCreate
{
    public string Subject { get; set; } = "";
    public string DescriptionHtml { get; set; } = "";
    public string RequesterEmail { get; set; } = "";
    public int Priority { get; set; } = 2;
    public int Status { get; set; } = 2;
    public string? Type { get; set; }
    public long? GroupId { get; set; }
    public long? DepartmentId { get; set; }
    public string? Category { get; set; }
    public string? SubCategory { get; set; }
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object?> CustomFields { get; set; } = new();
}

public sealed class TicketUpdate
{
    public int? Priority { get; set; }
    public int? Status { get; set; }
    public long? GroupId { get; set; }
    public long? ResponderId { get; set; }
    public Dictionary<string, object?> CustomFields { get; set; } = new();

    public bool IsEmpty => Priority is null && Status is null && GroupId is null && ResponderId is null && CustomFields.Count == 0;
}

public sealed record LookupItem(string Id, string Name, string? Email = null, string? Detail = null);

/// <summary>Everything the workflow engine and the form lookups need from Freshservice.</summary>
public interface IFreshserviceClient
{
    /// <summary>True when talking to a real Freshservice account; false in simulation mode.</summary>
    bool IsLive { get; }

    /// <summary>Returns the created ticket as a record (id, subject, status, priority, …).</summary>
    Task<Dictionary<string, object?>> CreateTicketAsync(TicketCreate ticket, CancellationToken ct = default);

    Task<Dictionary<string, object?>> UpdateTicketAsync(long ticketId, TicketUpdate update, CancellationToken ct = default);

    Task AddNoteAsync(long ticketId, string bodyHtml, bool isPrivate, CancellationToken ct = default);

    Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default);

    Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default);

    /// <summary>Places a service catalog request. Returns the resulting service request ticket record.</summary>
    Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity,
        Dictionary<string, object?> customFields, CancellationToken ct = default);

    /// <summary>Values for lookup fields and builder dropdowns. <paramref name="userEmail"/> scopes "my_assets".</summary>
    Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default);

    /// <summary>Verifies the connection; returns a short human-readable summary or throws.</summary>
    Task<string> TestConnectionAsync(CancellationToken ct = default);
}

public static class TicketFields
{
    public static readonly (int Value, string Label)[] Priorities = { (1, "Low"), (2, "Medium"), (3, "High"), (4, "Urgent") };
    public static readonly (int Value, string Label)[] Statuses = { (2, "Open"), (3, "Pending"), (4, "Resolved"), (5, "Closed") };
}
