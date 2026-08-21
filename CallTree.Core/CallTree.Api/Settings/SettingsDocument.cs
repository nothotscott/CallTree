using System.Text.Json.Nodes;
using CallTree.Telephony.Configuration;

namespace CallTree.Api.Settings;

/// <summary>
/// Translates between the settings DTOs, the bound options, and the JSON document on disk.
/// </summary>
/// <remarks>
/// Kept free of file and container access so the merge rules — which decide whether a saved trunk
/// password survives — can be tested directly.
/// </remarks>
public static class SettingsDocument
{
    private const string TelephonySection = TelephonyOptions.SectionName;
    private const string TrunkSection = TrunkOptions.SectionName;

    /// <summary>Every configuration key this endpoint writes, in the form the config system uses.</summary>
    public static readonly IReadOnlyList<string> ManagedKeys =
    [
        $"{TelephonySection}:{nameof(TelephonyOptions.MyCellNumber)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.DidNumber)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.PublicHost)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.SipListenPort)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.ListenOnTcp)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.RtpPortStart)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.RtpPortEnd)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.TraceSip)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.ScreeningDigit)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.ScreeningTimeoutSeconds)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.JitterBufferMilliseconds)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.RecordingToneIntervalSeconds)}",
        $"{TelephonySection}:{nameof(TelephonyOptions.OutboundPin)}",
        $"{TrunkSection}:{nameof(TrunkOptions.Host)}",
        $"{TrunkSection}:{nameof(TrunkOptions.Port)}",
        $"{TrunkSection}:{nameof(TrunkOptions.Username)}",
        $"{TrunkSection}:{nameof(TrunkOptions.AuthUsername)}",
        $"{TrunkSection}:{nameof(TrunkOptions.Password)}",
        $"{TrunkSection}:{nameof(TrunkOptions.RegistrationExpirySeconds)}",
    ];

    public static TelephonySettings ToSettings(TelephonyOptions options) => new()
    {
        MyCellNumber = options.MyCellNumber,
        DidNumber = options.DidNumber,
        PublicHost = options.PublicHost,
        SipListenPort = options.SipListenPort,
        ListenOnTcp = options.ListenOnTcp,
        RtpPortStart = options.RtpPortStart,
        RtpPortEnd = options.RtpPortEnd,
        TraceSip = options.TraceSip,
        ScreeningDigit = options.ScreeningDigit,
        ScreeningTimeoutSeconds = options.ScreeningTimeoutSeconds,
        JitterBufferMilliseconds = options.JitterBufferMilliseconds,
        RecordingToneIntervalSeconds = options.RecordingToneIntervalSeconds,
        // OutboundPin is deliberately absent: it is a credential, and the response reports only whether
        // one is set. See SettingsResponse.OutboundPinSet.
    };

    public static TrunkSettings ToSettings(TrunkOptions options) => new()
    {
        Host = options.Host,
        Port = options.Port,
        Username = options.Username,
        AuthUsername = options.AuthUsername,
        RegistrationExpirySeconds = options.RegistrationExpirySeconds,
    };

    /// <summary>
    /// Applies the editable fields to the current options, leaving the rest as they are. Used to ask
    /// the settings watcher whether a save will need a restart, before it is written.
    /// </summary>
    public static TelephonyOptions Apply(TelephonyOptions current, TelephonySettings settings, string? outboundPin) => current with
    {
        OutboundPin = outboundPin ?? current.OutboundPin,
        JitterBufferMilliseconds = settings.JitterBufferMilliseconds,
        RecordingToneIntervalSeconds = settings.RecordingToneIntervalSeconds,
        MyCellNumber = settings.MyCellNumber,
        DidNumber = settings.DidNumber,
        PublicHost = settings.PublicHost,
        SipListenPort = settings.SipListenPort,
        ListenOnTcp = settings.ListenOnTcp,
        RtpPortStart = settings.RtpPortStart,
        RtpPortEnd = settings.RtpPortEnd,
        TraceSip = settings.TraceSip,
        ScreeningDigit = settings.ScreeningDigit,
        ScreeningTimeoutSeconds = settings.ScreeningTimeoutSeconds,
    };

