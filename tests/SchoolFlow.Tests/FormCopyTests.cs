using SchoolFlow.Core;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Workflows;

namespace SchoolFlow.Tests;

public class FormCopyTests
{
    private static FormDefinition TripForm() => new()
    {
        AllowCopy = CopyModes.Everyone,
        Fields =
        {
            new FieldDef { Key = "heading", Label = "Trip", Type = "section" },
            new FieldDef { Key = "destination", Label = "Destination", Type = "text" },
            new FieldDef { Key = "trip_date", Label = "Date", Type = "date", NoCopy = true },
            new FieldDef { Key = "students", Label = "Students", Type = "number" },
            new FieldDef { Key = "cost_each", Label = "Cost each", Type = "currency" },
            new FieldDef { Key = "total", Label = "Total", Type = "calculated", Formula = "students * cost_each" },
            new FieldDef { Key = "notes", Label = "Notes", Type = "textarea" },
            new FieldDef { Key = "medical", Label = "Medical notes", Type = "textarea", NoCopy = true },
            new FieldDef { Key = "dept", Label = "Department", Type = "lookup", Source = "departments" },
            new FieldDef { Key = "stops", Label = "Stops", Type = "table", Columns = new() { new ColumnDef { Key = "place", Label = "Place", Type = "text" } } },
        },
    };

    private static Dictionary<string, object?> LastYear() => Json.ToValues("""
    {
      "Destination": "Science Museum", "trip_date": "2025-10-03", "students": 42, "cost_each": 12.5, "total": 525,
      "notes": "Bring lunch", "medical": "Two students carry inhalers", "dept": { "id": 105, "name": "Science" },
      "stops": [ { "place": "Planetarium" } ],
      "submitter": { "email": "a@school.org", "name": "A" },
      "approval": { "by": "Boss", "comment": "private remark" },
      "ticket": { "id": 90001 },
      "removed_field": "from an older version of the form"
    }
    """);

    [Fact]
    public void Extract_CopiesOnlyCurrentCopyableFields()
    {
        var values = FormCopy.Extract(TripForm(), LastYear());
        Assert.Equal(new[] { "cost_each", "dept", "destination", "notes", "stops", "students" }, values.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("Science Museum", values["destination"]); // key matched case-insensitively
    }

    [Fact]
    public void Extract_NeverLeaksExcludedFieldsOrRunData()
    {
        var json = Json.Serialize(FormCopy.Extract(TripForm(), LastYear()));
        foreach (var secret in new[] { "inhalers", "2025-10-03", "private remark", "a@school.org", "90001", "older version", "525" })
            Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public void Summary_And_SearchText_UseOnlyCopyableAnswers()
    {
        Assert.Equal("Science Museum · 42 · 12.5", FormCopy.Summary(TripForm(), LastYear()));
        var search = FormCopy.SearchText(TripForm(), LastYear());
        Assert.Contains("Planetarium", search);
        Assert.Contains("Science", search);
        Assert.DoesNotContain("inhalers", search);
        Assert.DoesNotContain("private remark", search);
    }

    [Fact]
    public void CopiedValues_PrefillAndRecalculate_WithDefaultsForExcludedFields()
    {
        var form = TripForm();
        form.Fields.Single(f => f.Key == "trip_date").Default = "DATE(2026, 10, 2)";
        var result = FormEvaluator.Evaluate(form, FormCopy.Extract(form, LastYear()), applyDefaults: true);
        Assert.Equal("Science Museum", result.Values["destination"]);
        Assert.Equal(525m, result.Values["total"]);                       // recalculated, not copied
        Assert.Equal(new DateTime(2026, 10, 2), result.Values["trip_date"]); // excluded field takes its default
        Assert.Null(result.Values["medical"]);
        Assert.Equal("Science", ((Dictionary<string, object?>)result.Values["dept"]!)["name"]);
        Assert.Single((List<object?>)result.Values["stops"]!);
    }

    [Fact]
    public void Setting_RoundTrips_AndIsValidated()
    {
        var def = Json.ParseDefinition(Json.Serialize(new WorkflowDefinition { Form = TripForm() }));
        Assert.Equal("everyone", def.Form.AllowCopy);
        Assert.True(def.Form.Fields.Single(f => f.Key == "medical").NoCopy);
        Assert.DoesNotContain("noCopy", Json.Serialize(new FieldDef { Key = "a" })); // unset stays out of stored JSON

        def.Form.AllowCopy = "sometimes";
        Assert.Contains(DefinitionValidator.Validate(def), i => i.IsError && i.Message.Contains("previous request"));
        Assert.False(CopyModes.IsOn(null));
        Assert.False(CopyModes.IsOn("sometimes"));
    }
}
