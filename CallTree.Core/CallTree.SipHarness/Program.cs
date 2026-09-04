using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace CallTree.SipHarness;

/// <summary>
/// Drives a spoofing-mode CallTree instance over real SIP and real RTP, and says whether what came back
/// was what should have come back.
/// </summary>
/// <remarks>
/// Nothing here is a mock. The harness registers no trunk and needs no provider, but everything between
/// its socket and CallTree's is the genuine article: SDP negotiation, RFC 4733 DTMF, mu-law frames on a
/// 20 ms cadence, BYE at the end. What it adds over placing a real phone call is that every leg it owns
/// carries a distinct tone, so "the audio arrived" can be checked as "<em>whose</em> audio arrived" -
/// which is the only question that becomes interesting once more than one call is up at a time.
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!HarnessOptions.TryParse(args, out var options, out var error))
        {
            if (error.Length > 0)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
            }

            Console.Error.WriteLine(HarnessOptions.Usage);
            return error.Length > 0 ? 2 : 0;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss.fff ";
            })
            .SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information)
            // SIPSorcery is chatty at Information even when nothing is wrong, and its noise buries the
            // harness's own lines - which are the ones that say what the run proved.
            .AddFilter("SIPSorcery", options.Verbose ? LogLevel.Debug : LogLevel.Warning));

        var logger = loggerFactory.CreateLogger("harness");
        SIPSorcery.LogFactory.Set(loggerFactory);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var transport = new SIPTransport();
        transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, options.ListenPort)));

        var media = new MediaFactory(options.RtpPortStart, options.RtpPortEnd, logger);

        logger.LogInformation(
            "Harness listening on {Listen}; calling {Target} as {Scenario}; {Calls} concurrent call(s)",
            transport.GetSIPChannels()[0].ListeningSIPEndPoint,
            $"{options.Host}:{options.Port}",
            options.Scenario,
            options.Calls);

        // Recorded before the first INVITE so the sweep afterwards picks up every file this run caused.
        // Barely back-dated at all: a second's slack is enough to sweep up the recording the *previous*
        // run left behind, and a file from another run is worse than no file - it turns a scenario that
        // is supposed to record nothing into one that appears to have recorded something.
        var startedAt = DateTime.UtcNow.AddMilliseconds(-50);

        // Far-end tones start past the block the callers use, so the two sets never overlap and a tone
        // identifies a leg uniquely across the whole run.
        await using var farEnd = new FarEndAnswerer(
            transport, options, media, options.Calls, logger, cancellation.Token)
        {
            RefuseToAnswer = options.Scenario == Scenario.Missed,
        };

        var callerId = options.Scenario == Scenario.Outbound ? options.Cell : options.Stranger;

        var callers = await Task.WhenAll(Enumerable.Range(0, options.Calls).Select(index =>
            new SimulatedCaller(
                    transport,
                    options,
                    media,
                    callerId,
                    Tone.For(index),
                    $"caller {index + 1}",
                    logger)
                .RunAsync(cancellation.Token)));

        // The far end can outlive its caller by a moment - CallTree hangs the mobile up after the caller
        // does - so its legs are drained rather than assumed finished.
        await farEnd.DrainAsync();

        var recordings = options.RecordingsRoot is { } root
            ? RecordingCheck.Since(root, startedAt, Tone.Series(options.ToneCount))
            : [];

        transport.Shutdown();

        return Report.Print(options, callers, farEnd.Audits, recordings) ? 0 : 1;
    }
}
