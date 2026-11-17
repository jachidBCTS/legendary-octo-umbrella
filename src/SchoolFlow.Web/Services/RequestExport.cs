using System.Text;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Turns requests into a CSV file a spreadsheet can open: fixed columns, then one column per form answer.</summary>
public static class RequestExport
{
    private static readonly string[] Fixed = { "Request #", "Form", "Status", "Submitted by", "Email", "Department", "Ticket", "Submitted", "Last updated" };

    /// <param name="includeStudents">False for someone without student access: student answers are left out of the file.</param>
    public static string ToCsv(IReadOnlyList<WorkflowRun> runs, bool includeStudents = true)
    {
        // Requests on different forms (or versions of a form) have different questions; a column is a question's label.
        var forms = new Dictionary<string, FormDefinition>();
        FormDefinition FormOf(WorkflowRun run)
        {
            if (!forms.TryGetValue(run.DefinitionJson, out var form)) forms[run.DefinitionJson] = form = Json.ParseDefinition(run.DefinitionJson).Form;
            return form;
        }
        static bool HasAnswer(FieldDef f) => !FieldTypes.IsLayout(f.Type) && !string.IsNullOrWhiteSpace(f.Key);
        static string Header(FieldDef f) => string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label.Trim();

        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
            foreach (var field in FormOf(run).Fields.Where(HasAnswer))
                if (seen.Add(Header(field))) columns.Add(Header(field));

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", Fixed.Concat(columns).Select(Cell)));
        foreach (var run in runs)
        {
            var (status, _) = Ui.Status(run.Status);
            var cells = new List<string>
            {
                run.Id.ToString(), run.WorkflowName, status, run.SubmitterName, run.SubmitterEmail, run.DepartmentName,
                run.TicketId?.ToString() ?? "", Ui.Local(run.CreatedAt).ToString("yyyy-MM-dd HH:mm"), Ui.Local(run.UpdatedAt).ToString("yyyy-MM-dd HH:mm"),
            };
            var context = Json.ToValues(run.ContextJson);
            var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in FormOf(run).Fields.Where(HasAnswer))
                answers.TryAdd(Header(field), field.Type == FieldTypes.Student && !includeStudents
                    ? (Values.IsBlank(context.GetValueOrDefault(field.Key)) ? "" : "(restricted)")
                    : Answer(field, context.GetValueOrDefault(field.Key)));
            cells.AddRange(columns.Select(c => answers.GetValueOrDefault(c, "")));
            csv.AppendLine(string.Join(",", cells.Select(Cell)));
        }
        return csv.ToString();
    }

    private static string Answer(FieldDef field, object? value)
    {
        if (Values.IsBlank(value)) return "";
        if (field.Type == FieldTypes.Table && value is List<object?> rows)
        {
            var columns = field.Columns ?? new List<ColumnDef>();
            return string.Join("; ", rows.OfType<Dictionary<string, object?>>().Select(r =>
                string.Join(" | ", columns.Select(c => Plain(r.GetValueOrDefault(c.Key), c.Type)))));
        }
        return Plain(value, field.Type);
    }

    /// <summary>Numbers and dates as plain values a spreadsheet can calculate with, not display text like "$1,200.00".</summary>
    private static string Plain(object? value, string type) => value switch
    {
        null => "",
        bool b => b ? "Yes" : "No",
        decimal d => Values.FormatNumber(d),
        _ when type == FieldTypes.Date && Values.TryToDate(value, out var date) => date.ToString("yyyy-MM-dd"),
        _ => Ui.Display(value, type, null, null),
    };

    /// <summary>
    /// Quotes a cell for CSV. Text beginning with =, +, - or @ would be run as a formula by Excel, so it
    /// is given a leading apostrophe (plain numbers such as -5 are left alone).
    /// </summary>
    public static string Cell(string? text)
    {
        text ??= "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]) && !decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
