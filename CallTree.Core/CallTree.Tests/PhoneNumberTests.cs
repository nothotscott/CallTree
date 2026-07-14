using CallTree.Domain.ValueObjects;
using Xunit;

namespace CallTree.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("(305) 555-1234", "+13055551234")]
    [InlineData("305-555-1234", "+13055551234")]
    [InlineData("3055551234", "+13055551234")]
    [InlineData("13055551234", "+13055551234")]
    [InlineData("+13055551234", "+13055551234")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    public void Parse_normalizes_to_e164(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumber.Parse(input).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("anonymous")]
    [InlineData("123")]
    [InlineData("0123456789012345")]
    public void TryParse_rejects_invalid_input(string? input)
    {
        Assert.False(PhoneNumber.TryParse(input, out _));
    }

    [Fact]
    public void Equality_is_by_normalized_value()
    {
        Assert.Equal(PhoneNumber.Parse("(305) 555-1234"), PhoneNumber.Parse("+1 305 555 1234"));
    }
}
