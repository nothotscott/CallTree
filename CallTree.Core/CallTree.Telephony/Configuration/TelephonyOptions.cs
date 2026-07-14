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
}
