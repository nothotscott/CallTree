namespace CallTree.Telephony.Configuration;

/// <summary>
/// SIP trunk (or test PBX extension) registration settings. The password should come
/// from an environment variable (Trunk__Password), never from a committed file.
/// </summary>
public sealed class TrunkOptions
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
