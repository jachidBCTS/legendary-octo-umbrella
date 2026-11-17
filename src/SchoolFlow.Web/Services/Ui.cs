using System.Globalization;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Workflows;

namespace SchoolFlow.Web.Services;

/// <summary>Small display helpers shared by the Razor pages.</summary>
public static class Ui
{
    /// <summary>Database timestamps are UTC; show them in the server's local time zone.</summary>
    public static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    public static string When(DateTime utc)
    {
        var local = Local(utc);
        var today = DateTime.Now.Date;
        if (local.Date == today) return "Today " + local.ToString("h:mm tt", Values.DisplayCulture);
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("h:mm tt", Values.DisplayCulture);
        return local.ToString(local.Year == today.Year ? "MMM d, h:mm tt" : "MMM d, yyyy", Values.DisplayCulture);
    }

    public static (string Label, string Css) Status(string status) => status switch
    {
        RunStatus.Running => ("In progress", "info"),
        RunStatus.WaitingApproval => ("Awaiting approval", "warn"),
        RunStatus.WaitingTask => ("Waiting on a task", "warn"),
        RunStatus.WaitingTicket => ("With help desk", "info"),
        RunStatus.ChangesRequested => ("Changes requested", "warn"),
        RunStatus.Completed => ("Completed", "ok"),
        RunStatus.Rejected => ("Not approved", "bad"),
        RunStatus.Failed => ("Needs attention", "bad"),
        RunStatus.Cancelled => ("Cancelled", "muted"),
        _ => (status, "muted"),
    };

    public static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '.', '@' }, StringSplitOptions.RemoveEmptyEntries);
        var letters = parts.Take(2).Select(p => char.ToUpperInvariant(p[0]));
        return string.Concat(letters);
    }

    /// <summary>Formats a stored field value for read-only display.</summary>
    public static string Display(object? value, string type, string? format, int? decimals)
    {
        if (Values.IsBlank(value)) return "—";
        if (ChainValue.IsChain(value)) return ChainValue.Describe(value);
        if (type == FieldTypes.Attachments && value is List<object?> files)
            return string.Join(", ", files.OfType<Dictionary<string, object?>>().Select(f => Values.ToText(f.GetValueOrDefault("name"))));
        if (type == FieldTypes.Student && value is Dictionary<string, object?> student)
        {
            string Part(string key) => Values.ToText(student.GetValueOrDefault(key)).Trim();
            var parts = new[] { Part("name"), "ID " + Part("id"), Part("school"), Part("grade").Length > 0 ? "Grade " + Part("grade") : "", Part("academy") };
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
        if (value is bool b) return b ? "Yes" : "No";
        if (value is decimal d)
        {
            if (type == FieldTypes.Currency || format == "currency") return d.ToString("C" + (decimals ?? 2), Values.DisplayCulture);
            return decimals is int places ? d.ToString("N" + places, Values.DisplayCulture) : Values.FormatNumber(d);
        }
        if (type == FieldTypes.Date && Values.TryToDate(value, out var date)) return date.ToString("MMM d, yyyy", Values.DisplayCulture);
        if (value is DateTime dt) return dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "MMM d, yyyy" : "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"));
        return Values.ToText(value);
    }
}
