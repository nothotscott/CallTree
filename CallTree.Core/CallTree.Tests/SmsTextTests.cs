using CallTree.Domain.Messages;
using CallTree.Domain.ValueObjects;
using CallTree.Messaging;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// The length arithmetic behind forwarding. The provider refuses a body over
/// <see cref="SmsText.MaxLength"/> outright, so a prefix that is not budgeted for does not truncate the
/// message - it loses it, silently, on the one path this whole feature exists to serve.
/// </summary>
public class SmsTextTests
{
    [Fact]
    public void Leaves_text_that_already_fits_alone()
    {
        Assert.Equal("hello", SmsText.Truncate("hello"));
        Assert.Equal("", SmsText.Truncate(""));
    }

    [Fact]
    public void Cuts_to_the_limit_and_says_so()
    {
        var truncated = SmsText.Truncate(new string('x', SmsText.MaxLength + 1));

        Assert.Equal(SmsText.MaxLength, truncated.Length);
        Assert.EndsWith(SmsText.Ellipsis, truncated);
    }

    [Fact]
    public void Hard_cuts_when_there_is_no_room_even_for_the_marker()
    {
        // A fragment beats a message that is nothing but "[truncated]".
        var truncated = SmsText.Truncate("abcdef", 3);

        Assert.Equal("abc", truncated);
    }

    [Fact]
    public void A_forwarded_message_carries_the_sender_and_fits_the_limit()
    {
        var forwarded = ForwardText.ForInbound(PhoneNumber.Parse("+13055551234"), "hello there", mediaCount: 0);

        Assert.StartsWith("(305) 555-1234:", forwarded);
        Assert.EndsWith("hello there", forwarded);
    }

    [Fact]
    public void A_maximum_length_message_still_fits_once_the_prefix_is_added()
    {
        // The case that matters: the body is already at the provider's limit before anything is added.
        var forwarded = ForwardText.ForInbound(
            PhoneNumber.Parse("+13055551234"), new string('x', SmsText.MaxLength), mediaCount: 0);

        Assert.True(forwarded.Length <= SmsText.MaxLength, $"forward was {forwarded.Length} characters");
        Assert.StartsWith("(305) 555-1234:", forwarded);
    }

    [Theory]
    [InlineData(1, "[1 attachment, not forwarded]")]
    [InlineData(3, "[3 attachments, not forwarded]")]
    public void Attachments_are_reported_rather_than_forwarded(int mediaCount, string expected)
    {
        var forwarded = ForwardText.ForInbound(PhoneNumber.Parse("+13055551234"), "look", mediaCount);

        Assert.EndsWith(expected, forwarded);
    }

    [Fact]
    public void The_attachment_note_survives_a_maximum_length_body()
    {
        // The note is the only trace of a picture the operator will not see, so it must not be the part
        // that gets cut off the end.
        var forwarded = ForwardText.ForInbound(
            PhoneNumber.Parse("+13055551234"), new string('x', SmsText.MaxLength), mediaCount: 2);

        Assert.True(forwarded.Length <= SmsText.MaxLength, $"forward was {forwarded.Length} characters");
        Assert.EndsWith("[2 attachments, not forwarded]", forwarded);
    }

    [Fact]
    public void A_failure_notice_names_itself_so_it_is_not_read_as_a_reply()
    {
        var notice = ForwardText.ForFailure("the provider refused the message");

        Assert.StartsWith("CallTree", notice);
        Assert.Contains("the provider refused the message", notice);
    }
}
