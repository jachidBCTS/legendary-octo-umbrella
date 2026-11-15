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
        Department = "department", ApprovalChain = "approval_chain", Location = "location";

    public static readonly string[] All =
    {
        Text, TextArea, Email, Number, Currency, Date, Select, Radio, MultiSelect, Checkbox, Lookup, Calculated, Table, Section,
        Department, ApprovalChain, Location,
    };

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
}

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
        ApprovalChain = "approval_chain", Notify = "notify";

    public static readonly string[] All =
    {
        Start, Condition, Approval, ApprovalChain, Notify, SetVariable, CreateTicket, ServiceRequest, UpdateTicket, AddNote, Lookup, WaitTicket, End,
    };

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
