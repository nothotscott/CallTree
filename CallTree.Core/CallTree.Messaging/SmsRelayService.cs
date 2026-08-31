using CallTree.Application.Configuration;
using CallTree.Application.Messages;
using CallTree.Domain.Messages;
using CallTree.Domain.ValueObjects;
using CallTree.Messaging.Configuration;
using CallTree.Messaging.Telnyx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTree.Messaging;

/// <summary>What the webhook endpoint should answer.</summary>
public enum WebhookOutcome
{
    /// <summary>Acted on. 200.</summary>
    Handled,

    /// <summary>
    /// Understood and deliberately not acted on — a duplicate, an event we do not care about, or a
    /// message for a number that is not ours. Also 200: the provider must not retry any of these.
    /// </summary>
    Ignored,

    /// <summary>Not a webhook we can read. 400, and the provider stops retrying it.</summary>
    Malformed,
}

/// <summary>
/// The SMS side of the line: decides what an arriving message means and sends whatever it implies.
/// </summary>
/// <remarks>
/// <para>
/// The split here matches the call side exactly. <see cref="MessageLifecycleService"/> drives the
/// aggregate and nothing else; the policy — who is allowed to text in, what a message means, what gets
/// sent back — lives out here, the way <c>TelephonyBackgroundService</c> owns the call policy while
/// <c>CallLifecycleService</c> owns the transitions.
/// </para>
/// <para>
/// Two classifications, by the sender's number, mirroring <c>CallSource</c>:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Inbound</b> — anyone else. The text is forwarded to <c>Telephony:MyCellNumber</c> with the
/// sender's number on the front, so the operator can read who it is from and reply with a send command.
/// </item>
/// <item>
/// <b>Outbound</b> — from <c>Telephony:MyCellNumber</c>. The body is a
/// <c>{RECIPIENT-NUMBER} Body of text</c> command; the number is parsed off the front and the rest is
/// sent from the DID, so the far end sees the DID and never the operator's real mobile — the messaging
/// counterpart of the outbound proxy dial.
/// </item>
/// </list>
/// <para>
/// <b>Attachments are never forwarded.</b> MMS media is counted and noted in the forwarded text, and the
/// picture itself is left where it is. Forwarding it would mean handing the provider a list of media
/// URLs to re-fetch, at MMS rates, with a second set of failure modes — deliberately out of scope. A
/// non-zero MediaCount on a message is the operator's cue that there is something to go and look at.
/// </para>
/// <para>
/// <b>The failure notice is not a relay.</b> When a send command cannot be carried out the operator is
/// texted back, but that text is not recorded as the message's <see cref="Relay"/> — nothing was
/// relayed. It costs a message and has no delivery tracking, which is why only failures get one; see
/// <see cref="MessagingOptions.NotifyOnFailure"/>.
/// </para>
/// </remarks>
public sealed class SmsRelayService(
    MessageLifecycleService messages,
    TelnyxClient telnyx,
    IOptionsMonitor<LineOptions> lineOptions,
    IOptionsMonitor<MessagingOptions> messagingOptions,
    ILogger<SmsRelayService> logger)
{
    public async Task<WebhookOutcome> HandleAsync(
        TelnyxWebhook webhook,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var payload = webhook.Data?.Payload;
        if (payload is null)
        {
            logger.LogWarning("A messaging webhook arrived with no payload.");
            return WebhookOutcome.Malformed;
        }

        var when = webhook.Data?.OccurredAt ?? now;
        var eventType = webhook.Data?.EventType;

        switch (eventType)
        {
            case "message.received":
                return await HandleReceivedAsync(payload, when, cancellationToken);

            // Both carry a carrier verdict about something we sent; finalized is the terminal one.
            case "message.sent":
            case "message.finalized":
                return await HandleReceiptAsync(payload, when, cancellationToken);

            default:
                logger.LogDebug("Ignoring messaging webhook of type {EventType}.", eventType ?? "(none)");
                return WebhookOutcome.Ignored;
        }
    }

    private async Task<WebhookOutcome> HandleReceivedAsync(
        TelnyxMessagePayload payload,
        DateTimeOffset when,
        CancellationToken cancellationToken)
    {
        if (payload.Id is not { Length: > 0 } providerMessageId)
        {
            logger.LogWarning(
                "An inbound message webhook arrived with no message id; there is no way to deduplicate it.");
            return WebhookOutcome.Malformed;
        }

        if (!PhoneNumber.TryParse(payload.From?.PhoneNumber, out var from))
        {
            logger.LogWarning("Inbound message {ProviderMessageId} has no usable sender number.", providerMessageId);
            return WebhookOutcome.Malformed;
        }

        if (!PhoneNumber.TryParse(payload.To?.FirstOrDefault()?.PhoneNumber, out var to))
        {
            logger.LogWarning(
                "Inbound message {ProviderMessageId} has no usable destination number.", providerMessageId);
            return WebhookOutcome.Malformed;
        }

        var line = lineOptions.CurrentValue;

        // The same filter the SIP side applies to INVITEs, for the same reason: this endpoint is public
        // and unauthenticated beyond its signature, and a message addressed to a number we do not own
        // has no business making this instance send anything.
        if (line.Did is { } did && to != did)
        {
            logger.LogWarning(
                "Rejecting message {ProviderMessageId} addressed to {To}, which is not Telephony:DidNumber.",
                providerMessageId,
                to.Value);
            return WebhookOutcome.Ignored;
        }

        // Only reachable if the DID and the mobile are configured to the same number, which would make
        // every forward arrive back here as a fresh command. Cheap to check, unbounded if missed.
        if (from == to)
        {
            logger.LogWarning(
                "Ignoring message {ProviderMessageId} from {From} to itself - Telephony:DidNumber and "
                + "Telephony:MyCellNumber must not be the same number.",
                providerMessageId,
                from.Value);
            return WebhookOutcome.Ignored;
        }

        // The provider retries on any non-2xx and on a slow response. Without this, one text could be
        // forwarded several times - which costs money and buzzes the operator again. The unique index on
        // ProviderMessageId is the backstop for two retries genuinely in flight at once: that write
        // fails, the request 500s, and the next retry finds the row here and stops.
        if (await messages.AlreadyReceivedAsync(providerMessageId, cancellationToken))
        {
            logger.LogInformation(
                "Message {ProviderMessageId} has already been handled; ignoring the retry.", providerMessageId);
            return WebhookOutcome.Ignored;
        }

        var source = line.MyCell is { } cell && from == cell ? MessageSource.Outbound : MessageSource.Inbound;
        var body = payload.Text ?? "";
        var mediaCount = payload.Media?.Count ?? 0;

        var messageId = await messages.ReceiveAsync(
            source, from, to, body, mediaCount, providerMessageId, when, cancellationToken);

        logger.LogInformation(
            "Message {MessageId} received from {From} ({Source}), {Length} chars, {MediaCount} attachments.",
            messageId,
            from.Value,
            source,
            body.Length,
            mediaCount);

        // Receive-only: the webhook works, the API key does not exist, so nothing can be sent. Stop here
        // rather than walking the message through a relay that can only be refused - see ReceiveOnly.
        if (messagingOptions.CurrentValue.ApiKey.Length == 0)
        {
            return await ReceiveOnlyAsync(messageId, source, when, cancellationToken);
        }

        return source == MessageSource.Outbound
            ? await SendOnCommandAsync(messageId, body, when, cancellationToken)
            : await ForwardToMobileAsync(messageId, from, body, mediaCount, when, cancellationToken);
    }

    /// <summary>
    /// Closes out a message on a line that has no <c>Messaging:ApiKey</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Running with the webhook configured and the API key left blank is a deliberate mode, not a
    /// half-finished setup: it makes the DID a receive-only number whose texts - verification codes,
    /// mostly - are read from the messages page instead of being forwarded. It is also the only mode
    /// available on a number the carrier has not approved for sending, which on US long codes means one
    /// without 10DLC registration.
    /// </para>
    /// <para>
    /// So an inbound message is <b>recorded</b>, not failed. Letting it fall through to
    /// <see cref="ForwardToMobileAsync"/> would work - <see cref="TelnyxClient"/> refuses politely with
    /// "Messaging:ApiKey is not set" - but it would end every single message at
    /// <see cref="MessageStatus.Failed"/>, and a log that is entirely red is a log nobody reads.
    /// </para>
    /// <para>
    /// A send command still gets a <see cref="MessageStatus.Rejected"/>, because the operator asked for
    /// something this line cannot do and the row has to say so. There is no failure notice: sending one
    /// would need the API key that is missing.
    /// </para>
    /// </remarks>
    private async Task<WebhookOutcome> ReceiveOnlyAsync(
        Guid messageId,
        MessageSource source,
        DateTimeOffset when,
        CancellationToken cancellationToken)
    {
        if (source == MessageSource.Outbound)
        {
            const string reason =
                "Messaging:ApiKey is not set, so this line can receive texts but cannot send them.";
            logger.LogWarning("Message {MessageId}: {Reason}", messageId, reason);
            await messages.RejectAsync(messageId, reason, when, cancellationToken);
            return WebhookOutcome.Handled;
        }

        logger.LogInformation(
            "Message {MessageId} recorded without forwarding: no Messaging:ApiKey, so this line is receive-only.",
            messageId);
        await messages.RecordOnlyAsync(messageId, when, cancellationToken);
        return WebhookOutcome.Handled;
    }

    /// <summary>A stranger texted the DID: pass it to the operator's mobile.</summary>
    private async Task<WebhookOutcome> ForwardToMobileAsync(
        Guid messageId,
        PhoneNumber from,
        string body,
        int mediaCount,
        DateTimeOffset when,
        CancellationToken cancellationToken)
    {
        var line = lineOptions.CurrentValue;

        if (line.MyCell is not { } cell)
        {
            const string reason = "Telephony:MyCellNumber is not set, so there is nowhere to forward to.";
            logger.LogError("Message {MessageId}: {Reason}", messageId, reason);
            await messages.RejectAsync(messageId, reason, when, cancellationToken);
            return WebhookOutcome.Handled;
        }

        if (line.Did is not { } did)
        {
            const string reason = "Telephony:DidNumber is not set, so there is no number to send from.";
            logger.LogError("Message {MessageId}: {Reason}", messageId, reason);
            await messages.RejectAsync(messageId, reason, when, cancellationToken);
            return WebhookOutcome.Handled;
        }

        // No failure notice on this path, deliberately: the channel a notice would use is the very one
        // that just failed. A forward that cannot be sent is recorded and logged, and that is all it
        // can be.
        await RelayAsync(
            messageId,
            did,
            cell,
            ForwardText.ForInbound(from, body, mediaCount),
            when,
            notifyOnFailure: false,
            cancellationToken);

        return WebhookOutcome.Handled;
    }

    /// <summary>The operator texted in a "{recipient} body" command: send it from the DID.</summary>
    private async Task<WebhookOutcome> SendOnCommandAsync(
        Guid messageId,
        string body,
        DateTimeOffset when,
        CancellationToken cancellationToken)
    {
        var line = lineOptions.CurrentValue;

        if (!SmsCommand.TryParse(body, out var command, out var error))
        {
            logger.LogInformation("Message {MessageId} is not a send command: {Error}", messageId, error);
            await messages.RejectAsync(messageId, error, when, cancellationToken);
            await NotifyAsync(error, cancellationToken);
            return WebhookOutcome.Handled;
        }

        if (line.Did is not { } did)
        {
            const string reason = "Telephony:DidNumber is not set, so there is no number to send from.";
            logger.LogError("Message {MessageId}: {Reason}", messageId, reason);
            await messages.RejectAsync(messageId, reason, when, cancellationToken);
            return WebhookOutcome.Handled;
        }

        // Texting the DID from the DID would arrive back here as another command, forever.
        if (command.Recipient == did)
        {
            var reason = $"{command.Recipient.ToDisplayString()} is this line's own number.";
            logger.LogWarning("Message {MessageId}: {Reason}", messageId, reason);
            await messages.RejectAsync(messageId, reason, when, cancellationToken);
            await NotifyAsync(reason, cancellationToken);
            return WebhookOutcome.Handled;
        }

        await RelayAsync(
            messageId, did, command.Recipient, command.Body, when, notifyOnFailure: true, cancellationToken);

        return WebhookOutcome.Handled;
    }

    /// <summary>
    /// Records the intent to send, sends, and records what came back. The write before the send is what
    /// makes a process killed mid-flight leave a row that says Relaying rather than nothing at all.
    /// </summary>
    private async Task RelayAsync(
        Guid messageId,
        PhoneNumber from,
        PhoneNumber recipient,
        string body,
        DateTimeOffset when,
        bool notifyOnFailure,
        CancellationToken cancellationToken)
    {
        await messages.BeginRelayAsync(messageId, recipient, body, when, cancellationToken);

        var result = await telnyx.SendAsync(from, recipient, body, cancellationToken);

        if (result.Accepted)
        {
            await messages.RelayAcceptedAsync(messageId, result.ProviderMessageId ?? "", when, cancellationToken);
            logger.LogInformation(
                "Message {MessageId} relayed to {Recipient} as {ProviderMessageId}.",
                messageId,
                recipient.Value,
                result.ProviderMessageId);
            return;
        }

        var error = result.Error ?? "the provider refused the message";
        await messages.RelayFailedAsync(messageId, error, when, cancellationToken);
        logger.LogWarning(
            "Message {MessageId} could not be relayed to {Recipient}: {Error}",
            messageId,
            recipient.Value,
            error);

        if (notifyOnFailure)
        {
            await NotifyAsync($"{recipient.ToDisplayString()} - {error}", cancellationToken);
        }
    }

    /// <summary>
    /// Texts the operator that something went wrong. Best effort and unrecorded: it is not a relay of
    /// anything, and a notice that itself fails has nowhere left to complain to but the log.
    /// </summary>
    private async Task NotifyAsync(string reason, CancellationToken cancellationToken)
    {
        var settings = messagingOptions.CurrentValue;
        var line = lineOptions.CurrentValue;

        if (!settings.NotifyOnFailure || line.Did is not { } did || line.MyCell is not { } cell)
        {
            return;
        }

        var result = await telnyx.SendAsync(did, cell, ForwardText.ForFailure(reason), cancellationToken);
        if (!result.Accepted)
        {
            logger.LogWarning(
                "Could not text the failure notice back to {Cell}: {Error}", cell.Value, result.Error);
        }
    }

    /// <summary>Applies a carrier verdict about something CallTree sent.</summary>
    private async Task<WebhookOutcome> HandleReceiptAsync(
        TelnyxMessagePayload payload,
        DateTimeOffset when,
        CancellationToken cancellationToken)
    {
        if (payload.Id is not { Length: > 0 } providerMessageId)
        {
            return WebhookOutcome.Malformed;
        }

        var status = payload.To?.FirstOrDefault()?.Status;
        if (MapDelivery(status) is not { } delivery)
        {
            logger.LogDebug(
                "Ignoring delivery receipt for {ProviderMessageId} with status {Status}.",
                providerMessageId,
                status ?? "(none)");
            return WebhookOutcome.Ignored;
        }

        var error = payload.Errors is { Count: > 0 } errors ? errors[0].Describe() : null;

        // False is ordinary rather than an error: receipts also arrive for the failure notices, which
        // are not relays of anything and are deliberately not recorded.
        var applied = await messages.RecordDeliveryAsync(
            providerMessageId, delivery, error, when, cancellationToken);

        if (applied)
        {
            logger.LogInformation(
                "Relay {ProviderMessageId} is now {Delivery}{Error}.",
                providerMessageId,
                delivery,
                error is null ? "" : $" ({error})");
        }

        return applied ? WebhookOutcome.Handled : WebhookOutcome.Ignored;
    }

    /// <summary>
    /// Collapses the provider's per-recipient status to what the operator actually needs to know. An
    /// unrecognised status maps to null and is left alone rather than guessed at, so a status the
    /// provider adds later cannot overwrite a verdict already reached.
    /// </summary>
    private static RelayDelivery? MapDelivery(string? status) => status switch
    {
        "delivered" => RelayDelivery.Delivered,
        "delivery_unconfirmed" => RelayDelivery.Unconfirmed,
        "sending_failed" or "delivery_failed" or "failed" or "expired" => RelayDelivery.Failed,
        "sent" or "sending" => RelayDelivery.Sent,
        "queued" or "queued_for_delivery" => RelayDelivery.Queued,
        _ => null,
    };
}
