using SchoolFlow.Core;
using Xunit;

namespace SchoolFlow.Tests;

public class PersonNameTests
{
    [Theory]
    [InlineData("Abas_Ekrem", "Ekrem Abas")]
    [InlineData("Hidalgo_Jacob", "Jacob Hidalgo")]
    [InlineData("Van Der Berg_Anna Maria", "Anna Maria Van Der Berg")]
    [InlineData("Smith, Pat", "Pat Smith")]
    [InlineData("Pat Principal", "Pat Principal")]
    [InlineData("  Taylor Teacher ", "Taylor Teacher")]
    [InlineData("_Odd", "Odd")]
    [InlineData("Odd_", "Odd")]
    [InlineData("someone@bergen.org", "someone@bergen.org")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Tidy_shows_first_name_first(string? raw, string expected) => Assert.Equal(expected, PersonName.Tidy(raw));

    [Fact]
    public void Given_and_surname_win_over_the_display_name() =>
        Assert.Equal("Ekrem Abas", PersonName.From("Ekrem", "Abas", "Abas_Ekrem"));

    [Fact]
    public void Display_name_is_tidied_when_parts_are_missing() =>
        Assert.Equal("Ekrem Abas", PersonName.From(null, "Abas", "Abas_Ekrem"));
}
