using System.ComponentModel.DataAnnotations;
using CallTree.Telephony.Configuration;

namespace CallTree.Api.Settings;

/// <summary>
/// The subset of <see cref="TelephonyOptions"/> the settings UI may change.
/// </summary>
/// <remarks>
/// <c>PromptsRoot</c> is deliberately absent. It is a deployment path, not an operator setting: in the
/// container the prompts are baked in at a fixed location, and pointing it somewhere empty produces an
/// IVR that answers in silence — signalling succeeds, so it reads as working until nobody hears the
/// press-1 instruction.
/// </remarks>
public sealed record TelephonySettings
{
    /// <summary>Calls whose caller ID matches this are classified Outbound. Blank disables the match.</summary>
    public string MyCellNumber { get; init; } = "";

    /// <summary>The DID this instance owns. Blank accepts any request URI — see the warning on save.</summary>
    public string DidNumber { get; init; } = "";

    /// <summary>Public IP or DDNS hostname. Mandatory behind NAT. Applies at startup.</summary>
    public string PublicHost { get; init; } = "";

    [Range(1, 65535)]
    public int SipListenPort { get; init; } = 5060;

    public bool ListenOnTcp { get; init; } = true;

    [Range(1, 65535)]
    public int RtpPortStart { get; init; } = 10000;

    [Range(1, 65535)]
    public int RtpPortEnd { get; init; } = 10100;

    /// <summary>Raises the SIP wire-trace log category to Trace. Applies immediately.</summary>
    public bool TraceSip { get; init; }

    /// <summary>0-9, or 10 for * and 11 for #, matching RFC 4733 event codes.</summary>
    [Range(0, 15)]
    public byte ScreeningDigit { get; init; } = 1;

    [Range(1, 300)]
    public int ScreeningTimeoutSeconds { get; init; } = 12;

    /// <summary>How long the outbound leg to the mobile is allowed to ring before it counts as a miss.</summary>
    [Range(1, 120)]
    public int DialTimeoutSeconds { get; init; } = 25;

    /// <summary>Reordering window for received RTP before it is written to a recording.</summary>
    [Range(0, 1000)]
    public int JitterBufferMilliseconds { get; init; } = 60;

    /// <summary>
    /// Seconds between recording-notice tones, or 0 for none. The only disclosure the Outbound path can
    /// make to a party merged in by the handset — see the consent note in TODO.md.
    /// </summary>
    [Range(0, 600)]
    public int RecordingToneIntervalSeconds { get; init; }
}

/// <summary>
/// The subset of <see cref="TrunkOptions"/> the settings UI may change. The password is write-only:
/// it is never returned, only reported as set or not.
/// </summary>
public sealed record TrunkSettings
{
    public string Host { get; init; } = "";

    [Range(1, 65535)]
    public int Port { get; init; } = 5060;

    public string Username { get; init; } = "";

    /// <summary>Only needed by providers that split the SIP and auth usernames. Not honoured yet.</summary>
    public string? AuthUsername { get; init; }

    [Range(30, 3600)]
    public int RegistrationExpirySeconds { get; init; } = 120;
}

/// <summary>A settings save. Every section is required: this is a replace, not a patch.</summary>
public sealed record SettingsUpdate
{
    [Required]
    public required TelephonySettings Telephony { get; init; }

    [Required]
    public required TrunkSettings Trunk { get; init; }

    [Required]
    public required MessagingSettings Messaging { get; init; }

    /// <summary>
    /// The trunk password. Null or omitted leaves whatever is already configured alone, so the UI can
    /// save an unrelated field without ever holding the secret. A value — including an empty string —
    /// is written to the config file verbatim.
    /// </summary>
    public string? TrunkPassword { get; init; }

    /// <summary>
    /// The Outbound-path PIN, handled exactly like <see cref="TrunkPassword"/>: null leaves it alone,
    /// an empty string turns the PIN off. It is a credential, so it is never returned — only reported
    /// as set. Digits only, because it is entered on a phone keypad.
    /// </summary>
    [RegularExpression("^[0-9]*$", ErrorMessage = "The PIN must be digits only - it is keyed in on a phone.")]
    [StringLength(12)]
    public string? OutboundPin { get; init; }

