using System.Text.Json;
using CallTree.Messaging;
using CallTree.Messaging.Configuration;
using CallTree.Messaging.Telnyx;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CallTree.Api.Controllers;

/// <summary>
/// Where the messaging provider delivers inbound texts and delivery receipts.
/// </summary>
/// <remarks>
/// <para>
/// This is the one endpoint in the whole API that is meant to be reachable from the public internet,
/// and it is the one that can spend money — a request that gets through can make this instance send a
/// text. Its signature check is therefore not optional decoration, it is the door; see
/// <see cref="TelnyxSignatureVerifier"/>. Point the messaging profile's webhook URL at
/// <c>/api/messaging/telnyx</c> and paste the portal's public key into <c>Messaging:PublicKey</c>.
/// </para>
/// <para>
/// The body is read as a raw string rather than model-bound, because the signature covers the exact
/// bytes that arrived: binding to a model and re-serializing would change whitespace and key order and
/// nothing would ever verify.
/// </para>
/// </remarks>
[ApiController]
[Route("api/messaging")]
public class MessagingWebhookController(
    SmsRelayService relay,
    TelnyxSignatureVerifier verifier,
    IOptionsMonitor<MessagingOptions> options,
    ILogger<MessagingWebhookController> logger) : ControllerBase
{
    /// <summary>
    /// Takes one webhook. Answers 200 for anything understood — including duplicates and events we do
    /// not act on — because the provider retries every non-2xx, and a retry of something already
    /// handled costs another forward.
    /// </summary>
    [HttpPost("telnyx")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Telnyx(CancellationToken cancellationToken)
    {
        // 404 rather than a disabled-looking error: an instance with messaging switched off should be
        // indistinguishable from one that never had the feature.
        if (!options.CurrentValue.Enabled)
        {
            return NotFound();
        }

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        if (!verifier.Verify(
                body,
                Request.Headers[TelnyxSignatureVerifier.SignatureHeader],
                Request.Headers[TelnyxSignatureVerifier.TimestampHeader],
                DateTimeOffset.UtcNow,
                out var failure))
        {
            // Warning, not information: on a public URL this is either a misconfiguration that stops
            // every message, or somebody trying the door. Both are worth seeing.
            logger.LogWarning(
                "Rejected a messaging webhook from {RemoteAddress}: {Failure}",
                HttpContext.Connection.RemoteIpAddress,
                failure);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        TelnyxWebhook? webhook;
        try
        {
            webhook = JsonSerializer.Deserialize<TelnyxWebhook>(body, TelnyxJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "A messaging webhook body was not valid JSON.");
            return BadRequest();
        }

        if (webhook is null)
        {
            return BadRequest();
        }

        var outcome = await relay.HandleAsync(webhook, DateTimeOffset.UtcNow, cancellationToken);

        // Malformed is 400 on purpose: the provider stops retrying a 4xx, and a body we cannot read will
        // not become readable on the third attempt.
        return outcome == WebhookOutcome.Malformed ? BadRequest() : Ok();
    }
}
