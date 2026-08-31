using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CallTree.Domain.ValueObjects;
using CallTree.Messaging.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTree.Messaging.Telnyx;

/// <summary>What the provider said about a send we asked for.</summary>
/// <param name="ProviderMessageId">
/// The id to match later delivery receipts against. Null when the send was refused.
/// </param>
/// <param name="Error">The provider's own words for a refusal, fit to store and to text back.</param>
public readonly record struct TelnyxSendResult(bool Accepted, string? ProviderMessageId, string? Error)
{
    public static TelnyxSendResult Sent(string providerMessageId) => new(true, providerMessageId, null);

    public static TelnyxSendResult Refused(string error) => new(false, null, error);
}

/// <summary>
/// Sends one message through the provider's REST API.
/// </summary>
/// <remarks>
/// The credential is attached per request rather than to the <see cref="HttpClient"/>, and the timeout
/// is applied with a linked token rather than <see cref="HttpClient.Timeout"/>, because both are
/// configuration the settings UI can change while the process is running — anything baked into the
/// client at construction would keep using the old value until a restart, silently.
/// </remarks>
public sealed class TelnyxClient(
    HttpClient http,
    IOptionsMonitor<MessagingOptions> options,
    ILogger<TelnyxClient> logger)
{
    public const string MessagesEndpoint = "https://api.telnyx.com/v2/messages";

    public async Task<TelnyxSendResult> SendAsync(
        PhoneNumber from,
        PhoneNumber to,
        string text,
        CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;

        if (settings.ApiKey.Length == 0)
        {
            return TelnyxSendResult.Refused("Messaging:ApiKey is not set.");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint)
        {
            Content = JsonContent.Create(
                new TelnyxSendRequest
                {
                    From = from.Value,
                    To = to.Value,
                    Text = text,
                    // Omitted unless configured: the from number alone routes a send, and sending a
                    // blank profile id is a 422 rather than a no-op.
                    MessagingProfileId = settings.MessagingProfileId is { Length: > 0 } profile ? profile : null,
                },
                options: TelnyxJson.Options),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.ApiTimeoutSeconds)));

        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var error = DescribeError(body) ?? $"the provider answered {(int)response.StatusCode} {response.ReasonPhrase}";
                logger.LogWarning(
                    "Telnyx refused a send to {Recipient}: {StatusCode} {Error}",
                    to.Value,
                    (int)response.StatusCode,
                    error);
                return TelnyxSendResult.Refused(error);
            }

            var parsed = JsonSerializer.Deserialize<TelnyxSendResponse>(body, TelnyxJson.Options);
            var id = parsed?.Data?.Id;

            if (string.IsNullOrWhiteSpace(id))
            {
                // Accepted but unidentifiable. The message is very likely on its way, so this is not
                // failed - but no delivery receipt can ever be matched to it, which is worth a warning.
                logger.LogWarning("Telnyx accepted a send to {Recipient} but returned no message id.", to.Value);
                return TelnyxSendResult.Sent("");
            }

            return TelnyxSendResult.Sent(id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Telnyx did not answer within {Timeout}s for a send to {Recipient}.",
                settings.ApiTimeoutSeconds, to.Value);
            return TelnyxSendResult.Refused($"the provider did not answer within {settings.ApiTimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach Telnyx to send to {Recipient}.", to.Value);
            return TelnyxSendResult.Refused($"the provider could not be reached: {ex.Message}");
        }
    }

    /// <summary>Pulls the provider's own words out of an error body, when it sent any.</summary>
    private static string? DescribeError(string body)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<TelnyxSendResponse>(body, TelnyxJson.Options);
            return parsed?.Errors is { Count: > 0 } errors ? errors[0].Describe() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TelnyxSendRequest
    {
        public required string From { get; init; }

        public required string To { get; init; }

        public required string Text { get; init; }

        public string? MessagingProfileId { get; init; }
    }
}
