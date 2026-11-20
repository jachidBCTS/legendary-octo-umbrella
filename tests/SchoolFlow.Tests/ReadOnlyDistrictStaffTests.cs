using SchoolFlow.Core;
using SchoolFlow.Core.Forms;
using Xunit;

namespace SchoolFlow.Tests;

public class ReadOnlyDistrictStaffTests
{
    private static readonly Func<DateTime> Now = () => new DateTime(2026, 11, 19, 10, 0, 0);

    private static DateTime Day(object? value) => SchoolFlow.Core.Expressions.Values.TryToDate(value, out var d) ? d.Date : DateTime.MinValue;

    private static Dictionary<string, object?> Place(long id, string name) => new() { ["id"] = (decimal)id, ["name"] = name };

    [Fact]
    public void A_read_only_field_ignores_the_browser_and_keeps_the_submitted_value()
    {
        var form = new FormDefinition { Fields = { new FieldDef { Key = "request_date", Label = "Date of request", Type = FieldTypes.Date, Default = "TODAY()", ReadOnly = true } } };
        var typed = FormEvaluator.Evaluate(form, new Dictionary<string, object?> { ["request_date"] = "2020-01-01" }, now: Now);
        Assert.Equal(new DateTime(2026, 11, 19), Day(typed.Values["request_date"]));
        // Sent back and resubmitted later: the date it was first submitted with stands.
        var resubmitted = FormEvaluator.Evaluate(form, new Dictionary<string, object?> { ["request_date"] = "2020-01-01" }, now: () => new DateTime(2026, 12, 3),
            locked: new Dictionary<string, object?> { ["request_date"] = "2026-11-19" });
        Assert.Equal(new DateTime(2026, 11, 19), Day(resubmitted.Values["request_date"]));
        Assert.False(FormCopy.IsCopyable(form.Fields[0]));
    }

    [Fact]
    public void Read_only_without_a_default_is_flagged_by_the_checker()
    {
        var def = new WorkflowDefinition();
        def.Form.Fields.Add(new FieldDef { Key = "code", Label = "Code", Type = FieldTypes.Text, ReadOnly = true });
        Assert.Contains(SchoolFlow.Core.Workflows.DefinitionValidator.Validate(def), i => i.Message.Contains("read only but has no default"));
    }

    [Fact]
    public void A_location_can_be_limited_to_a_fixed_district_or_the_one_chosen_on_the_form()
    {
        var locations = new Dictionary<long, string> { [1] = "Academy", [2] = "N. A. Bleshman Regional Day School" };
        var places = new PlaceDistricts(new Dictionary<long, string> { [1] = "BCTS", [2] = "BCSS" },
            new Dictionary<string, string> { ["BCTS"] = "BCTS", ["Bergen County Technical Schools"] = "BCTS", ["BCSS"] = "BCSS", ["Bergen County Special Services"] = "BCSS" });
        var fixedForm = new FormDefinition { Fields = { new FieldDef { Key = "where", Label = "Where", Type = FieldTypes.Location, District = "BCTS" } } };
        Assert.True(FormEvaluator.Evaluate(fixedForm, new Dictionary<string, object?> { ["where"] = Place(1, "x") }, locations: locations, places: places).IsValid);
        var wrong = FormEvaluator.Evaluate(fixedForm, new Dictionary<string, object?> { ["where"] = Place(2, "x") }, locations: locations, places: places);
        Assert.Contains("not in the chosen district", string.Join(" ", wrong.Fields["where"].Errors));

        var chosenForm = new FormDefinition { Fields =
        {
            new FieldDef { Key = "where", Label = "Where", Type = FieldTypes.Location, DistrictField = "district" },   // before the question: still ordered after it
            new FieldDef { Key = "district", Label = "District", Type = FieldTypes.Radio, Options = new() { "Bergen County Technical Schools", "Bergen County Special Services" } },
        } };
        var ok = FormEvaluator.Evaluate(chosenForm, new Dictionary<string, object?> { ["district"] = "Bergen County Special Services", ["where"] = Place(2, "x") }, locations: locations, places: places);
        Assert.True(ok.IsValid, string.Join(" ", ok.Fields.SelectMany(f => f.Value.Errors)));
        var mismatch = FormEvaluator.Evaluate(chosenForm, new Dictionary<string, object?> { ["district"] = "Bergen County Technical Schools", ["where"] = Place(2, "x") }, locations: locations, places: places);
        Assert.False(mismatch.IsValid);
    }

    [Fact]
    public void A_staff_answer_comes_from_the_staff_list_only()
    {
        var form = new FormDefinition { Fields = { new FieldDef { Key = "supervisor", Label = "Supervisor", Type = FieldTypes.Staff, District = "BCTS" } } };
        var staff = new StaffDirectory(true, new Dictionary<string, StaffInfo>
        {
            ["pat@x.org"] = new("pat@x.org", "Pat Principal", "Principal", "ACADEMY", "Administration", "BCTS"),
            ["sam@x.org"] = new("sam@x.org", "Sam Smith", "Teacher", "BLESHMAN", "Science", "BCSS"),
        });
        var picked = FormEvaluator.Evaluate(form, new Dictionary<string, object?> { ["supervisor"] = new Dictionary<string, object?> { ["email"] = "PAT@x.org", ["name"] = "Someone Else" } }, staff: staff);
        var value = Assert.IsType<Dictionary<string, object?>>(picked.Values["supervisor"]);
        Assert.Equal("Pat Principal", value["name"]);   // the browser's name is ignored
        Assert.Equal("pat@x.org", value["email"]);
        Assert.False(FormEvaluator.Evaluate(form, new Dictionary<string, object?> { ["supervisor"] = "nobody@x.org" }, staff: staff).IsValid);
        var otherDistrict = FormEvaluator.Evaluate(form, new Dictionary<string, object?> { ["supervisor"] = "sam@x.org" }, staff: staff);
        Assert.Contains("does not work in the chosen district", string.Join(" ", otherDistrict.Fields["supervisor"].Errors));
        Assert.False(FormEvaluator.Evaluate(form, new Dictionary<string, object?> { ["supervisor"] = "pat@x.org" }, staff: new StaffDirectory(false, new Dictionary<string, StaffInfo>())).IsValid);
    }
}
