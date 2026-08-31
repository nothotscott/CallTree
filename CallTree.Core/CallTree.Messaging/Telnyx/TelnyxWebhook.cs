using System.Text.Json;
using System.Text.Json.Serialization;

namespace CallTree.Messaging.Telnyx;

/// <summary>
/// The provider's webhook envelope, cut down to the fields CallTree acts on.
/// </summary>
/// <remarks>
/// <para>
/// Property names are mapped by <see cref="TelnyxJson.Options"/>'s snake_case policy rather than by an
/// attribute on every member, so <c>event_type</c>, <c>phone_number</c> and the rest bind without
/// forty annotations. Unknown fields are ignored, which is the point of modelling only a subset: the
/// provider adds fields, and a webhook that started failing because of one would take the whole feature
/// down.
/// </para>
/// <para>
/// Everything is nullable because none of it is guaranteed. This is parsed from a public endpoint; a
/// missing <c>from</c> must produce a rejected request, not a <c>NullReferenceException</c> inside a
/// handler that has already been told the request is authentic.
/// </para>
/// </remarks>
public sealed record TelnyxWebhook
{
    public TelnyxEvent? Data { get; init; }
}

public sealed record TelnyxEvent
{
    /// <summary>
    /// <c>message.received</c> for an inbound text, <c>message.sent</c> and <c>message.finalized</c> for
    /// receipts about a message we sent.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>When the provider says the event happened. Falls back to our clock when absent.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    public TelnyxMessagePayload? Payload { get; init; }
}

public sealed record TelnyxMessagePayload
{
    /// <summary>The provider's id for this message — the idempotency key on the way in, the receipt key on the way back.</summary>
    public string? Id { get; init; }

    /// <summary><c>inbound</c> or <c>outbound</c>.</summary>
    public string? Direction { get; init; }

    public TelnyxAddress? From { get; init; }

    /// <summary>A list because a message can be addressed to several numbers. Ours is the first.</summary>
    public IReadOnlyList<TelnyxAddress>? To { get; init; }

    public string? Text { get; init; }

    /// <summary><c>SMS</c> or <c>MMS</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Attachments. Counted and reported, never fetched or forwarded — see SmsRelayService.</summary>
    public IReadOnlyList<TelnyxMedia>? Media { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }

    public IReadOnlyList<TelnyxError>? Errors { get; init; }
}

public sealed record TelnyxAddress
{
    public string? PhoneNumber { get; init; }

    /// <summary>
    /// Per-recipient delivery state on a receipt: <c>queued</c>, <c>sent</c>, <c>delivered</c>,
    /// <c>sending_failed</c>, <c>delivery_failed</c>, <c>delivery_unconfirmed</c>.
    /// </summary>
    public string? Status { get; init; }
}

public sealed record TelnyxMedia
{
    public string? Url { get; init; }

    public string? ContentType { get; init; }
}

public sealed record TelnyxError
{
    public string? Code { get; init; }

    public string? Title { get; init; }

    public string? Detail { get; init; }

    /// <summary>The most useful single line, for a log entry or a text back to the operator.</summary>
    public string Describe() =>
        new[] { Detail, Title, Code }.FirstOrDefault(part => !string.IsNullOrWhiteSpace(part)) ?? "unspecified error";
}

/// <summary>The provider's reply to a send.</summary>
public sealed record TelnyxSendResponse
{
    public TelnyxMessagePayload? Data { get; init; }

    public IReadOnlyList<TelnyxError>? Errors { get; init; }
}

/// <summary>One JSON configuration, shared by the webhook parser and the API client.</summary>
public static class TelnyxJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
