using System.Text.Json;
using System.Text.Json.Serialization;
using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core;

// The JSON document a builder edits: a form, a flow graph and a trigger.

public sealed class WorkflowDefinition
{
    public TriggerDefinition Trigger { get; set; } = new();
    public FormDefinition Form { get; set; } = new();
    public FlowDefinition Flow { get; set; } = new();

    /// <summary>
    /// Whether the workflow depends on the Freshservice help desk: it starts from a ticket, has a
    /// help desk step, or looks something up there. Only administrators build these.
    /// </summary>
    public bool UsesHelpDesk() =>
        Trigger.Type == TriggerTypes.Ticket
        || Flow.Nodes.Any(n => NodeTypes.IsHelpDesk(n.Type))
        || Form.Fields.Any(f => f.Type == FieldTypes.Lookup);
}

public sealed class TriggerDefinition
{
    /// <summary>"form" (a person submits the form) or "ticket" (a Freshservice webhook event).</summary>
    public string Type { get; set; } = TriggerTypes.Form;

    /// <summary>For ticket triggers: formula that must be true for the event to start a run, e.g. ticket.priority >= 3.</summary>
    public string? Condition { get; set; }
}

public static class TriggerTypes
{
    public const string Form = "form";
    public const string Ticket = "ticket";
}

public sealed class FormDefinition
{
    public List<FieldDef> Fields { get; set; } = new();
    public string? SubmitLabel { get; set; }
    public string? Intro { get; set; }
    /// <summary>
    /// Whether people may start from a previous request: empty (off), "own" (their own earlier
    /// requests) or "everyone" (anyone's earlier requests of this form).
    /// </summary>
    public string? AllowCopy { get; set; }
    /// <summary>How the form is drawn: blank for the standard look, "paper" for a printed-form look.</summary>
    public string? Style { get; set; }
    /// <summary>First day the form accepts requests ("yyyy-MM-dd"); blank means it is already open.</summary>
    public string? OpensOn { get; set; }
    /// <summary>Last day the form accepts requests ("yyyy-MM-dd", inclusive); blank means no closing date.</summary>
    public string? ClosesOn { get; set; }

    public static bool TryParseDay(string? text, out DateTime day) =>
        DateTime.TryParseExact((text ?? "").Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day);

    /// <summary>Why the form is not taking requests on <paramref name="today"/>, or null when it is open.</summary>
    public string? ClosedMessage(DateTime today)
    {
        if (TryParseDay(OpensOn, out var opens) && today.Date < opens) return $"This form opens on {opens:dddd, MMMM d, yyyy}.";
        if (TryParseDay(ClosesOn, out var closes) && today.Date > closes) return $"This form closed on {closes:dddd, MMMM d, yyyy}.";
        return null;
    }
}

public static class CopyModes
{
    public const string Own = "own", Everyone = "everyone";
    public static bool IsOn(string? mode) => mode is Own or Everyone;
    public static bool IsValid(string? mode) => string.IsNullOrEmpty(mode) || IsOn(mode);
}

public static class FieldTypes
{
    public const string Text = "text", TextArea = "textarea", Email = "email", Number = "number", Currency = "currency",
        Date = "date", Select = "select", Radio = "radio", MultiSelect = "multiselect", Checkbox = "checkbox",
        Lookup = "lookup", Calculated = "calculated", Table = "table", Section = "section",
        Department = "department", ApprovalChain = "approval_chain", Location = "location", Attachments = "attachments",
        Time = "time", Phone = "phone", Signature = "signature", Page = "page", Worksheet = "worksheet", Student = "student",
        /// <summary>GSA per diem rates for a destination ZIP code and travel dates, looked up by the server.</summary>
        PerDiem = "perdiem",
        /// <summary>Ends the current row: the next field starts on a new line (optionally under a thin line).</summary>
        Break = "break";

    public static readonly string[] All =
    {
        Text, TextArea, Email, Number, Currency, Date, Select, Radio, MultiSelect, Checkbox, Lookup, Calculated, Table, Section,
        Department, ApprovalChain, Location, Attachments, Time, Phone, Signature, Page, Worksheet, Student, PerDiem, Break,
    };

    /// <summary>Field types a Task step can ask its assignee for.</summary>
    public static readonly string[] InTask =
    {
        Text, TextArea, Email, Number, Currency, Date, Time, Phone, Select, Radio, MultiSelect, Checkbox, Calculated, Attachments, Section,
    };

    /// <summary>Field types that can be placed inside a worksheet row.</summary>
    public static readonly string[] InWorksheet = { Text, Number, Currency, Calculated, Date, Time, Select };

