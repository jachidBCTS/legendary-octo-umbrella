using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Requests;

/// <summary>The requester edits a request that an approver sent back, on the version of the form it was submitted with.</summary>
public class ReviseModel : PageModel
{
    private readonly AppDbContext _db;
    public ReviseModel(AppDbContext db) => _db = db;

    public WorkflowRun Run { get; private set; } = null!;
    public ApprovalTask? Asked { get; private set; }
    public string FormJson { get; private set; } = "{}";
    public string ValuesJson { get; private set; } = "{}";
    /// <summary>How each answer reads now, so a field the requester changes can say "Was: …".</summary>
    public string PreviousJson { get; private set; } = "{}";
    public string? LetterheadUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var email = User.Email();
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (run is null || email.Length == 0 || run.SubmitterEmail != email) return NotFound();
        await HttpContext.RequestServices.GetRequiredService<DefinitionStore>().FillAsync(_db, run);
        if (run.Status != RunStatus.ChangesRequested) return Redirect($"/Requests/Detail/{id}");

        Run = run;
        LetterheadUrl = await Letterhead.UrlAsync(_db, run.WorkflowId);
        Asked = await _db.ApprovalTasks.AsNoTracking().Where(t => t.RunId == id && t.Status == ApprovalStatus.Returned)
            .OrderByDescending(t => t.Id).FirstOrDefaultAsync();
        var form = Json.ParseDefinition(run.DefinitionJson).Form;
        // The requester is not offered "save for later" or "copy from previous" here: this is an edit of one request.
        form.AllowCopy = null;
        FormJson = Json.Serialize(form).Replace("<", "\\u003c");
        // Only the form's own answers go to the browser, not the request's internal record.
        var context = Json.ToValues(run.ContextJson);
        var values = new Dictionary<string, object?>();
        foreach (var field in form.Fields.Where(f => !FieldTypes.IsLayout(f.Type) && f.Type != FieldTypes.Calculated))
            if (context.TryGetValue(field.Key, out var value)) values[field.Key] = value;
        ValuesJson = Json.Serialize(values).Replace("<", "\\u003c");
        var shown = new Dictionary<string, string>();
        // Requests keep only a student's number, so student answers (and anything worked out from them)
        // get no "Was: …", as on the request page.
        var studentKeys = SchoolFlow.Core.Forms.StudentData.KeysOf(Json.ParseDefinition(run.DefinitionJson));
        foreach (var field in form.Fields.Where(f => !FieldTypes.IsLayout(f.Type) && !studentKeys.Contains(f.Key)))
        {
            if (!context.TryGetValue(field.Key, out var value)) continue;
            shown[field.Key] = field.Type == FieldTypes.Table && value is List<object?> rows
                ? $"{rows.Count} row{(rows.Count == 1 ? "" : "s")}"
                : Ui.Display(value, field.Type, field.Format, field.Decimals);
        }
        PreviousJson = Json.Serialize(shown).Replace("<", "\\u003c");
        return Page();
    }
}
