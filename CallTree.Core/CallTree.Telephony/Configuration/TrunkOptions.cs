namespace CallTree.Telephony.Configuration;

/// <summary>
/// SIP trunk (or test PBX extension) registration settings. The password should come from an
/// environment variable (Trunk__Password), user secrets, or the writable config file the settings UI
/// edits — never from a committed file.
/// </summary>
/// <remarks>
/// A record so that a snapshot can be compared with <c>==</c> and amended with <c>with</c>:
/// <see cref="TelephonySettingsWatcher"/> relies on both to work out whether a configuration reload
/// changed anything that only takes effect at startup.
/// </remarks>
public sealed record TrunkOptions
{
    public const string SectionName = "Trunk";

    public string Host { get; init; } = "";
    public int Port { get; init; } = 5060;
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    /// <summary>Auth username when it differs from the SIP username (some providers split these).</summary>
    public string? AuthUsername { get; init; }

    public int RegistrationExpirySeconds { get; init; } = 120;

    public bool IsConfigured => Host.Length > 0 && Username.Length > 0;
}
