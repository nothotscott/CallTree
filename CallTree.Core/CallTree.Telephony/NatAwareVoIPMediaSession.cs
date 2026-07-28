using System.Net;
using SIPSorcery.Media;
using SIPSorcery.Net;

namespace CallTree.Telephony;

/// <summary>
/// A <see cref="VoIPMediaSession"/> that advertises our public address in the SDP.
/// </summary>
/// <remarks>
/// <see cref="SIPUserAgent.Answer"/> takes a <c>publicIpAddress</c> argument, but it is only a
/// *fallback*: <c>RTPSession.GetSdpConnectionAddress</c> prefers the local address that routes to the
/// offer's connection address, and only falls back to the supplied address when the offer carries none.
/// A trunk always sends a connection address, so the argument never wins and the SDP goes out advertising
/// the LAN address. The trunk then streams RTP to an unroutable destination and the call has no audio.
/// Rewriting the answer after the base class has built it is the reliable fix.
/// </remarks>
internal sealed class NatAwareVoIPMediaSession(VoIPMediaSessionConfig config, IPAddress? publicAddress)
    : VoIPMediaSession(config)
{
    public override SDP CreateAnswer(IPAddress? connectionAddress) =>
        AdvertisePublicAddress(base.CreateAnswer(connectionAddress));

    public override SDP CreateOffer(IPAddress? connectionAddress = null) =>
        AdvertisePublicAddress(base.CreateOffer(connectionAddress));

    private SDP AdvertisePublicAddress(SDP sdp)
    {
        if (publicAddress is null || sdp is null)
        {
            return sdp!;
        }

        var address = publicAddress.ToString();

        sdp.AddressOrHost = address;

        if (sdp.Connection is not null)
        {
            sdp.Connection.ConnectionAddress = address;
        }

        // Media-level connection lines override the session-level one, so they need it too.
        foreach (var announcement in sdp.Media)
        {
            if (announcement.Connection is not null)
            {
                announcement.Connection.ConnectionAddress = address;
            }
        }

        return sdp;
    }
}
