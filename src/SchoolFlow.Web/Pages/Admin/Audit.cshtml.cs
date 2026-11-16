using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Pages.Admin;

public class AuditModel : PageModel
{
    private const int PageSize = 50;
    private readonly AppDbContext _db;
    public AuditModel(AppDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;
    public List<AuditEntry> Entries { get; private set; } = new();
    public bool HasMore { get; private set; }

    public string PageUrl(int page) => $"/Admin/Audit?p={page}{(string.IsNullOrEmpty(Q) ? "" : "&q=" + Uri.EscapeDataString(Q))}";

    /// <summary>Plain-language name for an action code.</summary>
    public static string Describe(string action) => action switch
    {
        "role.changed" => "Role changed",
        "group.registered" => "Group registered",
        "group.changed" => "Group changed",
        "group.removed" => "Group removed",
        "workflow.created" => "Workflow created",
        "workflow.published" => "Workflow published",
        "workflow.deleted" => "Workflow deleted",
        "retention.purged" => "Old requests deleted (retention)",
        "workflow.restored" => "Earlier version restored",
        "workflow.imported" => "Workflow imported",
        "workflow.switched-on" => "Workflow switched on",
        "workflow.switched-off" => "Workflow switched off",
        "workflow.owners" => "Workflow owners changed",
        "workflow.audience" => "Who can submit changed",
        "department.created" => "Department added",
        "department.approvers" => "Final approvers changed",
        "request.cancelled" => "Request cancelled",
        "request.retried" => "Request retried",
        "approval.override-approved" => "Approved on someone's behalf",
        "approval.override-rejected" => "Rejected on someone's behalf",
        "approval.override-returned" => "Sent back on someone's behalf",
        "calendar.closed" => "Closed days added",
        "calendar.reopened" => "Closed days removed",
        "requests.exported" => "Requests exported",
        _ => action,
    };

    public async Task OnGetAsync()
    {
        if (PageNumber < 1) PageNumber = 1;
        var query = _db.AuditEntries.AsNoTracking().AsQueryable();
        var q = (Q ?? "").Trim();
        if (q.Length > 100) q = q[..100];
        if (q.Length > 0)
            query = query.Where(e => e.ActorEmail.Contains(q) || e.ActorName.Contains(q) || e.Target.Contains(q) || e.Action.Contains(q) || e.Detail.Contains(q));
        var rows = await query.OrderByDescending(e => e.Id).Skip((PageNumber - 1) * PageSize).Take(PageSize + 1).ToListAsync();
        HasMore = rows.Count > PageSize;
        Entries = rows.Take(PageSize).ToList();
    }
}
