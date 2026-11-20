using SchoolFlow.Web.Services;
using Xunit;

namespace SchoolFlow.Tests;

public class StartingLocationsTests
{
    [Fact]
    public void Uses_student_side_names_and_turns_number_codes_into_the_staff_name()
    {
        var list = StartingLocations.From(new (string, string?)[]
        {
            ("BLESHMAN", "N. A. Bleshman Regional Day School"),
            ("WASH EMERSON JR/SR HS", "Washington Emerson"),
            ("WASH EMERSON VILLANO ELEM SCHOOL", "Washington Emerson"),   // two staff places, one name
            ("ACADEMY", "20"),
            ("APPLIED TECHNOLOGY HS (BCC)", "300"),
            ("PARAMUS TEC", "75"),
            ("PARAMUS VOC", "75"),
            ("GATEWAY", "Venture Program"),
            ("VENTURE", "Venture Program"),
        });
        Assert.Equal(new[]
        {
            "Academy", "Applied Technology HS (BCC)", "N. A. Bleshman Regional Day School", "Paramus TEC", "Paramus VOC",
            "Venture Program", "Washington Emerson",
        }, list);
    }
}