    /// <summary>Headings, page and line breaks, worksheets: they arrange the form and never hold an answer.</summary>
    public static bool IsLayout(string type) => type is Section or Page or Worksheet or Break;

    public static readonly string[] Column = { Text, Number, Currency, Date, Select, Checkbox, Calculated };

    public static bool HasOptions(string type) => type is Select or Radio or MultiSelect;
    public static bool IsNumeric(string type) => type is Number or Currency;
}

public sealed class FieldDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = FieldTypes.Text;
    public string? Help { get; set; }
    public string? Placeholder { get; set; }
    /// <summary>"half" places the field in a two-column row; anything else is full width.</summary>
    public string? Width { get; set; }

    public bool Required { get; set; }
    /// <summary>Formula; when it is true the field becomes required.</summary>
    public string? RequiredWhen { get; set; }
    /// <summary>Formula; when present the field only shows (and only keeps a value) while it is true.</summary>
    public string? VisibleWhen { get; set; }
    /// <summary>Formula for calculated fields.</summary>
    public string? Formula { get; set; }
    /// <summary>Formula evaluated once when the form opens, e.g. TODAY() or submitter.name.</summary>
    public string? Default { get; set; }

    public List<string>? Options { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int? Decimals { get; set; }
    /// <summary>For calculated fields: "currency", "number" or empty for automatic.</summary>
    public string? Format { get; set; }
    /// <summary>For lookup fields: departments, groups, locations, requesters or my_assets.</summary>
    public string? Source { get; set; }

    public List<RuleDef>? Rules { get; set; }
    /// <summary>When true, this field is never copied (or shown) from a previous request.</summary>
    public bool? NoCopy { get; set; }

    public List<ColumnDef>? Columns { get; set; }
    public int? MinRows { get; set; }
    public int? MaxRows { get; set; }
    /// <summary>For attachment fields: how many files may be added (default 5).</summary>
    public int? MaxFiles { get; set; }
    /// <summary>Worksheet only: the rows of the table. Other fields of the form are drawn inside them.</summary>
    public List<WorksheetRow>? Rows { get; set; }
    /// <summary>Per diem fields only: which fields of the form hold the destination ZIP code and the travel dates.</summary>
    public PerDiemSettings? PerDiem { get; set; }
}

/// <summary>The fields a per diem field reads: the destination ZIP code, and the first and last day of travel.</summary>
public sealed class PerDiemSettings
{
    public string? Zip { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
}

/// <summary>An uploaded file as the form sees it. The file itself is kept by the web app.</summary>
/// <summary>
/// One row of a worksheet: a number, an item name, a description that can hold other fields of the
/// form as <c>{field_key}</c>, and the field shown in the cost column. A worksheet only arranges
/// fields; their values, formulas and checks are those of the fields themselves.
/// </summary>
public sealed class WorksheetRow
{
    public string? Number { get; set; }
    public string? Item { get; set; }
    public string? Description { get; set; }
    public string? Cost { get; set; }

    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}");

    /// <summary>Field keys this row draws, in order: those in the description, then the cost field.</summary>
    public IEnumerable<string> FieldKeys()
    {
        foreach (System.Text.RegularExpressions.Match m in Placeholder.Matches(Description ?? "")) yield return m.Groups[1].Value;
        if (!string.IsNullOrWhiteSpace(Cost)) yield return Cost.Trim();
    }
}

/// <summary>The only facts about a student the system holds.</summary>
public sealed record StudentInfo(string Id, string FirstName, string LastName, string School, string Grade, string Academy)
{
    /// <summary>The student's district. Used only to decide who may see the student; never shown or stored.</summary>
    public string District { get; init; } = "";

