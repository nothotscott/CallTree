using System.Text;
using CallTree.Messaging.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CallTree.Messaging.Telnyx;

/// <summary>
/// Checks the Ed25519 signature the provider puts on every webhook.
/// </summary>
/// <remarks>
/// <para>
/// This is the only thing standing in front of the webhook endpoint. There is no authentication on this
/// API, the URL has to be reachable from the public internet for the feature to work at all, and a
/// request that gets through is enough to make this instance send a text at the operator's expense —
/// the messaging equivalent of the toll-fraud exposure the DID filter exists to close on the SIP port.
/// Treat a verification failure as an attack, not as a bug to be worked around by turning
/// <see cref="MessagingOptions.RequireSignature"/> off.
/// </para>
/// <para>
/// The signed message is <c>{telnyx-timestamp}|{raw request body}</c>, so the body must be the exact
/// bytes that arrived. Deserializing and re-serializing changes whitespace and key order and the
/// signature stops matching — which is why the controller reads the body as a string and hands the same
/// string to both this and the parser.
/// </para>
/// <para>
/// BouncyCastle rather than the framework: .NET 10 has no standalone Ed25519. The only occurrence in
/// <c>System.Security.Cryptography</c> is inside the Composite ML-DSA algorithm identifiers, which
/// cannot verify a bare Ed25519 signature.
/// </para>
/// </remarks>
public sealed class TelnyxSignatureVerifier(
    IOptionsMonitor<MessagingOptions> options,
    ILogger<TelnyxSignatureVerifier> logger)
{
    /// <summary>Header carrying the base64 Ed25519 signature.</summary>
    public const string SignatureHeader = "telnyx-signature-ed25519";

    /// <summary>Header carrying the Unix-seconds timestamp the signature covers.</summary>
    public const string TimestampHeader = "telnyx-timestamp";

    private const int PublicKeyLength = 32;
    private const int SignatureLength = 64;

    /// <summary>
    /// Whether this request is genuinely from the provider. <paramref name="failure"/> is filled in on
    /// rejection with something safe to log — never with any part of the signature or the key.
    /// </summary>
    public bool Verify(
        string body,
        string? signature,
        string? timestamp,
        DateTimeOffset now,
        out string failure)
    {
        var settings = options.CurrentValue;

        if (!settings.RequireSignature)
        {
            // Logged every time rather than once: this is a deliberately unsafe mode for bring-up, and
            // an instance left in it should be saying so in the log the operator is reading.
            logger.LogWarning(
                "Messaging:RequireSignature is off - accepting a webhook without checking who sent it. "
                + "Anyone who finds this URL can make this instance send a text.");
            failure = "";
            return true;
        }

        if (!TryDecodePublicKey(settings.PublicKey, out var publicKey))
        {
            failure = "Messaging:PublicKey is not set to a 32-byte base64 Ed25519 key.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp))
        {
            failure = $"The request is missing {SignatureHeader} or {TimestampHeader}.";
            return false;
        }

        if (!long.TryParse(timestamp, out var unixSeconds))
        {
            failure = $"{TimestampHeader} is not a Unix timestamp.";
            return false;
        }

        // Checked before the signature: a replayed request carries a perfectly valid signature, and
        // age is the only thing that distinguishes it from the original.
        var age = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var tolerance = TimeSpan.FromSeconds(Math.Max(1, settings.SignatureToleranceSeconds));
        if (age.Duration() > tolerance)
        {
            failure = $"The request is {age.Duration().TotalSeconds:F0}s out of date (tolerance {tolerance.TotalSeconds:F0}s).";
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            failure = $"{SignatureHeader} is not base64.";
            return false;
        }

        if (signatureBytes.Length != SignatureLength)
        {
            failure = $"{SignatureHeader} is {signatureBytes.Length} bytes, expected {SignatureLength}.";
            return false;
        }

        var signed = Encoding.UTF8.GetBytes($"{timestamp}|{body}");

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(signed, 0, signed.Length);

        if (!verifier.VerifySignature(signatureBytes))
        {
            failure = "The signature does not match the body.";
            return false;
        }

        failure = "";
        return true;
    }

    private static bool TryDecodePublicKey(string configured, out byte[] publicKey)
    {
        publicKey = [];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        try
        {
            var decoded = Convert.FromBase64String(configured.Trim());
            if (decoded.Length != PublicKeyLength)
            {
                return false;
            }

            publicKey = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
