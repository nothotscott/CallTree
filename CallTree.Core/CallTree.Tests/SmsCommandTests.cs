using CallTree.Domain.Messages;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// Reading "{RECIPIENT-NUMBER} Body of text" off a text message.
/// </summary>
/// <remarks>
/// The reason this is worth testing hard: a parser that reads the number wrong does not fail, it sends
/// the operator's message to a stranger. The awkward cases are all variations on one ambiguity — a
/// number may contain spaces, and a body may start with digits — so the fixtures below are mostly
/// pairs that differ only in which of those is happening.
/// </remarks>
public class SmsCommandTests
{
    [Theory]
    // Plain, and the punctuation people actually type.
    [InlineData("3055551234 hello", "+13055551234", "hello")]
    [InlineData("(305) 555-1234 hello", "+13055551234", "hello")]
    [InlineData("305-555-1234 hello", "+13055551234", "hello")]
    [InlineData("305.555.1234 hello", "+13055551234", "hello")]
    [InlineData("1-305-555-1234 hello", "+13055551234", "hello")]
    [InlineData("+13055551234 hello", "+13055551234", "hello")]
    [InlineData("+1 (305) 555-1234 hello", "+13055551234", "hello")]
    // A number split across tokens: only the end of the run can close it.
    [InlineData("+1 305 555 1234 hello", "+13055551234", "hello")]
    [InlineData("305 555 1234 hello", "+13055551234", "hello")]
    // International, where there is no canonical length to stop at.
    [InlineData("+447911123456 cheers", "+447911123456", "cheers")]
    [InlineData("+44 7911 123456 cheers", "+447911123456", "cheers")]
    // Leading whitespace, and a body that keeps its own spacing.
    [InlineData("  3055551234   two  spaces", "+13055551234", "two  spaces")]
    [InlineData("3055551234 multi\nline", "+13055551234", "multi\nline")]
    public void Reads_the_recipient_and_the_body(string input, string expectedNumber, string expectedBody)
    {
        Assert.True(SmsCommand.TryParse(input, out var command, out var error));
        Assert.Equal("", error);
        Assert.Equal(expectedNumber, command.Recipient.Value);
        Assert.Equal(expectedBody, command.Body);
    }

    [Theory]
    // The case a "stop at the first thing that parses" scan gets wrong: ten digits is a complete NANP
    // number, so the number ends there whatever follows - even another number.
    [InlineData("305-555-1234 42 is the answer", "+13055551234", "42 is the answer")]
    [InlineData("3055551234 911 emergency", "+13055551234", "911 emergency")]
    [InlineData("+13055551234 2024 was a year", "+13055551234", "2024 was a year")]
    // Eleven digits beginning with 1 is equally complete.
    [InlineData("13055551234 42 is the answer", "+13055551234", "42 is the answer")]
    public void A_complete_nanp_number_ends_the_recipient_even_when_the_body_starts_with_digits(
        string input,
        string expectedNumber,
        string expectedBody)
    {
        Assert.True(SmsCommand.TryParse(input, out var command, out _));
        Assert.Equal(expectedNumber, command.Recipient.Value);
        Assert.Equal(expectedBody, command.Body);
    }

    [Fact]
    public void Does_not_cut_an_e164_number_short_at_a_prefix_that_would_also_parse()
    {
        // "+1305555123" is eleven characters PhoneNumber.TryParse accepts quite happily. Testing only at
        // whitespace boundaries is what stops the recipient being truncated to it.
        Assert.True(SmsCommand.TryParse("+13055551234 hello", out var command, out _));
        Assert.Equal("+13055551234", command.Recipient.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_an_empty_message(string? input)
    {
        Assert.False(SmsCommand.TryParse(input, out var command, out var error));
        Assert.Null(command);
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("hello there")]
    [InlineData("call me back")]
    // Too short to be a number, so the run ends without anything parseable.
    [InlineData("123 hello")]
    // No space between the number and the body, so the token is not number-shaped.
    [InlineData("3055551234hello")]
    public void Rejects_a_message_with_no_recipient_on_the_front(string input)
    {
        Assert.False(SmsCommand.TryParse(input, out var command, out var error));
        Assert.Null(command);
        Assert.Contains("No recipient number", error);
        Assert.Contains(SmsCommand.Usage, error);
    }

    [Theory]
    [InlineData("3055551234")]
    [InlineData("(305) 555-1234   ")]
    public void Rejects_a_number_with_nothing_to_send(string input)
    {
        Assert.False(SmsCommand.TryParse(input, out var command, out var error));
        Assert.Null(command);
        // Named, so the operator can see the number was understood and only the body was missing.
        Assert.Contains("(305) 555-1234", error);
    }

    [Fact]
    public void A_message_of_nothing_but_numbers_still_stops_at_ten_digits()
    {
        // Not an ambiguity the parser resolves by giving up: ten digits is a complete number, so it
        // stops there and the rest is the body, exactly as it would with any other body.
        Assert.True(SmsCommand.TryParse("12 34 56 78 90 12 34 56 78", out var command, out _));

        Assert.Equal("+11234567890", command.Recipient.Value);
        Assert.Equal("12 34 56 78", command.Body);
    }

    [Fact]
    public void Gives_up_rather_than_swallowing_a_message_looking_for_the_last_digit()
    {
        // A "+" number has no canonical length to stop at, so only the end of the run closes it - and
        // without a token cap a message of short numeric words would be consumed whole looking for one.
        Assert.False(SmsCommand.TryParse("+1 2 3 4 5 6 7 8 9 hello", out var command, out var error));

        Assert.Null(command);
        Assert.Contains("No recipient number", error);
    }

    [Fact]
    public void Truncates_a_body_that_would_be_refused_by_the_provider()
    {
        var input = "3055551234 " + new string('x', SmsText.MaxLength + 500);

        Assert.True(SmsCommand.TryParse(input, out var command, out _));
        Assert.Equal(SmsText.MaxLength, command.Body.Length);
        Assert.EndsWith(SmsText.Ellipsis, command.Body);
    }
}