    public string Name => string.Join(", ", new[] { LastName, FirstName }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// What the person filling in a form may use from the student roster: whether they are allowed to at
/// all, and the roster entries for the student IDs their answers mention (looked up by the server).
/// </summary>
public sealed record StudentDirectory(bool Allowed, IReadOnlyDictionary<string, StudentInfo> Known);

public sealed record AttachmentInfo(string Id, string Name, long Size);

public sealed class RuleDef
{
    /// <summary>Formula that must be true for the value to be accepted.</summary>
    public string Check { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ColumnDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = FieldTypes.Text;
    public bool Required { get; set; }
    public List<string>? Options { get; set; }
    public string? Formula { get; set; }
    public int? Decimals { get; set; }
    public string? Format { get; set; }
}

public static class LookupSources
{
    public const string Departments = "departments", Groups = "groups", Locations = "locations",
        Requesters = "requesters", MyAssets = "my_assets", Agents = "agents";

    public static readonly string[] FormSources = { Departments, Locations, Requesters, MyAssets, Groups };
}

public sealed class FlowDefinition
{
    public List<NodeDef> Nodes { get; set; } = new();
    public List<EdgeDef> Edges { get; set; } = new();
}

public static class NodeTypes
{
    public const string Start = "start", Condition = "condition", Approval = "approval", SetVariable = "set_variable",
        CreateTicket = "fs_create_ticket", ServiceRequest = "fs_service_request", UpdateTicket = "fs_update_ticket",
        AddNote = "fs_add_note", Lookup = "fs_lookup", WaitTicket = "wait_ticket", End = "end",
        ApprovalChain = "approval_chain", Notify = "notify", Task = "task";

    public static readonly string[] All =
    {
        Start, Condition, Approval, ApprovalChain, Task, Notify, SetVariable, CreateTicket, ServiceRequest, UpdateTicket, AddNote, Lookup, WaitTicket, End,
    };

    /// <summary>Steps that talk to the Freshservice help desk.</summary>
    public static bool IsHelpDesk(string type) => type is CreateTicket or ServiceRequest or UpdateTicket or AddNote or Lookup or WaitTicket;

    /// <summary>Output ports for each node type.</summary>
    public static string[] Ports(string type) => type switch
    {
        Condition => new[] { "true", "false" },
        Approval or ApprovalChain => new[] { "approved", "rejected" },
        End => Array.Empty<string>(),
        _ => new[] { "next" },
    };
}

public sealed class NodeDef
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Label { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Type-specific settings. Most accept {{formula}} placeholders.</summary>
    public Dictionary<string, string> Config { get; set; } = new();
    /// <summary>Freshservice custom field name → value template.</summary>
    public Dictionary<string, string> CustomFields { get; set; } = new();
    /// <summary>Task steps only: what the assignee fills in. Their answers join the request under these keys.</summary>
    public List<FieldDef>? Fields { get; set; }

    public string Get(string key) => Config.TryGetValue(key, out var v) ? v ?? "" : "";
}

public sealed class EdgeDef
{
    public string From { get; set; } = "";
    public string Port { get; set; } = "next";
    public string To { get; set; } = "";
}

/// <summary>JSON helpers shared by the web app and the engine.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new DateOnlyStringConverter() },
    };

    public static WorkflowDefinition ParseDefinition(string json)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options) ?? new WorkflowDefinition();
        // Guard against explicit nulls in hand-edited JSON.
        def.Trigger ??= new TriggerDefinition();
        def.Form ??= new FormDefinition();
        def.Form.Fields ??= new List<FieldDef>();
        def.Form.Fields.RemoveAll(f => f is null);
        def.Flow ??= new FlowDefinition();
        def.Flow.Nodes ??= new List<NodeDef>();
        def.Flow.Edges ??= new List<EdgeDef>();
        def.Flow.Nodes.RemoveAll(n => n is null);
        def.Flow.Edges.RemoveAll(e => e is null);
        foreach (var n in def.Flow.Nodes)
        {
            n.Config ??= new Dictionary<string, string>();
            n.CustomFields ??= new Dictionary<string, string>();
        }
        return def;
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Converts parsed JSON into the formula value model (decimal, string, bool, list, record).</summary>
    public static object? ToValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String: return element.GetString();
            case JsonValueKind.Number:
                return element.TryGetDecimal(out var d) ? d : (object?)null;
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Array: return element.EnumerateArray().Select(ToValue).ToList();
            case JsonValueKind.Object:
                var dict = Values.NewDict();
                foreach (var prop in element.EnumerateObject()) dict[prop.Name] = ToValue(prop.Value);
                return dict;
            default: return null;
        }
    }

    public static Dictionary<string, object?> ToValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Values.NewDict();
        using var doc = JsonDocument.Parse(json);
        return ToValue(doc.RootElement) as Dictionary<string, object?> ?? Values.NewDict();
    }

    public static Dictionary<string, object?> ToValues(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? (Dictionary<string, object?>)ToValue(element)!
            : Values.NewDict();

    /// <summary>Writes whole-day dates as yyyy-MM-dd so they round-trip cleanly through storage and the browser.</summary>
    private sealed class DateOnlyStringConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Values.TryToDate(reader.GetString(), out var d) ? d : default;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Values.ToText(value));
    }
}
