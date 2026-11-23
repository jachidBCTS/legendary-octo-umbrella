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
    /// <summary>
    /// Who may edit this workflow and see all of its requests, as lower-case emails packed like
    /// ";a@x.org;b@x.org;". Admins always can. Empty means admins only.
    /// </summary>
    public string Owners { get; set; } = "";
    /// <summary>
    /// Entra group IDs whose members may submit this form, packed the same way. Empty means everyone
    /// who can sign in. Takes effect as soon as it is saved (it is not part of the published version).
    /// </summary>
    public string AudienceGroups { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }

    public bool IsLive => PublishedJson is not null && IsEnabled;
    public IEnumerable<string> OwnerList => Owners.Split(';', StringSplitOptions.RemoveEmptyEntries);
    public IEnumerable<string> AudienceList => AudienceGroups.Split(';', StringSplitOptions.RemoveEmptyEntries);
    public bool IsOwnedBy(string email) => email.Length > 0 && Owners.Contains(";" + email.ToLowerInvariant() + ";", StringComparison.Ordinal);
    public bool HasUnpublishedChanges => PublishedJson is not null && PublishedJson != DraftJson;
}

/// <summary>A workflow definition as runs started with it: stored once, however many runs use it (see <see cref="WorkflowRun.DefinitionId"/>).</summary>
public class RunDefinition
{
    public int Id { get; set; }
    /// <summary>SHA-256 of <see cref="Json"/> (hex), so the same definition is never stored twice.</summary>
    public string Hash { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public class WorkflowRun
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public int WorkflowVersion { get; set; }
    /// <summary>
    /// The definition this run started with, so later edits never change a run in flight. Shared: every
    /// run on the same version points at one <see cref="RunDefinition"/> row. Null only on runs from before
    /// that change until <see cref="SchoolFlow.Web.Services.DefinitionStore.MoveLegacy"/> has moved them (on start).
    /// </summary>
    public int? DefinitionId { get; set; }
    /// <summary>Where each run's own copy used to be kept (column "DefinitionJson"). Emptied on start; never read otherwise.</summary>
    public string? LegacyDefinitionJson { get; set; }
    private string? _definitionJson;
    /// <summary>
    /// The definition's JSON, filled in by <see cref="SchoolFlow.Web.Services.DefinitionStore"/> after loading
    /// (not a column). Reading it before it was filled is a programming error, so it says so.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string DefinitionJson
    {
        get => _definitionJson ?? throw new InvalidOperationException($"Request #{Id}: its definition was not loaded (call DefinitionStore.FillAsync after reading it).");
        set => _definitionJson = value;
    }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool HasDefinition => _definitionJson is not null;

    /// <summary>
    /// What request lists read: everything but the answers (<see cref="ContextJson"/>) and the old copy, so
    /// a page of requests moves only a few hundred bytes per row from the database.
    /// </summary>
    public static readonly System.Linq.Expressions.Expression<Func<WorkflowRun, WorkflowRun>> ListColumns = r => new WorkflowRun
    {
        Id = r.Id, WorkflowId = r.WorkflowId, WorkflowName = r.WorkflowName, WorkflowVersion = r.WorkflowVersion, DefinitionId = r.DefinitionId,
        Status = r.Status, CurrentNodeId = r.CurrentNodeId, SubmitterEmail = r.SubmitterEmail, SubmitterName = r.SubmitterName,
        WaitingTicketId = r.WaitingTicketId, TicketId = r.TicketId, TriggerTicketId = r.TriggerTicketId, Error = r.Error,
        DepartmentId = r.DepartmentId, DepartmentName = r.DepartmentName, CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
        Version = r.Version, LastPolledAt = r.LastPolledAt,
    };
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
    /// <summary>The department the submitter chose on the form (first Department field), for filtering and reporting.</summary>
    public long? DepartmentId { get; set; }
    public string DepartmentName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Optimistic concurrency token: stops two people (or a person and a webhook) advancing the same run twice.</summary>
    public int Version { get; set; }
    /// <summary>When SchoolFlow last asked Freshservice about the ticket this run is waiting on (the safety net for missed webhook events).</summary>
    public DateTime? LastPolledAt { get; set; }
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
    /// <summary>"approval" (approve / reject / send back) or "task" (fill in the step's fields).</summary>
    public string Kind { get; set; } = TaskKinds.Approval;
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    /// <summary>Lower-case emails wrapped in semicolons (";a@x.org;b@x.org;") so one can be matched exactly.</summary>
    public string Assignees { get; set; } = "";
    public string Status { get; set; } = ApprovalStatus.Pending;
    /// <summary>"any": one assignee decides. "all": every assignee must approve (one rejection rejects).</summary>
    public string Rule { get; set; } = "any";
    /// <summary>For "all" tasks: assignees who have approved so far, packed like <see cref="Assignees"/>.</summary>
    public string ApprovedBy { get; set; } = "";
    /// <summary>For chain steps: "Step 2 of 3".</summary>
    public string Position { get; set; } = "";
    public string? DecidedBy { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// Stand-ins for approvers who are away, packed as ";standin=original;". A stand-in decides in the
    /// original approver's place.
    /// </summary>
    public string StandIns { get; set; } = "";
    /// <summary>People the step was escalated to because it sat unanswered, packed. Any of them can decide the step.</summary>
    public string Escalated { get; set; } = "";
    /// <summary>Everyone in <see cref="StandIns"/> and <see cref="Escalated"/>, packed, so "can this person act?" is one text match.</summary>
    public string Extra { get; set; } = "";
    /// <summary>Remind every this many school days (0 = never); escalate after this many (0 = never).</summary>
    public int RemindDays { get; set; }
    public int EscalateDays { get; set; }
    /// <summary>Who to escalate to, packed.</summary>
    public string EscalateTo { get; set; } = "";
    public DateTime? LastReminderAt { get; set; }
    public int ReminderCount { get; set; }
    public DateTime? EscalatedAt { get; set; }
    /// <summary>The day this should be done by (a calendar date, no time), if the step set one.</summary>
    public DateTime? DueOn { get; set; }
    /// <summary>Who else is told if it is not done by <see cref="DueOn"/>, packed like <see cref="Assignees"/>.</summary>
    public string DueNotify { get; set; } = "";
    /// <summary>When the "due soon" heads-up and the "overdue" notice were sent (each is sent once).</summary>
    public DateTime? DueSoonSentAt { get; set; }
    public DateTime? OverdueSentAt { get; set; }

    public IEnumerable<string> DueNotifyList => DueNotify.Split(';', StringSplitOptions.RemoveEmptyEntries);

    public IEnumerable<string> EscalatedList => Escalated.Split(';', StringSplitOptions.RemoveEmptyEntries);
    public IEnumerable<string> EscalateToList => EscalateTo.Split(';', StringSplitOptions.RemoveEmptyEntries);
    public IEnumerable<(string StandIn, string For)> StandInPairs => StandIns.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=')).Where(p => p.Length == 2).Select(p => (p[0], p[1]));
    /// <summary>The approver this person is standing in for on this task, or null.</summary>
    public string? StandsInFor(string email) => StandInPairs.Where(p => p.StandIn == email.ToLowerInvariant()).Select(p => p.For).FirstOrDefault();
    public bool IsEscalatedTo(string email) => Escalated.Contains(";" + email.ToLowerInvariant() + ";", StringComparison.Ordinal);
    /// <summary>Assigned, standing in for someone assigned, or escalated to.</summary>
    public bool CanBeDecidedBy(string email) => email.Length > 0 && (IsAssignedTo(email) || StandsInFor(email) is not null || IsEscalatedTo(email));
    /// <summary>Has nothing left to do: already approved (themselves, or the person they stand in for).</summary>
    public bool IsDoneFor(string email) => HasApproved(email) || (StandsInFor(email) is { } original && HasApproved(original));
    public void RebuildExtra()
    {
        var extra = StandInPairs.Select(p => p.StandIn).Concat(EscalatedList).Distinct().ToList();
        Extra = extra.Count == 0 ? "" : Pack(extra);
    }

    public IEnumerable<string> AssigneeList => Assignees.Split(';', StringSplitOptions.RemoveEmptyEntries);
    public IEnumerable<string> ApprovedByList => ApprovedBy.Split(';', StringSplitOptions.RemoveEmptyEntries);
    public bool HasApproved(string email) => ApprovedBy.Contains(";" + email.ToLowerInvariant() + ";", StringComparison.Ordinal);
    public bool IsAssignedTo(string email) => Assignees.Contains(";" + email.ToLowerInvariant() + ";", StringComparison.Ordinal);
    public static string Pack(IEnumerable<string> emails) => ";" + string.Join(";", emails.Select(e => e.Trim().ToLowerInvariant())) + ";";
}

public static class ApprovalStatus
{
    public const string Pending = "Pending", Approved = "Approved", Rejected = "Rejected", Cancelled = "Cancelled",
        /// <summary>The approver sent the request back to the requester for changes.</summary>
        Returned = "Returned",
        /// <summary>A Task step was completed (its fields filled in).</summary>
        Done = "Done";
}

/// <summary>How far a person's student access reaches.</summary>
public static class StudentScopes
{
    public const string Location = "location", District = "district", All = "all";
    public static string Normalize(string? value) => value is District or All ? value : Location;
    public static string Describe(string? value) => Normalize(value) switch { District => "Whole district", All => "All districts", _ => "Own location" };
}

/// <summary>What a pending item asks its assignee to do.</summary>
public static class TaskKinds
{
    public const string Approval = "approval", Task = "task";
}

/// <summary>
/// A named group of staff (for example "Business Office – Technical Schools") that workflow steps
/// can name as [Team name] instead of listing people, so a change of staff is made in one place.
/// </summary>
/// <summary>A category forms are grouped under on the home page (Admin → Categories), in the order admins choose.</summary>
public class WorkflowCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>
/// A form's letterhead: an image shown at the top of the form and of its printout / PDF. One per workflow,
/// kept in the database (small, and backed up with everything else). Like owners, it is not versioned.
/// </summary>
public class FormImage
{
    public int WorkflowId { get; set; }
    public string ContentType { get; set; } = "";
    public string FileName { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lower-case emails packed like ";a@x.org;b@x.org;".</summary>
    public string Members { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";

    public IEnumerable<string> MemberList => Members.Split(';', StringSplitOptions.RemoveEmptyEntries);
}

public class AppUser
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Staff;
    public DateTime LastSeenAt { get; set; }
    /// <summary>Whether this person also wants notifications by email (they always get them in the app).</summary>
    public bool EmailNotifications { get; set; } = true;
    /// <summary>
    /// Daily summary (owner, Nov 23, 2026): minutes after midnight, local time, when held emails are sent
    /// as one summary on school days. Null = each email right away.
    /// </summary>
    public int? DigestMinute { get; set; }
    /// <summary>The local date the last daily summary went out (one a day at most).</summary>
    public DateTime? DigestSentOn { get; set; }
    /// <summary>The Entra group IDs seen at this person's last sign-in (packed), so admins can see where a role comes from.</summary>
    public string Groups { get; set; } = "";
    /// <summary>The role Entra itself assigned through an app role ("Admin"/"Builder"), if any, at last sign-in.</summary>
    public string EntraRole { get; set; } = "";
    /// <summary>May look students up and see student details on requests. Off for everyone, admins included, until granted.</summary>
    public bool StudentAccess { get; set; }
    /// <summary>
    /// With <see cref="StudentAccess"/>: which students the person may see — those at their own
    /// location (from the staff list), their whole district, or every district. See <see cref="StudentScopes"/>.
    /// </summary>
    public string StudentScope { get; set; } = StudentScopes.Location;
}

/// <summary>
/// A Microsoft Entra security group that SchoolFlow knows about. Admins register a group here (its
/// name and its Object ID from Entra) to grant its members a role and/or to limit forms to them.
/// </summary>
public class DirectoryGroup
{
    public int Id { get; set; }
    /// <summary>The group's Object ID in Entra (a GUID), lower-case.</summary>
    public string ObjectId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"" (no role, just usable as a form audience), "Builder" or "Admin".</summary>
    public string Role { get; set; } = "";
}

/// <summary>A record of something an admin or builder did that changes who can do what, or overrides the normal flow.</summary>
public class AuditEntry
{
    public int Id { get; set; }
    public DateTime At { get; set; }
    public string ActorEmail { get; set; } = "";
    public string ActorName { get; set; } = "";
    /// <summary>Short machine-friendly kind, e.g. "role.changed", "workflow.published".</summary>
    public string Action { get; set; } = "";
    /// <summary>What it was done to, in words: a person, a workflow, a request.</summary>
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>A school department. Admins set who gives final approval for requests categorised under it.</summary>
public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Lower-case emails packed as ";a@x.org;b@x.org;".</summary>
    public string FinalApprovers { get; set; } = "";
    public string FinalRule { get; set; } = "any";
    public bool IsActive { get; set; } = true;

    public List<string> FinalApproverList => FinalApprovers.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
}

/// <summary>
/// A school location (building, room, campus). Usually pulled from Freshservice with the Sync button
/// on Admin → Locations, but admins can also add their own.
/// </summary>
public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The location's ID in Freshservice's Locations table (the old source of Sync, no longer used); null otherwise.</summary>
    public long? FreshserviceId { get; set; }
    /// <summary>One of the choices of the ticket form's Location field in Freshservice (Sync keeps these; name changed there).</summary>
    public bool TicketChoice { get; set; }
    /// <summary>The district the location belongs to (its short code, e.g. BCTS); blank when not set.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(40)]
    public string? District { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A form someone has started and saved to finish later. Only its owner can see it.</summary>
public class FormDraft
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public string OwnerEmail { get; set; } = "";
    /// <summary>Short description built from the first answers, for the drafts list.</summary>
    public string Summary { get; set; } = "";
    /// <summary>The answers exactly as typed (not yet validated); checked again when the draft is opened and submitted.</summary>
    public string ValuesJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// An uploaded file. The content is a file in the uploads folder (named <see cref="StoredName"/>);
/// this row says what it is, who uploaded it and which request it belongs to.
/// </summary>
public class Attachment
{
    public int Id { get; set; }
    /// <summary>Random identifier used in links and form values; cannot be guessed from another file's.</summary>
    public string PublicId { get; set; } = "";
    /// <summary>Random name of the file on disk. Never derived from what the person called their file.</summary>
    public string StoredName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string OwnerEmail { get; set; } = "";
    public int WorkflowId { get; set; }
    /// <summary>The request this file was submitted with; null while the form is still being filled in.</summary>
    public int? RunId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>"I'm away: let this person approve for me between these dates."</summary>
public class Delegation
{
    public int Id { get; set; }
    public string FromEmail { get; set; } = "";
    public string ToEmail { get; set; } = "";
    /// <summary>First and last day (inclusive, server-local dates).</summary>
    public DateTime StartsOn { get; set; }
    public DateTime EndsOn { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsActiveOn(DateTime day) => StartsOn.Date <= day.Date && day.Date <= EndsOn.Date;
}

/// <summary>A message in the discussion on a request.</summary>
public class RunComment
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public string AuthorEmail { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>A weekday the school is closed. "School day" date maths, reminders and escalation skip these.</summary>
/// <summary>A published version of a workflow, kept so an earlier one can be brought back.</summary>
public class WorkflowVersion
{
    public int Id { get; set; }
    public int WorkflowId { get; set; }
    public int Version { get; set; }
    public string Json { get; set; } = "{}";
    public DateTime PublishedAt { get; set; }
    public string PublishedBy { get; set; } = "";
}

/// <summary>A fixed amount kept by admins (mileage rate, meal rates…); forms read it as <c>rates.key</c>.</summary>
public class Rate
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal Value { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

public class ClosedDay
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A saved approval chain. OwnerEmail "" means a shared template; otherwise it is that person's own.</summary>
public class ApprovalChainTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OwnerEmail { get; set; } = "";
    /// <summary>JSON array of steps: [{ name, approvers: [...], rule }].</summary>
    public string StepsJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; }
}

public static class EmailStatus
{
    /// <summary>In-app only (email switched off, or the person opted out).</summary>
    public const string None = "None";
    public const string Pending = "Pending", Sent = "Sent", Failed = "Failed";
    /// <summary>Waiting for the person's daily summary email.</summary>
    public const string Held = "Held";
}

/// <summary>A message for one person: always shown in the app, and emailed when email is configured.</summary>
public class Notification
{
    public int Id { get; set; }
    public string UserEmail { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Site-relative link, e.g. /Requests/Detail/12. Empty when there is nothing to open.</summary>
    public string Link { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string EmailStatus { get; set; } = Data.EmailStatus.None;
    public int EmailAttempts { get; set; }
    public string? EmailError { get; set; }
    public DateTime? EmailNextAttemptAt { get; set; }
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

    /// <summary>For the provider-specific contexts below, which carry the migrations.</summary>
    protected AppDbContext(DbContextOptions options) : base(options) { }

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<FormDraft> FormDrafts => Set<FormDraft>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<DirectoryGroup> DirectoryGroups => Set<DirectoryGroup>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<RunComment> RunComments => Set<RunComment>();
    public DbSet<ClosedDay> ClosedDays => Set<ClosedDay>();
    public DbSet<Rate> Rates => Set<Rate>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<WorkflowCategory> Categories => Set<WorkflowCategory>();
    public DbSet<FormImage> FormImages => Set<FormImage>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<RunDefinition> RunDefinitions => Set<RunDefinition>();
    public DbSet<RunEventRow> RunEvents => Set<RunEventRow>();
    public DbSet<ApprovalTask> ApprovalTasks => Set<ApprovalTask>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<WebhookLog> WebhookLogs => Set<WebhookLog>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<ApprovalChainTemplate> ApprovalChains => Set<ApprovalChainTemplate>();
    public DbSet<Notification> Notifications => Set<Notification>();

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
            e.Property(x => x.DepartmentName).HasMaxLength(200);
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.SubmitterEmail);
            e.HasIndex(x => x.CreatedAt);                       // reports by date
            e.Property(x => x.LegacyDefinitionJson).HasColumnName("DefinitionJson");
            e.HasOne<RunDefinition>().WithMany().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Restrict);
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
            e.Property(x => x.Rule).HasMaxLength(10);
            e.Property(x => x.Position).HasMaxLength(40);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.DecidedBy);                       // "your recent decisions"
        });
        b.Entity<RunDefinition>(e =>
        {
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => x.Hash).IsUnique();
        });
        b.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Email);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Role).HasMaxLength(20);
            e.Property(x => x.EntraRole).HasMaxLength(20);
            e.Property(x => x.StudentScope).HasMaxLength(20).HasDefaultValue(StudentScopes.Location);
        });
        b.Entity<Department>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.FinalRule).HasMaxLength(10);
        });
        b.Entity<FormDraft>(e =>
        {
            e.Property(x => x.OwnerEmail).HasMaxLength(320);
            e.Property(x => x.Summary).HasMaxLength(300);
            e.HasIndex(x => new { x.OwnerEmail, x.WorkflowId });
        });
        b.Entity<Delegation>(e =>
        {
            e.Property(x => x.FromEmail).HasMaxLength(320);
            e.Property(x => x.ToEmail).HasMaxLength(320);
            e.HasIndex(x => x.FromEmail);
        });
        b.Entity<RunComment>(e =>
        {
            e.Property(x => x.AuthorEmail).HasMaxLength(320);
            e.Property(x => x.AuthorName).HasMaxLength(300);
            e.HasIndex(x => x.RunId);
        });
        b.Entity<WorkflowVersion>(e =>
        {
            e.Property(x => x.PublishedBy).HasMaxLength(320);
            e.HasIndex(x => new { x.WorkflowId, x.Version }).IsUnique();
        });
        b.Entity<WorkflowCategory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<FormImage>(e =>
        {
            e.HasKey(x => x.WorkflowId);
            e.Property(x => x.WorkflowId).ValueGeneratedNever();
            e.Property(x => x.ContentType).HasMaxLength(40);
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.UpdatedBy).HasMaxLength(320);
        });
        b.Entity<Team>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.UpdatedBy).HasMaxLength(320);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<ApprovalTask>().Property(x => x.Kind).HasMaxLength(20).HasDefaultValue(TaskKinds.Approval);
        b.Entity<Rate>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(60);
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.UpdatedBy).HasMaxLength(320);
            e.Property(x => x.Value).HasPrecision(18, 4);
            e.HasIndex(x => x.Key).IsUnique();
        });
        b.Entity<ClosedDay>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.Date).IsUnique();
        });
        b.Entity<DirectoryGroup>(e =>
        {
            e.Property(x => x.ObjectId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(20);
            e.HasIndex(x => x.ObjectId).IsUnique();
        });
        b.Entity<AuditEntry>(e =>
        {
            e.Property(x => x.ActorEmail).HasMaxLength(320);
            e.Property(x => x.ActorName).HasMaxLength(300);
            e.Property(x => x.Action).HasMaxLength(60);
            e.Property(x => x.Target).HasMaxLength(300);
            e.HasIndex(x => x.At);
        });
        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.PublicId).HasMaxLength(40);
            e.Property(x => x.StoredName).HasMaxLength(60);
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.ContentType).HasMaxLength(120);
            e.Property(x => x.OwnerEmail).HasMaxLength(320);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => new { x.OwnerEmail, x.RunId });
        });
        b.Entity<Location>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.FreshserviceId);
        });
        b.Entity<ApprovalChainTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.OwnerEmail).HasMaxLength(320);
            e.HasIndex(x => x.OwnerEmail);
        });
        b.Entity<Notification>(e =>
        {
            e.Property(x => x.UserEmail).HasMaxLength(320);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Link).HasMaxLength(300);
            e.Property(x => x.EmailStatus).HasMaxLength(20);
            e.Property(x => x.EmailError).HasMaxLength(1000);
            e.HasIndex(x => new { x.UserEmail, x.ReadAt });
            e.HasIndex(x => x.EmailStatus);
        });
        b.Entity<WebhookLog>(e =>
        {
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.Outcome).HasMaxLength(1000);
        });
    }
}

// Migrations are specific to a database engine, so each engine has its own context type and its own
// Migrations folder. The rest of the app only ever sees AppDbContext.

public sealed class SqliteAppDbContext : AppDbContext
{
    public SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options) : base(options) { }
}

public sealed class SqlServerAppDbContext : AppDbContext
{
    public SqlServerAppDbContext(DbContextOptions<SqlServerAppDbContext> options) : base(options) { }
}
