using CallTree.Domain.Primitives;
using CallTree.Domain.ValueObjects;

namespace CallTree.Domain.Messages;

/// <summary>
/// Aggregate root for one text message that arrived at the DID, and for what CallTree did with it.
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors <see cref="Calls.Call"/> deliberately: both kinds of traffic arrive *inbound* and
/// are then classified by the remote number, and in both cases what CallTree did about it (a
/// <see cref="Relay"/>, a <see cref="Calls.Recording"/>) is a fact hanging off the aggregate rather than
/// a status on it.
/// </para>
/// <para>
/// Only one relay per message, for the same reason a call has only one recording: every path here sends
/// at most one message on. The failure notice texted back to the operator when a command cannot be read
/// is not a relay — nothing was relayed — and is deliberately not recorded as one.
/// </para>
/// </remarks>
public class Message : AggregateRoot
{
    public Guid Id { get; private set; }

    public MessageSource Source { get; private set; }

    public MessageStatus Status { get; private set; }

    /// <summary>Who sent it.</summary>
    public PhoneNumber From { get; private set; } = null!;

    /// <summary>Which of our numbers it was addressed to. Always the configured DID today.</summary>
    public PhoneNumber To { get; private set; } = null!;

    public string Body { get; private set; } = "";

    /// <summary>
    /// Attachments on the received message. Recorded but never forwarded — see the note in
    /// <c>SmsRelayService</c> — so a non-zero count is the only trace the operator gets that there was
    /// a picture they have not seen.
    /// </summary>
    public int MediaCount { get; private set; }

    /// <summary>
    /// The provider's id for the received message. The webhook is retried on any non-2xx and on a slow
    /// response, so this is what stops one text being forwarded twice; it is uniquely indexed.
    /// </summary>
    public string ProviderMessageId { get; private set; } = "";

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Why it was rejected or failed, in words fit to show the operator.</summary>
    public string? FailureReason { get; private set; }

    public Relay? Relay { get; private set; }

    public bool IsTerminal => Status
        is MessageStatus.Recorded
        or MessageStatus.Relayed
        or MessageStatus.Rejected
        or MessageStatus.Failed;

    private Message()
    {
    }

    public static Message Receive(
        MessageSource source,
        PhoneNumber from,
        PhoneNumber to,
        string body,
        int mediaCount,
        string providerMessageId,
        DateTimeOffset when)
    {
        var message = new Message
        {
            Id = Guid.NewGuid(),
            Source = source,
            Status = MessageStatus.Received,
            From = from,
            To = to,
            // Truncated rather than rejected: the column has a length and an over-long body is the
            // provider's problem to have avoided, not a reason to lose the message entirely.
            Body = SmsText.Truncate(body ?? ""),
            MediaCount = Math.Max(0, mediaCount),
            ProviderMessageId = providerMessageId,
            ReceivedAt = when,
        };

        message.Raise(new MessageReceived(message.Id, source));
        return message;
    }

    /// <summary>A message is being sent on: the forward to the mobile, or what a command asked for.</summary>
    public Relay BeginRelay(PhoneNumber recipient, string body, DateTimeOffset when)
    {
        EnsureStatus(MessageStatus.Received);
        Status = MessageStatus.Relaying;
        Relay = new Relay(recipient, SmsText.Truncate(body), when);
        return Relay;
    }

    /// <summary>The provider accepted the message we sent on. Delivery is a separate, later question.</summary>
    public void RelayAccepted(string providerMessageId, DateTimeOffset when)
    {
        EnsureStatus(MessageStatus.Relaying);
        RequireRelay().MarkSent(providerMessageId, when);
        Status = MessageStatus.Relayed;
        CompletedAt = when;
        Raise(new MessageRelayed(Id, Source));
    }

    /// <summary>The provider refused the message we sent on.</summary>
    public void RelayFailed(string reason, DateTimeOffset when)
    {
        EnsureStatus(MessageStatus.Relaying);
        RequireRelay().MarkRefused(reason, when);
        Status = MessageStatus.Failed;
        CompletedAt = when;
        FailureReason = reason;
        Raise(new MessageFailed(Id, reason));
    }

    /// <summary>
    /// Kept, with no relay attempted, because this line cannot send: receive-only operation.
    /// </summary>
    /// <remarks>
    /// Distinct from both <see cref="Reject"/> and <see cref="RelayFailed"/>, and the distinction is the
    /// point. Nothing went wrong and nothing was refused — there was never going to be an outgoing
    /// message, so recording one as failed would be a lie the operator has to learn to ignore. No
    /// <see cref="FailureReason"/> for the same reason: why this line does not send is a property of the
    /// configuration, not a fact about this message, and belongs stated once rather than on every row.
    /// </remarks>
    public void RecordOnly(DateTimeOffset when)
    {
        EnsureStatus(MessageStatus.Received);
        Status = MessageStatus.Recorded;
        CompletedAt = when;
    }

    /// <summary>
    /// Nothing was sent on, and nothing will be: an unreadable send command, or no mobile configured to
    /// forward to. Distinct from <see cref="RelayFailed"/>, which means the provider turned us down.
    /// </summary>
    public void Reject(string reason, DateTimeOffset when)
    {
        EnsureStatus(MessageStatus.Received);
        Status = MessageStatus.Rejected;
        CompletedAt = when;
        FailureReason = reason;
        Raise(new MessageFailed(Id, reason));
    }

    /// <summary>
    /// Records a carrier receipt for the relayed message.
    /// </summary>
    /// <remarks>
    /// Touches only the <see cref="Relay"/>, and never the message's own status — the same rule as
    /// <c>Call.FinalizeRecording</c>, and for the same reason. This runs from a webhook that can arrive
    /// minutes later, in a scope of its own; leaving the parent's columns unchanged means EF emits no
    /// UPDATE for it and a late receipt cannot overwrite anything with a stale value.
    /// </remarks>
    public void RecordDelivery(RelayDelivery delivery, string? error, DateTimeOffset when) =>
        Relay?.RecordDelivery(delivery, error, when);

    private Relay RequireRelay() =>
        Relay ?? throw new InvalidOperationException($"Message {Id} has no relay.");

    private void EnsureStatus(MessageStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Message {Id} is {Status}, expected {expected}.");
        }
    }
}
