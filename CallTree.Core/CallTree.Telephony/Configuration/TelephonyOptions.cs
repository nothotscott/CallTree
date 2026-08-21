namespace CallTree.Telephony.Configuration;

/// <remarks>
/// A record for the same reason as <see cref="TrunkOptions"/>: value equality and <c>with</c> are what
/// <see cref="TelephonySettingsWatcher"/> and the settings endpoint are built on.
/// </remarks>
public sealed record TelephonyOptions
{
    public const string SectionName = "Telephony";

    /// <summary>My cell number; inbound calls matching this caller ID are classified as CallSource.Outbound.</summary>
    public string MyCellNumber { get; init; } = "";

    public int SipListenPort { get; init; } = 5060;

    /// <summary>RTP port range — keep narrow and matched to the router/firewall forward.</summary>
    public int RtpPortStart { get; init; } = 10000;

    public int RtpPortEnd { get; init; } = 10100;

    /// <summary>
    /// Public IP address (or DDNS hostname) of this host as seen from the internet. Required when running
    /// behind NAT: without it the REGISTER Contact and the answer SDP advertise the LAN address, which the
    /// trunk cannot route to — inbound calls then fail before an INVITE ever reaches us.
    /// </summary>
    public string PublicHost { get; init; } = "";

    /// <summary>
    /// Logs every SIP message sent and received. Noisy; for bring-up and NAT debugging. Setting this
    /// raises the <see cref="SipTrace.CategoryName"/> log category to Trace on its own — there is no
    /// second logging setting to keep in step — and it takes effect without a restart, so tracing can
    /// be turned on during a misbehaving call without dropping the registration.
    /// </summary>
    public bool TraceSip { get; init; }

    /// <summary>
    /// The DID this instance owns. Inbound INVITEs addressed to anything else are rejected with 404
    /// before a Call row is created — an open SIP port attracts constant dial-plan probing for toll
    /// fraud, and answering those calls both confirms a live PBX and fills the database with junk.
    /// Leave blank to accept any request URI (the pre-Phase-2 behaviour).
    /// </summary>
    public string DidNumber { get; init; } = "";

    /// <summary>Directory holding the IVR prompt WAVs (8 or 16 kHz, 16-bit, mono PCM).</summary>
    public string PromptsRoot { get; init; } = "prompts";

    /// <summary>The digit an inbound caller must press to get past the spam gate.</summary>
    public byte ScreeningDigit { get; init; } = 1;

    /// <summary>
    /// How long to wait for that digit before screening the caller out. Also the deadline for
    /// <see cref="OutboundPin"/>.
    /// </summary>
    public int ScreeningTimeoutSeconds { get; init; } = 12;

    /// <summary>
    /// Optional PIN the Outbound (my cell) path must key in before it is answered and recorded. Blank
    /// means caller ID alone is enough.
    /// </summary>
    /// <remarks>
    /// Caller ID is trivially spoofable and this is the path that answers automatically and records, so
    /// without a PIN anyone willing to forge a From header can make this instance record them. Today
    /// that costs disk; once Phase 4 can place an outbound leg it costs a phone bill, which is why the
    /// setting exists before it is needed. Left blank by default so bringing the phase up does not
    /// require deciding this first — see TODO.md.
    /// </remarks>
    public string OutboundPin { get; init; } = "";

    /// <summary>
    /// How long received audio is held back so out-of-order RTP can be put right before it is written.
    /// Reordering only; nothing here is played out, so this costs latency in the file, not in the call.
    /// </summary>
    public int JitterBufferMilliseconds { get; init; } = 60;

    /// <summary>
    /// Interval between recording-notice tones, or 0 for none.
    /// </summary>
    /// <remarks>
    /// On the Outbound path the spoken notice only reaches the operator: the third party is merged in
    /// later by the mobile handset, and CallTree is never told it happened. A periodic tone is therefore
    /// the only disclosure this path can make mechanically to that party — everything else has to be
    /// said out loud by the operator. It is off by default because the wording, the mechanism, and
    /// whether one-party consent is even sufficient are open legal decisions for the operator; see the
    /// consent note in CLAUDE.md. The tone is sent, not received, so it does not appear in the recording.
    /// </remarks>
    public int RecordingToneIntervalSeconds { get; init; }

    /// <summary>
    /// Also listen for SIP over TCP on <see cref="SipListenPort"/>. Outbound registration stays on UDP;
    /// this only covers a trunk whose inbound transport is set to TCP, which would otherwise deliver to a
    /// closed port and leave no trace on our side.
    /// </summary>
    public bool ListenOnTcp { get; init; } = true;
}
