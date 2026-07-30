namespace CallTree.Telephony.Configuration;

public sealed class TelephonyOptions
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

    /// <summary>Logs every SIP message sent and received. Noisy; for bring-up and NAT debugging.</summary>
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

    /// <summary>How long to wait for that digit before screening the caller out.</summary>
    public int ScreeningTimeoutSeconds { get; init; } = 12;

    /// <summary>
    /// Also listen for SIP over TCP on <see cref="SipListenPort"/>. Outbound registration stays on UDP;
    /// this only covers a trunk whose inbound transport is set to TCP, which would otherwise deliver to a
    /// closed port and leave no trace on our side.
    /// </summary>
    public bool ListenOnTcp { get; init; } = true;
}
