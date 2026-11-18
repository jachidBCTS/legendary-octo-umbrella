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

    /// <summary>The picture for a workflow's tile, chosen from words in its name and category.</summary>
    public static string WorkflowIcon(string? name, string? category = null)
    {
        var text = ((name ?? "") + " " + (category ?? "")).ToLowerInvariant();
        // Whole words only ("hr" must not match "three"); a trailing * allows endings ("escalat*").
        bool Has(params string[] words) => words.Any(w => System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b" + System.Text.RegularExpressions.Regex.Escape(w.TrimEnd('*')) + (w.EndsWith('*') ? "" : @"s?\b")));
        if (Has("travel", "conference", "mileage")) return "plane";
        if (Has("field trip", "bus", "trip", "transport", "excursion")) return "bus";
        if (Has("purchase", "order", "buy", "procure*", "requisition", "supplies")) return "cart";
        if (Has("repair", "broken", "fix", "maintenance", "facilities")) return "wrench";
        if (Has("device", "laptop", "computer", "technology", "help desk", "it", "software", "escalat*", "account")) return "laptop";
        if (Has("leave", "hr", "personnel", "hiring", "job")) return "briefcase";
        return "form";
    }

    /// <summary>Formats a stored field value for read-only display.</summary>
    private static readonly System.Text.RegularExpressions.Regex DateOnlyText = new(@"^\d{4}-\d{2}-\d{2}$");

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
        // A calculated date (e.g. a due date) is stored as "yyyy-MM-dd"; show it the same way as a date field.
        if (type == FieldTypes.Calculated && value is string text && DateOnlyText.IsMatch(text) && Values.TryToDate(text, out var worked))
            return worked.ToString("MMM d, yyyy", Values.DisplayCulture);
        if (value is DateTime dt) return dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "MMM d, yyyy" : "MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"));
        return Values.ToText(value);
    }
}
