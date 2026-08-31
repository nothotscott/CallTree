using CallTree.Domain.Messages;
using CallTree.Domain.ValueObjects;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// Legal transitions of the <see cref="Message"/> aggregate, in the same spirit as
/// <see cref="CallStateMachineTests"/>: the aggregate is what stops a webhook arriving out of order
/// from writing a state the log then has to be read around.
/// </summary>
public class MessageStateMachineTests
{
    private static readonly PhoneNumber Stranger = PhoneNumber.Parse("+13055551234");
    private static readonly PhoneNumber Did = PhoneNumber.Parse("+13055559999");
    private static readonly PhoneNumber Cell = PhoneNumber.Parse("+13055550000");
    private static readonly DateTimeOffset When = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static Message Received(MessageSource source = MessageSource.Inbound, string body = "hello") =>
        Message.Receive(source, Stranger, Did, body, mediaCount: 0, providerMessageId: "prov-1", When);

    [Fact]
    public void A_received_message_starts_with_no_relay()
    {
        var message = Received();

        Assert.Equal(MessageStatus.Received, message.Status);
        Assert.Null(message.Relay);
        Assert.False(message.IsTerminal);
        Assert.Contains(message.DomainEvents, e => e is MessageReceived);
    }

    [Fact]
    public void Relaying_then_accepting_ends_in_relayed()
    {
        var message = Received();

        message.BeginRelay(Cell, "(305) 555-1234:\nhello", When);
        Assert.Equal(MessageStatus.Relaying, message.Status);

        message.RelayAccepted("prov-2", When.AddSeconds(1));

        Assert.Equal(MessageStatus.Relayed, message.Status);
        Assert.True(message.IsTerminal);
        Assert.Equal("prov-2", message.Relay!.ProviderMessageId);
        Assert.Equal(When.AddSeconds(1), message.Relay.SentAt);
        // Accepted by the provider is not delivered by the carrier - that is a separate, later question.
        Assert.Equal(RelayDelivery.Queued, message.Relay.Delivery);
    }

    [Fact]
    public void A_refused_send_ends_in_failed_and_keeps_the_reason()
    {
        var message = Received();
        message.BeginRelay(Cell, "hello", When);

        message.RelayFailed("the from number is not on a messaging profile", When);

        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Equal("the from number is not on a messaging profile", message.FailureReason);
        Assert.Equal(RelayDelivery.Failed, message.Relay!.Delivery);
    }

    [Fact]
    public void An_unreadable_command_is_rejected_rather_than_failed()
    {
        // The distinction is worth keeping: rejected means nothing was ever sent, failed means the
        // provider turned us down. Only one of them is the operator's typo.
        var message = Received(MessageSource.Outbound, "call me back");

        message.Reject("No recipient number was found at the start of the message.", When);

        Assert.Equal(MessageStatus.Rejected, message.Status);
        Assert.Null(message.Relay);
        Assert.True(message.IsTerminal);
    }

    [Fact]
    public void A_receive_only_line_records_rather_than_failing()
    {
        // The whole point of the state: a line with no API key is not a broken line, and a message it
        // could never have forwarded is not a failed message. Carrying a FailureReason here would put
        // "Messaging:ApiKey is not set" against every text the operator ever receives.
        var message = Received();

        message.RecordOnly(When.AddSeconds(1));

        Assert.Equal(MessageStatus.Recorded, message.Status);
        Assert.Null(message.Relay);
        Assert.Null(message.FailureReason);
        Assert.Equal(When.AddSeconds(1), message.CompletedAt);
        Assert.True(message.IsTerminal);
    }

    [Fact]
    public void A_recorded_message_is_finished_with()
    {
        // Terminal means terminal: a key configured later does not retroactively forward an old text.
        var message = Received();
        message.RecordOnly(When);

        Assert.Throws<InvalidOperationException>(() => message.BeginRelay(Cell, "hello", When));
        Assert.Throws<InvalidOperationException>(() => message.Reject("too late", When));
    }

    [Fact]
    public void A_message_cannot_be_relayed_twice()
    {
        var message = Received();
        message.BeginRelay(Cell, "hello", When);
        message.RelayAccepted("prov-2", When);

        Assert.Throws<InvalidOperationException>(() => message.BeginRelay(Cell, "again", When));
    }

    [Fact]
    public void A_rejected_message_cannot_then_be_relayed()
    {
        var message = Received();
        message.Reject("nowhere to forward to", When);

        Assert.Throws<InvalidOperationException>(() => message.BeginRelay(Cell, "hello", When));
    }

    [Fact]
    public void A_delivery_receipt_touches_only_the_relay()
    {
        // The same rule as Call.FinalizeRecording, and for the same reason: this arrives minutes later
        // in a scope of its own, and leaving the parent's columns alone is what stops a late receipt
        // overwriting the message's status with a stale one.
        var message = Received();
        message.BeginRelay(Cell, "hello", When);
        message.RelayAccepted("prov-2", When);

        message.RecordDelivery(RelayDelivery.Delivered, error: null, When.AddMinutes(2));

        Assert.Equal(MessageStatus.Relayed, message.Status);
        Assert.Equal(RelayDelivery.Delivered, message.Relay!.Delivery);
        Assert.Equal(When.AddMinutes(2), message.Relay.DeliveryChangedAt);
    }

    [Fact]
    public void A_late_receipt_does_not_walk_a_verdict_backwards()
    {
        // Receipts arrive out of order and more than once. "Delivered" then "sent" must not read as a
        // message that stopped being delivered.
        var message = Received();
        message.BeginRelay(Cell, "hello", When);
        message.RelayAccepted("prov-2", When);
        message.RecordDelivery(RelayDelivery.Delivered, null, When.AddMinutes(1));

        message.RecordDelivery(RelayDelivery.Sent, null, When.AddMinutes(2));

        Assert.Equal(RelayDelivery.Delivered, message.Relay!.Delivery);
        Assert.Equal(When.AddMinutes(1), message.Relay.DeliveryChangedAt);
    }

    [Fact]
    public void A_receipt_for_a_message_with_no_relay_is_ignored_rather_than_thrown()
    {
        // Reachable when a rejected message's provider id somehow comes back around; losing the request
        // over it would be worse than doing nothing.
        var message = Received();
        message.Reject("nowhere to forward to", When);

        message.RecordDelivery(RelayDelivery.Delivered, null, When);

        Assert.Null(message.Relay);
    }

    [Fact]
    public void An_over_long_body_is_truncated_rather_than_refused()
    {
        var message = Message.Receive(
            MessageSource.Inbound,
            Stranger,
            Did,
            new string('x', SmsText.MaxLength + 100),
            mediaCount: 0,
            providerMessageId: "prov-1",
            When);

        Assert.Equal(SmsText.MaxLength, message.Body.Length);
    }
}
