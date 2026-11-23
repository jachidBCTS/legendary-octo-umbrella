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
    /// <summary>The third level of the category ("Item"), below the sub-category.</summary>
    public string? ItemCategory { get; set; }
    /// <summary>People copied on the ticket (Freshservice cc_emails).</summary>
    public List<string> CcEmails { get; set; } = new();
    /// <summary>Filled in by the client: values it had to leave out or change to fit the ticket form (shown on the timeline).</summary>
    public List<string> Notes { get; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object?> CustomFields { get; set; } = new();
}

/// <summary>
/// A field on the Freshservice ticket form: what an admin sees (<see cref="Label"/>) and what the API
/// needs (<see cref="Name"/>; custom fields are filled under custom_fields with this name). Choices of a
/// nested field (category › sub-category › item) are listed as "A > B > C".
/// </summary>
/// <param name="CustomerLabel">What requesters see on the portal, when it differs from the agents' label (e.g. "Room Number" vs "Room #").</param>
public sealed record TicketField(string Name, string Label, string Type, bool IsDefault, IReadOnlyList<string> Choices, string? CustomerLabel = null);

public sealed class TicketUpdate
{
    public int? Priority { get; set; }
    public int? Status { get; set; }
    public long? GroupId { get; set; }
    public long? ResponderId { get; set; }
    public Dictionary<string, object?> CustomFields { get; set; } = new();
    /// <summary>Filled in by the client: values it had to leave out (shown on the timeline).</summary>
    public List<string> Notes { get; } = new();

    public bool IsEmpty => Priority is null && Status is null && GroupId is null && ResponderId is null && CustomFields.Count == 0;
}

/// <summary>A file to attach to a ticket.</summary>
public sealed record TicketFile(string Name, string ContentType, byte[] Content);

/// <summary>A reply on a ticket that the requester can see (never a private agent note).</summary>
public sealed record TicketReply(DateTime AtUtc, bool FromHelpDesk, string Text);

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

    /// <summary>Adds files to an existing ticket.</summary>
    Task AttachFilesAsync(long ticketId, IReadOnlyList<TicketFile> files, CancellationToken ct = default);

    Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default);

    Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default);

    /// <summary>Places a service catalog request. Returns the resulting service request ticket record.</summary>
    Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity,
        Dictionary<string, object?> customFields, CancellationToken ct = default);

    /// <summary>Values for lookup fields and builder dropdowns. <paramref name="userEmail"/> scopes "my_assets".</summary>
    Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default);

    /// <summary>Verifies the connection; returns a short human-readable summary or throws.</summary>
    Task<string> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// The ticket's public conversation, oldest first: help desk replies and the requester's own
    /// emails. Private notes are left out here, never later.
    /// </summary>
    Task<IReadOnlyList<TicketReply>> PublicRepliesAsync(long ticketId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TicketReply>>(Array.Empty<TicketReply>());

    /// <summary>The fields on the ticket form, with their API names and choices (for setting up Create ticket steps).</summary>
    Task<IReadOnlyList<TicketField>> TicketFieldsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TicketField>>(Array.Empty<TicketField>());
}

public static class TicketFields
{
    public static readonly (int Value, string Label)[] Priorities = { (1, "Low"), (2, "Medium"), (3, "High"), (4, "Urgent") };
    public static readonly (int Value, string Label)[] Statuses = { (2, "Open"), (3, "Pending"), (4, "Resolved"), (5, "Closed") };

    /// <summary>A ticket status number as a word; custom statuses (not in the list) read "In progress".</summary>
    public static string StatusLabel(int status) =>
        Statuses.Where(s => s.Value == status).Select(s => s.Label).FirstOrDefault() ?? "In progress";
}
