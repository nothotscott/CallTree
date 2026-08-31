using CallTree.Domain.Messages;

namespace CallTree.Application.Messages;

/// <summary>
/// One row of the message log. A read model, deliberately separate from the <see cref="Message"/>
/// aggregate — same reasoning as <see cref="Calls.CallSummary"/>: the aggregate exists to enforce
/// transitions, and exposing it would freeze that surface into the HTTP contract.
/// </summary>
public sealed record MessageSummary
{
    public required Guid Id { get; init; }

    /// <summary>Business direction. Both kinds arrive as an inbound message to the DID.</summary>
    public required MessageSource Source { get; init; }

    public required MessageStatus Status { get; init; }

    /// <summary>E.164 number the message came from.</summary>
    public required string From { get; init; }

    /// <summary>E.164 number it was addressed to — our DID.</summary>
    public required string To { get; init; }

    /// <summary>The body as received, before any forwarding prefix was added.</summary>
    public required string Body { get; init; }

    /// <summary>Attachments on the received message. These are recorded but never forwarded.</summary>
    public required int MediaCount { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Why it was rejected or refused. Null on the happy path.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Where the message CallTree sent on went, when it sent one.</summary>
    public string? RelayRecipient { get; init; }

    /// <summary>What was sent on: the original body plus its prefix on the Inbound path.</summary>
    public string? RelayBody { get; init; }

    public DateTimeOffset? RelaySentAt { get; init; }

    /// <summary>The carrier's last word on the relayed message. Null when nothing was sent on.</summary>
    public RelayDelivery? RelayDelivery { get; init; }

    public string? RelayError { get; init; }

    public static MessageSummary FromMessage(Message message) => new()
    {
        Id = message.Id,
        Source = message.Source,
        Status = message.Status,
        From = message.From.Value,
        To = message.To.Value,
        Body = message.Body,
        MediaCount = message.MediaCount,
        ReceivedAt = message.ReceivedAt,
        CompletedAt = message.CompletedAt,
        FailureReason = message.FailureReason,
        RelayRecipient = message.Relay?.Recipient.Value,
        RelayBody = message.Relay?.Body,
        RelaySentAt = message.Relay?.SentAt,
        RelayDelivery = message.Relay?.Delivery,
        RelayError = message.Relay?.Error,
    };
}
