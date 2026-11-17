using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

/// <summary>Who may use student information, and the student roster itself.</summary>
public class StudentAccessModel : PageModel
{
    private const int MaxStudents = 100_000;
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public StudentAccessModel(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public List<AppUser> Allowed { get; private set; } = new();
    public int StudentCount { get; private set; }
    public List<(string School, int Count)> BySchool { get; private set; } = new();
    public List<string> FormsUsingStudents { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Allowed = await _db.Users.AsNoTracking().Where(u => u.StudentAccess).OrderBy(u => u.Name).ThenBy(u => u.Email).ToListAsync();
        StudentCount = await _db.Students.CountAsync();
        BySchool = (await _db.Students.GroupBy(s => s.School).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
            .OrderBy(x => x.Key).Select(x => (x.Key.Length == 0 ? "(no school)" : x.Key, x.Count)).ToList();
        FormsUsingStudents = (await _db.Workflows.AsNoTracking().Select(w => new { w.Name, w.DraftJson, w.PublishedJson }).ToListAsync())
            .Where(w => (w.DraftJson + w.PublishedJson).Contains("\"type\":\"student\"")).Select(w => w.Name).OrderBy(n => n).ToList();
    }

    private IActionResult Flash(string message, bool ok)
    {
        TempData["Flash"] = message;
        if (!ok) TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGrantAsync(string? email)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0 || email.Length > 320 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return Flash("Enter the person's email address.", false);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            // Someone who has not signed in yet: the flag is waiting for them when they do.
            user = new AppUser { Email = email, Name = "", Role = Roles.Staff };
            _db.Users.Add(user);
        }
        if (user.StudentAccess) return Flash($"{email} already has access to student information.", true);
        user.StudentAccess = true;
        _audit.Record(User, "students.access-granted", email);
        await _db.SaveChangesAsync();
        return Flash($"{email} can now look up students and see student details on requests.", true);
    }

    public async Task<IActionResult> OnPostRevokeAsync(string? email)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !user.StudentAccess) return RedirectToPage();
        user.StudentAccess = false;
        _audit.Record(User, "students.access-removed", email);
        await _db.SaveChangesAsync();
        return Flash($"{email} no longer has access to student information.", true);
    }

    /// <summary>
    /// Replaces the roster with the uploaded file: students in the file are added or updated, students
    /// not in it are removed. Only the six agreed columns are read; anything else in the file is ignored.
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0) return Flash("Choose a CSV file to import.", false);
        if (file.Length > 20_000_000) return Flash("That file is too large (20 MB at most).", false);
        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync();
        var rows = StudentCsv.Parse(text);
        if (rows.Count < 2) return Flash("The file needs a heading row and at least one student.", false);

        var columns = StudentCsv.MapColumns(rows[0]);
        var missing = new[] { ("id", "Student ID"), ("first", "First name"), ("last", "Last name") }.Where(c => !columns.ContainsKey(c.Item1)).Select(c => c.Item2).ToList();
        if (missing.Count > 0) return Flash($"The heading row has no column for: {string.Join(", ", missing)}. Expected headings: Student ID, First Name, Last Name, School, Grade, Academy.", false);
        if (rows.Count - 1 > MaxStudents) return Flash($"The file has more than {MaxStudents:N0} students.", false);

        string Cell(List<string> row, string column, int max)
        {
            if (!columns.TryGetValue(column, out var index) || index >= row.Count) return "";
            var value = row[index].Trim();
            return value.Length > max ? value[..max] : value;
        }
        var incoming = new Dictionary<string, Student>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var row in rows.Skip(1))
        {
            var id = Cell(row, "id", 40);
            if (id.Length == 0) { skipped++; continue; }
            incoming[id] = new Student
            {
                StudentId = id, FirstName = Cell(row, "first", 100), LastName = Cell(row, "last", 100),
                School = Cell(row, "school", 200), Grade = Cell(row, "grade", 20), Academy = Cell(row, "academy", 200),
            };
        }
        if (incoming.Count == 0) return Flash("No rows had a Student ID, so nothing was imported.", false);

        var existing = await _db.Students.ToDictionaryAsync(s => s.StudentId, StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0, removed = 0;
        foreach (var (id, student) in incoming)
        {
            if (!existing.TryGetValue(id, out var current)) { _db.Students.Add(student); added++; continue; }
            if (current.FirstName == student.FirstName && current.LastName == student.LastName && current.School == student.School
                && current.Grade == student.Grade && current.Academy == student.Academy) continue;
            (current.FirstName, current.LastName, current.School, current.Grade, current.Academy) =
                (student.FirstName, student.LastName, student.School, student.Grade, student.Academy);
            updated++;
        }
        foreach (var (id, current) in existing)
            if (!incoming.ContainsKey(id)) { _db.Students.Remove(current); removed++; }

        // Counts only: the audit log never holds student details.
        _audit.Record(User, "students.imported", "Student roster", $"{incoming.Count} student(s) in the file: {added} added, {updated} updated, {removed} removed.");
        await _db.SaveChangesAsync();
        return Flash($"Roster updated: {added} added, {updated} updated, {removed} removed." + (skipped > 0 ? $" {skipped} row(s) without a Student ID were skipped." : ""), true);
    }
}

/// <summary>Reads a CSV file (quoted cells, commas and line breaks inside quotes, doubled quotes).</summary>
public static class StudentCsv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(v => v.Trim().Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Any(v => v.Trim().Length > 0)) rows.Add(row);
        return rows;
    }

    /// <summary>Finds the six columns by their headings, whatever order they are in and however they are spaced.</summary>
    public static Dictionary<string, int> MapColumns(List<string> header)
    {
        var names = new Dictionary<string, string[]>
        {
            ["id"] = new[] { "studentid", "id", "studentnumber", "studentno", "localid" },
            ["first"] = new[] { "firstname", "first", "givenname" },
            ["last"] = new[] { "lastname", "last", "surname", "familyname" },
            ["school"] = new[] { "school", "schoolname", "building" },
            ["grade"] = new[] { "grade", "gradelevel" },
            ["academy"] = new[] { "academy", "program" },
        };
        var map = new Dictionary<string, int>();
        for (var i = 0; i < header.Count; i++)
        {
            var heading = new string(header[i].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            foreach (var (column, accepted) in names)
                if (accepted.Contains(heading)) map.TryAdd(column, i);
        }
        return map;
    }
}
