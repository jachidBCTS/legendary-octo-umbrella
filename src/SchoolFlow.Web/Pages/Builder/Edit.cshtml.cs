using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Pages.Builder;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IFreshserviceClient _freshservice;

    public EditModel(AppDbContext db, IFreshserviceClient freshservice)
    {
        _db = db;
        _freshservice = freshservice;
    }

    public int Id { get; private set; }
    public bool IsLive => _freshservice.IsLive;
    public string CatalogJson { get; private set; } = "[]";

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        if (workflow is null || !SchoolFlow.Web.Services.AccessService.CanManage(User, workflow)) return NotFound();
        Id = id;
        CatalogJson = Json.Serialize(Functions.Catalog).Replace("<", "\\u003c");
        return Page();
    }
}
