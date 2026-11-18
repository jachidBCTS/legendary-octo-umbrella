using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>Student search for the Student form field. Only for people an admin has given student access.</summary>
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/students")]
public sealed class StudentsController : ControllerBase
{
    private readonly IStudentRoster _roster;
    private readonly AccessService _access;
    private readonly ILogger<StudentsController> _log;

    private readonly StudentScopeService _scope;

    public StudentsController(IStudentRoster roster, AccessService access, ILogger<StudentsController> log, StudentScopeService scope)
    {
        _scope = scope;
        _roster = roster;
        _access = access;
        _log = log;
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

        if (!_roster.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The student roster is not connected yet. Ask an administrator." });

        var words = q.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        IReadOnlyList<SchoolFlow.Core.StudentInfo> rows;
        // Only the students this person may see: their location, their district, or all districts.
        try
        {
            var filter = await _scope.FilterForAsync(User.Email(), ct) ?? StudentFilter.Nobody;
            if (filter.IsEmpty)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "You are not on the staff list with a location, so no students can be shown. Ask an administrator." });
            rows = await _roster.SearchAsync(words, 15, filter, ct);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            _log.LogError(ex, "Student roster search failed");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The student roster could not be reached. Try again in a moment, or ask an administrator." });
        }
        return Ok(new
        {
            items = rows.Select(s => new
            {
                id = s.Id, name = s.Name, first = s.FirstName, last = s.LastName,
                school = s.School, grade = s.Grade, academy = s.Academy,
            }),
        });
    }
}
