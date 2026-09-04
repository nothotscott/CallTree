namespace CallTree.Telephony.Configuration;

/// <summary>
/// Loopback simulation mode: run the whole SIP/RTP stack against a local harness instead of a real
/// trunk, so call handling can be exercised without a phone, a provider, or a phone bill.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about call handling changes when this is on. The same INVITE path, the same DID filter, the
/// same screening gate, the same recorders and the same <c>PacedRtpRelay</c> run; the only two things
/// that move are where the stack gets its inbound calls from (a local harness rather than the trunk's
/// registrar binding) and where <c>PlaceOutboundLegAsync</c> sends an outbound leg (<see cref="LoopbackHost"/>
/// rather than the trunk). That is the whole point - a mode that stubbed out the interesting parts
/// would only ever prove that the stub works.
/// </para>
/// <para>
/// It is a top-level section rather than a property on <see cref="TelephonyOptions"/> on purpose. That
/// keeps it out of <see cref="TelephonySettingsWatcher"/> and out of the settings document the UI
/// writes: this is not a setting an operator adjusts on a live instance, it is a decision about what
/// the process is for, made once at startup by whoever launched it.
/// </para>
/// <para>
/// The two safety rules are in <c>TelephonyBackgroundService</c> rather than here, because refusing to
/// start is not something an options type can do: spoofing plus a configured trunk is a hard error (an
/// instance must never be half-simulated against a real line), and inbound INVITEs are refused unless
/// they arrive from a loopback address, so an accidentally-exposed spoofing instance is not an open
/// dialer with the trunk's guard rails removed.
/// </para>
/// </remarks>
public sealed record SpoofOptions
{
    public const string SectionName = "Spoof";

    /// <summary>
    /// Start the SIP stack with no trunk and no registration, dialing outbound legs at
    /// <see cref="LoopbackHost"/>. Refused outright when <c>Trunk:Host</c> is also set.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Where outbound legs go instead of the trunk - the harness's SIP port. Both the inbound bridge to
    /// <c>Telephony:MyCellNumber</c> and the <c>*{NUMBER}#</c> proxy dial land here, so the harness
    /// answers as whichever party the number says it is.
    /// </summary>
    public string LoopbackHost { get; init; } = "127.0.0.1:5070";

    /// <summary>
    /// Accept spoofed INVITEs from off-box as well as from loopback. Off by default: with no trunk
    /// registration there is nothing tying this instance to a provider, so the DID filter and the
    /// screening gate are the only things between the public SIP port and an outbound leg. Turn it on
    /// only to drive an instance from another machine on a network you control.
    /// </summary>
    public bool AllowRemoteCallers { get; init; }
}
