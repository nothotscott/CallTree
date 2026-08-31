namespace CallTree.Messaging.Configuration;

/// <summary>
/// SMS settings: the provider credential, the webhook's signing key, and the relay's behaviour.
/// </summary>
/// <remarks>
/// A record for the same reason the telephony options are: value equality and <c>with</c> are what the
/// settings endpoint uses to describe a save before it writes it. The API key belongs in an environment
/// variable (<c>Messaging__ApiKey</c>), user secrets, or the writable config file the settings UI edits
/// - never in a committed file.
/// </remarks>
public sealed record MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>
    /// Master switch. Off means the webhook endpoint answers 404 and nothing is ever sent, whatever
    /// else is configured — so a half-finished setup cannot start relaying on its own.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>The provider API key (<c>KEY...</c>), sent as a bearer token. A credential.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>
    /// The provider's Ed25519 webhook public key, base64, copied from the portal. Not a secret — it is
    /// a public key — so unlike <see cref="ApiKey"/> it is safe to show in the settings UI.
    /// </summary>
    public string PublicKey { get; init; } = "";

    /// <summary>
    /// Optional messaging profile id to send under. Only needed when the DID belongs to more than one
    /// profile; the <c>from</c> number alone is otherwise enough to route a send.
    /// </summary>
    public string MessagingProfileId { get; init; } = "";

    /// <summary>
    /// Whether an unsigned or badly-signed webhook is refused.
    /// </summary>
    /// <remarks>
    /// Defaults to on and should stay on. The webhook URL is public by necessity, has no authentication
    /// in front of it, and reaching it is enough to make this instance send a text at the operator's
    /// expense — the same shape of exposure as the DID filter on the SIP port, and just as attractive to
    /// anyone who finds it. Turning this off is for bring-up only, when the public key has not been
    /// pasted in yet.
    /// </remarks>
    public bool RequireSignature { get; init; } = true;

    /// <summary>
    /// How far out of date a signed webhook may be. Bounds how long a captured request stays replayable;
    /// too small and ordinary clock skew starts rejecting real traffic.
    /// </summary>
    public int SignatureToleranceSeconds { get; init; } = 300;

    /// <summary>
    /// Whether a send command that could not be carried out is answered with a text back to the mobile.
    /// </summary>
    /// <remarks>
    /// On by default: the phone has no other channel. Without it a mistyped number is silent — the
    /// operator believes a message was sent that never was, and only the message log says otherwise.
    /// Successful sends are deliberately not acknowledged; that would double the message count for no
    /// information the operator does not already have.
    /// </remarks>
    public bool NotifyOnFailure { get; init; } = true;

    /// <summary>
    /// Timeout for one call to the provider's API. Kept short on purpose: the send happens inside the
    /// webhook request, and a provider that has stopped answering must not hold that request open until
    /// the provider's own timeout fires and it retries the whole delivery.
    /// </summary>
    public int ApiTimeoutSeconds { get; init; } = 10;

    /// <summary>Whether there is enough here to send anything at all.</summary>
    public bool IsConfigured => Enabled && ApiKey.Length > 0;
}
