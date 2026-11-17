using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>Student search for the Student form field. Only for people an admin has given student access.</summary>
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/students")]
public sealed class StudentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AccessService _access;

    public StudentsController(AppDbContext db, AccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        if (!await _access.CanViewStudentsAsync(User, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have access to student information. Ask an administrator." });
        q = (q ?? "").Trim();
        if (q.Length > 60) q = q[..60];
        // No browsing the whole roster: a search needs at least two characters and returns a short list.
        if (q.Length < 2) return Ok(new { items = Array.Empty<object>() });

        var words = q.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        var query = _db.Students.AsNoTracking().AsQueryable();
        foreach (var word in words)
            query = query.Where(s => s.LastName.StartsWith(word) || s.FirstName.StartsWith(word) || s.StudentId.StartsWith(word));
        var rows = await query.OrderBy(s => s.LastName).ThenBy(s => s.FirstName).Take(15).ToListAsync(ct);
        return Ok(new
        {
            items = rows.Select(s => new
            {
                id = s.StudentId, name = s.ToInfo().Name, first = s.FirstName, last = s.LastName,
                school = s.School, grade = s.Grade, academy = s.Academy,
            }),
        });
    }
}
