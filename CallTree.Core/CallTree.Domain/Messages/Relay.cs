using CallTree.Domain.ValueObjects;

namespace CallTree.Domain.Messages;

/// <summary>
/// The message CallTree sent on as a result of one it received: the forward to the operator's mobile,
/// or the text a send command asked for.
/// </summary>
/// <remarks>
/// A fact about what was done, in the same spirit as <see cref="Calls.Recording"/> — which is why
/// delivery lives here rather than as a <see cref="MessageStatus"/>. Carrier verdicts arrive minutes
/// later on a webhook of their own, long after the request that created this has finished, and folding
/// them into the parent's status would mean a late receipt could overwrite a newer truth.
/// </remarks>
public class Relay
{
    public Guid Id { get; private set; }

    /// <summary>Who it was sent to: the operator's mobile on the Inbound path, the requested number otherwise.</summary>
    public PhoneNumber Recipient { get; private set; } = null!;

    /// <summary>What was actually sent, which on the Inbound path is the original body plus its prefix.</summary>
    public string Body { get; private set; } = "";

    /// <summary>The provider's id for the sent message. Null until the send is accepted.</summary>
    public string? ProviderMessageId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the provider accepted it. Null while in flight, and after a refusal.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    public RelayDelivery Delivery { get; private set; } = RelayDelivery.Queued;

    /// <summary>When the carrier last said something. Not necessarily when it arrived.</summary>
    public DateTimeOffset? DeliveryChangedAt { get; private set; }

    /// <summary>The provider's own words for a refusal or a failed delivery, kept verbatim.</summary>
    public string? Error { get; private set; }

    private Relay()
    {
    }

    internal Relay(PhoneNumber recipient, string body, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        Recipient = recipient;
        Body = body;
        CreatedAt = createdAt;
    }

    internal void MarkSent(string providerMessageId, DateTimeOffset when)
    {
        ProviderMessageId = providerMessageId;
        SentAt = when;
        Delivery = RelayDelivery.Queued;
    }

    internal void MarkRefused(string error, DateTimeOffset when)
    {
        Delivery = RelayDelivery.Failed;
        DeliveryChangedAt = when;
        Error = error;
    }

    /// <summary>
    /// Records a carrier receipt. Delivery receipts can arrive out of order and more than once, so a
    /// verdict already reached is never walked back to an earlier one — only a later receipt for a
    /// message that has not finished yet moves it.
    /// </summary>
    internal void RecordDelivery(RelayDelivery delivery, string? error, DateTimeOffset when)
    {
        if (Delivery is RelayDelivery.Delivered or RelayDelivery.Failed)
        {
            return;
        }

        Delivery = delivery;
        DeliveryChangedAt = when;
        if (error is { Length: > 0 })
        {
            Error = error;
        }
    }
}