    public static TrunkOptions Apply(TrunkOptions current, TrunkSettings settings, string? password) => current with
    {
        Host = settings.Host,
        Port = settings.Port,
        Username = settings.Username,
        AuthUsername = Blank(settings.AuthUsername) ? null : settings.AuthUsername,
        RegistrationExpirySeconds = settings.RegistrationExpirySeconds,
        Password = password ?? current.Password,
    };

    /// <summary>
    /// Writes the managed keys into an existing config document, in place. Anything else already in
    /// the file is left untouched — the file is the operator's, not this endpoint's.
    /// </summary>
    public static JsonObject Apply(JsonObject document, SettingsUpdate update)
    {
        var telephony = Section(document, TelephonySection);
        telephony[nameof(TelephonyOptions.MyCellNumber)] = update.Telephony.MyCellNumber;
        telephony[nameof(TelephonyOptions.DidNumber)] = update.Telephony.DidNumber;
        telephony[nameof(TelephonyOptions.PublicHost)] = update.Telephony.PublicHost;
        telephony[nameof(TelephonyOptions.SipListenPort)] = update.Telephony.SipListenPort;
        telephony[nameof(TelephonyOptions.ListenOnTcp)] = update.Telephony.ListenOnTcp;
        telephony[nameof(TelephonyOptions.RtpPortStart)] = update.Telephony.RtpPortStart;
        telephony[nameof(TelephonyOptions.RtpPortEnd)] = update.Telephony.RtpPortEnd;
        telephony[nameof(TelephonyOptions.TraceSip)] = update.Telephony.TraceSip;
        telephony[nameof(TelephonyOptions.ScreeningDigit)] = update.Telephony.ScreeningDigit;
        telephony[nameof(TelephonyOptions.ScreeningTimeoutSeconds)] = update.Telephony.ScreeningTimeoutSeconds;
        telephony[nameof(TelephonyOptions.JitterBufferMilliseconds)] = update.Telephony.JitterBufferMilliseconds;
        telephony[nameof(TelephonyOptions.RecordingToneIntervalSeconds)] = update.Telephony.RecordingToneIntervalSeconds;

        // Same rule as the trunk password: only written when one was actually supplied, so saving an
        // unrelated field cannot silently disable the PIN gate on the path that answers automatically
        // and records. An empty string is a deliberate "turn it off".
        if (update.OutboundPin is not null)
        {
            telephony[nameof(TelephonyOptions.OutboundPin)] = update.OutboundPin;
        }

        var trunk = Section(document, TrunkSection);
        trunk[nameof(TrunkOptions.Host)] = update.Trunk.Host;
        trunk[nameof(TrunkOptions.Port)] = update.Trunk.Port;
        trunk[nameof(TrunkOptions.Username)] = update.Trunk.Username;
        trunk[nameof(TrunkOptions.RegistrationExpirySeconds)] = update.Trunk.RegistrationExpirySeconds;

        // Absent rather than null or empty: a key present with an empty value would override the same
        // key coming from user secrets, which is where these live in development.
        if (Blank(update.Trunk.AuthUsername))
        {
            trunk.Remove(nameof(TrunkOptions.AuthUsername));
        }
        else
        {
            trunk[nameof(TrunkOptions.AuthUsername)] = update.Trunk.AuthUsername;
        }

        // Only written when one was actually supplied. Saving an unrelated field must not blank out a
        // password that came from user secrets or the environment, and the UI never receives the
        // current value to send back.
        if (update.TrunkPassword is not null)
        {
            trunk[nameof(TrunkOptions.Password)] = update.TrunkPassword;
        }

        return document;
    }

    /// <summary>
    /// Which managed keys are set in the environment. The environment provider sits above the config
    /// file, so these keep winning no matter what is saved.
    /// </summary>
    public static IReadOnlyList<string> EnvironmentOverrides() =>
        ManagedKeys.Where(IsSetInEnvironment).ToList();

    private static bool IsSetInEnvironment(string key) =>
        // Double underscore is the portable spelling; a literal colon also works where the OS allows it.
        Environment.GetEnvironmentVariable(key.Replace(":", "__")) is not null
        || Environment.GetEnvironmentVariable(key) is not null;

    private static JsonObject Section(JsonObject document, string name)
    {
        if (document[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        document[name] = created;
        return created;
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
