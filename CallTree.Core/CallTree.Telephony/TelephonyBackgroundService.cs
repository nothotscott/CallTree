using CallTree.Telephony.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTree.Telephony;

/// <summary>
/// Hosts the SIP user agent for the lifetime of the process.
/// Phase 0 stub: validates and reports configuration only. SIP registration lands in Phase 1.
/// </summary>
public class TelephonyBackgroundService(
    IOptions<TrunkOptions> trunkOptions,
    IOptions<TelephonyOptions> telephonyOptions,
    ILogger<TelephonyBackgroundService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var trunk = trunkOptions.Value;
        var telephony = telephonyOptions.Value;

        if (trunk.IsConfigured)
        {
            logger.LogInformation(
                "Telephony configured: trunk {Username}@{Host}:{Port}, SIP listen port {SipPort}, RTP {RtpStart}-{RtpEnd}",
                trunk.Username, trunk.Host, trunk.Port,
                telephony.SipListenPort, telephony.RtpPortStart, telephony.RtpPortEnd);
        }
        else
        {
            logger.LogWarning("Trunk is not configured (Trunk:Host / Trunk:Username missing) — telephony is idle.");
        }

        if (telephony.MyCellNumber.Length == 0)
        {
            logger.LogWarning("Telephony:MyCellNumber is not set — outbound-source calls cannot be classified.");
        }

        return Task.CompletedTask;
    }
}
