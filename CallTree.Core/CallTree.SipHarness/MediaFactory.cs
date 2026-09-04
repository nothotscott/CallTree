using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace CallTree.SipHarness;

/// <summary>
/// Builds the media sessions both halves of the harness use.
/// </summary>
/// <remarks>
/// Deliberately a copy of what <c>TelephonyBackgroundService.CreateMediaSession</c> does, minus the NAT
/// rewriting - the harness and the instance under test are on the same machine, so the LAN address
/// SIPSorcery picks is the right one and there is nothing to correct. The PCMU restriction is the part
/// that matters and is not optional: left unrestricted, the offer carries G722 as well, and if the two
/// sides ever negotiated it the harness's mu-law tone would be encoded as something else and the
/// detector would score noise. Offering exactly what CallTree offers also means the SDP exchange under
/// test is the real one rather than a more permissive variant.
/// </remarks>
internal sealed class MediaFactory(int rtpPortStart, int rtpPortEnd, ILogger logger)
{
    private readonly PortRange _ports = new(rtpPortStart, rtpPortEnd);

    public (VoIPMediaSession Session, AudioExtrasSource Audio) Create(string label)
    {
        var audio = new AudioExtrasSource(
            new AudioEncoder(),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

        audio.RestrictFormats(format => format.Codec == AudioCodecsEnum.PCMU);

        var session = new VoIPMediaSession(
            new VoIPMediaSessionConfig
            {
                MediaEndPoint = new MediaEndPoints { AudioSource = audio },
                RtpPortRange = _ports,
            });

        // Same reason CallTree sets it: the far end's RTP can arrive from a port the SDP did not name,
        // and on loopback with a shared port range that happens routinely.
        session.AcceptRtpFromAny = true;

        logger.LogDebug("{Label}: media session created", label);
        return (session, audio);
    }
}