    /// <summary>
    /// The messaging provider API key, handled exactly like <see cref="TrunkPassword"/>: null leaves it
    /// alone, an empty string removes it. Never returned - only reported as set.
    /// </summary>
    public string? MessagingApiKey { get; init; }
}

/// <summary>The effective configuration, plus what the operator needs to know to trust it.</summary>
public sealed record SettingsResponse
{
    public required TelephonySettings Telephony { get; init; }

    public required TrunkSettings Trunk { get; init; }

    public required MessagingSettings Messaging { get; init; }

    /// <summary>Whether a trunk password is configured. The value itself is never sent.</summary>
    public required bool TrunkPasswordSet { get; init; }

    /// <summary>
    /// Whether a messaging API key is configured. The value itself is never sent. False means nothing
    /// can be sent, however the rest of the messaging section reads.
    /// </summary>
    public required bool MessagingApiKeySet { get; init; }

    /// <summary>
    /// Whether the Outbound path is gated by a PIN. The value itself is never sent. False means caller
    /// ID alone decides who gets answered and recorded on that path.
    /// </summary>
    public required bool OutboundPinSet { get; init; }

    /// <summary>Whether the trunk has enough configuration for the SIP stack to register at all.</summary>
    public required bool TrunkConfigured { get; init; }

    /// <summary>
    /// Settings that have changed since the SIP stack started and only take effect at startup. Non-empty
    /// means the running behaviour does not match what this response describes.
    /// </summary>
    public required IReadOnlyList<string> PendingRestartKeys { get; init; }

    /// <summary>
    /// Every key that only takes effect at startup, changed or not, so the UI can say so before a save
    /// rather than after one.
    /// </summary>
    public required IReadOnlyList<string> RestartOnlyKeys { get; init; }

    /// <summary>
    /// Keys currently supplied by an environment variable. The environment sits above the config file,
    /// so saving these has no effect until the variable is removed — worth saying out loud rather than
    /// letting a save appear to succeed and change nothing.
    /// </summary>
    public required IReadOnlyList<string> EnvironmentOverrides { get; init; }

    /// <summary>Absolute path of the file a save writes to.</summary>
    public required string ConfigFilePath { get; init; }

    public required bool ConfigFileExists { get; init; }
}

/// <summary>
/// The subset of <c>MessagingOptions</c> the settings UI may change.
/// </summary>
/// <remarks>
/// The API key is absent for the same reason the trunk password is: it is a credential, and the
/// response reports only whether one is set. <c>PublicKey</c> is here in full because it is a *public*
/// key — the operator copies it out of the provider's portal, and hiding it would only make it
/// impossible to check against the portal without clearing and retyping it.
/// </remarks>
public sealed record MessagingSettings
{
    /// <summary>
    /// Master switch. Off means the webhook answers 404 and nothing is ever sent, whatever else is set.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>The provider's Ed25519 webhook public key, base64 (32 bytes decoded).</summary>
    public string PublicKey { get; init; } = "";

    /// <summary>Only needed when the DID belongs to more than one messaging profile.</summary>
    public string MessagingProfileId { get; init; } = "";

    /// <summary>
    /// Whether an unsigned or badly-signed webhook is refused. Leave on: the URL is public, there is no
    /// other authentication, and reaching it is enough to make this instance send a text.
    /// </summary>
    public bool RequireSignature { get; init; } = true;

    /// <summary>How far out of date a signed webhook may be, which bounds how long one stays replayable.</summary>
    [Range(30, 3600)]
    public int SignatureToleranceSeconds { get; init; } = 300;

    /// <summary>Whether a send command that could not be carried out is answered with a text back.</summary>
    public bool NotifyOnFailure { get; init; } = true;

    /// <summary>Timeout for one call to the provider's API, from inside the webhook request.</summary>
    [Range(1, 120)]
    public int ApiTimeoutSeconds { get; init; } = 10;
}
