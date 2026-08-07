using CallTree.Telephony.Configuration;
using Microsoft.Extensions.Options;

namespace CallTree.Telephony;

/// <summary>
/// Tracks which telephony settings the running SIP stack was built from, so a configuration change can
/// be reported honestly as "applied" or "needs a restart".
/// </summary>
/// <remarks>
/// Some settings are read on every call and follow configuration live (the numbers, the screening
/// digit and timeout, SIP tracing). The rest were baked into sockets and a registration binding when
/// the process started: rebinding a UDP port or re-registering a trunk mid-flight would drop calls and,
/// worse, briefly hand the provider's binding to nobody. So they are deliberately restart-only, and
/// the one thing that must not happen is a settings screen that accepts a new SIP port and says
/// nothing while the old one stays live.
/// </remarks>
public sealed class TelephonySettingsWatcher(
    IOptionsMonitor<TelephonyOptions> telephonyOptions,
    IOptionsMonitor<TrunkOptions> trunkOptions)
{
    /// <summary>Telephony keys the SIP stack only reads while starting up.</summary>
    private static readonly (string Key, Func<TelephonyOptions, object?> Read)[] TelephonyStartupKeys =
    [
        ($"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.SipListenPort)}", o => o.SipListenPort),
        ($"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.ListenOnTcp)}", o => o.ListenOnTcp),
        ($"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.RtpPortStart)}", o => o.RtpPortStart),
        ($"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.RtpPortEnd)}", o => o.RtpPortEnd),
        ($"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.PublicHost)}", o => o.PublicHost),
        ($"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.PromptsRoot)}", o => o.PromptsRoot),
    ];

    /// <summary>Every trunk key: the registration is established once and never rebuilt.</summary>
    private static readonly (string Key, Func<TrunkOptions, object?> Read)[] TrunkStartupKeys =
    [
        ($"{TrunkOptions.SectionName}:{nameof(TrunkOptions.Host)}", o => o.Host),
        ($"{TrunkOptions.SectionName}:{nameof(TrunkOptions.Port)}", o => o.Port),
        ($"{TrunkOptions.SectionName}:{nameof(TrunkOptions.Username)}", o => o.Username),
        ($"{TrunkOptions.SectionName}:{nameof(TrunkOptions.AuthUsername)}", o => o.AuthUsername ?? ""),
        ($"{TrunkOptions.SectionName}:{nameof(TrunkOptions.Password)}", o => o.Password),
        ($"{TrunkOptions.SectionName}:{nameof(TrunkOptions.RegistrationExpirySeconds)}", o => o.RegistrationExpirySeconds),
    ];

    /// <summary>
    /// Every key that only takes effect at startup, whether or not it has changed. Exposed so the
    /// settings UI can label the fields up front rather than keeping its own copy of the list, which
    /// would quietly drift the first time one of these becomes reloadable.
    /// </summary>
    public static IReadOnlyList<string> StartupOnlyKeys { get; } =
    [
        .. TelephonyStartupKeys.Select(entry => entry.Key),
        .. TrunkStartupKeys.Select(entry => entry.Key),
    ];

    private TelephonyOptions? _startupTelephony;
    private TrunkOptions? _startupTrunk;

    /// <summary>The values the SIP stack actually started with. Null until it has started.</summary>
    public bool HasStarted => _startupTelephony is not null;

    /// <summary>Called by the hosted service once, before it touches a socket.</summary>
    public void CaptureStartupSnapshot(TelephonyOptions telephony, TrunkOptions trunk)
    {
        _startupTelephony = telephony;
        _startupTrunk = trunk;
    }

    /// <summary>Startup-only keys whose current value differs from what the stack is running.</summary>
    public IReadOnlyList<string> PendingRestartKeys =>
        PendingRestartKeysFor(telephonyOptions.CurrentValue, trunkOptions.CurrentValue);

    /// <summary>
    /// The same comparison against candidate values, so the settings endpoint can tell the operator a
    /// restart will be needed at the moment they save rather than after the file watcher catches up.
    /// </summary>
    public IReadOnlyList<string> PendingRestartKeysFor(TelephonyOptions telephony, TrunkOptions trunk)
    {
        // Nothing is running yet, so nothing can be stale. This is the "telephony is idle" case too:
        // the snapshot is taken before the trunk-configured check, so configuring a trunk from the UI
        // correctly reports that the process has to be restarted to pick it up.
        if (_startupTelephony is null || _startupTrunk is null)
        {
            return [];
        }

        var pending = new List<string>();

        foreach (var (key, read) in TelephonyStartupKeys)
        {
            if (!Equals(read(telephony), read(_startupTelephony)))
            {
                pending.Add(key);
            }
        }

        foreach (var (key, read) in TrunkStartupKeys)
        {
            if (!Equals(read(trunk), read(_startupTrunk)))
            {
                pending.Add(key);
            }
        }

        return pending;
    }
}
