using Microsoft.EntityFrameworkCore;

namespace SchoolFlow.Web.Data;

public class Workflow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Color { get; set; } = "indigo";
    /// <summary>The definition being edited in the builder.</summary>
    public string DraftJson { get; set; } = "{}";
    /// <summary>The definition people actually run; null until first published.</summary>
    public string? PublishedJson { get; set; }
    public int PublishedVersion { get; set; }
    public DateTime? PublishedAt { get; set; }
    /// <summary>Trigger type of the published version ("form" or "ticket").</summary>
    public string TriggerType { get; set; } = "form";
    /// <summary>Published workflows can be switched off without deleting them.</summary>
    public bool IsEnabled { get; set; } = true;
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }

    public bool IsLive => PublishedJson is not null && IsEnabled;
    public bool HasUnpublishedChanges => PublishedJson is not null && PublishedJson != DraftJson;
}

public class WorkflowRun
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public int WorkflowVersion { get; set; }
    /// <summary>Snapshot of the definition this run started with, so later edits never change a run in flight.</summary>
    public string DefinitionJson { get; set; } = "{}";
    public string Status { get; set; } = "";
    public string? CurrentNodeId { get; set; }
    public string ContextJson { get; set; } = "{}";
    public string SubmitterEmail { get; set; } = "";
    public string SubmitterName { get; set; } = "";
    /// <summary>Set while the run is paused on a "wait for ticket" step.</summary>
    public long? WaitingTicketId { get; set; }
    /// <summary>The most recent Freshservice ticket this run created or handled.</summary>
    public long? TicketId { get; set; }
    /// <summary>For ticket-triggered runs: the ticket whose event started the run.</summary>
    public long? TriggerTicketId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Optimistic concurrency token: stops two people (or a person and a webhook) advancing the same run twice.</summary>
    public int Version { get; set; }
}

public class RunEventRow
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public DateTime At { get; set; }
    public string? NodeId { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
}

public class ApprovalTask
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string NodeId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    /// <summary>Lower-case emails wrapped in semicolons (";a@x.org;b@x.org;") so one can be matched exactly.</summary>
    public string Assignees { get; set; } = "";
    public string Status { get; set; } = ApprovalStatus.Pending;
    public string? DecidedBy { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    public IEnumerable<string> AssigneeList => Assignees.Split(';', StringSplitOptions.RemoveEmptyEntries);
    public bool IsAssignedTo(string email) => Assignees.Contains(";" + email.ToLowerInvariant() + ";", StringComparison.Ordinal);
    public static string Pack(IEnumerable<string> emails) => ";" + string.Join(";", emails.Select(e => e.Trim().ToLowerInvariant())) + ";";
}

public static class ApprovalStatus
{
    public const string Pending = "Pending", Approved = "Approved", Rejected = "Rejected", Cancelled = "Cancelled";
}

public class AppUser
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Staff;
    public DateTime LastSeenAt { get; set; }
}

public static class Roles
{
    public const string Admin = "Admin", Builder = "Builder", Staff = "Staff";
    public static readonly string[] All = { Staff, Builder, Admin };
}

public class WebhookLog
{
    public int Id { get; set; }
    public DateTime At { get; set; }
    public string Summary { get; set; } = "";
    public string Outcome { get; set; } = "";
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<RunEventRow> RunEvents => Set<RunEventRow>();
    public DbSet<ApprovalTask> ApprovalTasks => Set<ApprovalTask>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<WebhookLog> WebhookLogs => Set<WebhookLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Workflow>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(30);
            e.Property(x => x.TriggerType).HasMaxLength(20);
            e.Property(x => x.CreatedBy).HasMaxLength(320);
            e.Property(x => x.UpdatedBy).HasMaxLength(320);
        });
        b.Entity<WorkflowRun>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.WorkflowName).HasMaxLength(200);
            e.Property(x => x.CurrentNodeId).HasMaxLength(100);
            e.Property(x => x.SubmitterEmail).HasMaxLength(320);
            e.Property(x => x.SubmitterName).HasMaxLength(300);
            e.HasIndex(x => x.SubmitterEmail);
            e.HasIndex(x => x.WaitingTicketId);
            e.HasIndex(x => new { x.WorkflowId, x.TriggerTicketId });
        });
        b.Entity<RunEventRow>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(30);
            e.Property(x => x.NodeId).HasMaxLength(100);
            e.HasIndex(x => x.RunId);
        });
        b.Entity<ApprovalTask>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.NodeId).HasMaxLength(100);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.DecidedBy).HasMaxLength(320);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.Status);
        });
        b.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Email);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Role).HasMaxLength(20);
        });
        b.Entity<WebhookLog>(e =>
        {
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.Outcome).HasMaxLength(1000);
        });
    }
}
