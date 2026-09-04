using System.Net;
using CallTree.Telephony.Audio;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace CallTree.SipHarness;

/// <summary>
/// One phone calling the DID: places the INVITE, keys in whatever DTMF the scenario asks for, plays its
/// tone, listens to what comes back, and hangs up.
/// </summary>
/// <remarks>
/// A real SIP client, not a stub. It negotiates SDP with CallTree, sends RFC 4733 DTMF that the real
/// screening gate has to debounce, and puts real mu-law frames on the wire that the real recorder has to
/// reorder and write. The only thing about it that is fake is the caller ID, which is the entire point:
/// CallTree classifies a call by comparing the From user against Telephony:MyCellNumber, so the number
/// this leg claims to be is what decides whether it takes the Outbound path or faces the spam gate.
/// </remarks>
internal sealed class SimulatedCaller(
    SIPTransport transport,
    HarnessOptions options,
    MediaFactory media,
    string callerId,
    int toneHz,
    string label,
    ILogger logger)
{
    public async Task<LegAudit> RunAsync(CancellationToken cancellationToken)
    {
        var detector = new ToneDetector(Tone.Series(options.ToneCount));
        var agent = new SIPUserAgent(transport, null);
        var (session, audio) = media.Create(label);

        var destination = $"sip:{options.Did.TrimStart('+')}@{options.Host}:{options.Port}";
        var from = $"<sip:{callerId.TrimStart('+')}@{options.Host}>";

        // Built by hand for the same reason CallTree builds its own: the string-destination overload of
        // Call() leaves From unset, and From is the only thing carrying the caller ID this whole test
        // turns on.
        var descriptor = new SIPCallDescriptor(
            username: "harness",
            password: null,
            uri: destination,
            from: from,
            to: null,
            routeSet: null,
            customHeaders: null,
            authUsername: null,
            callDirection: SIPCallDirection.Out,
            contentType: null,
            content: null,
            mangleIPAddress: null);

        var hungUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnCallHungup += _ => hungUp.TrySetResult();

        void OnRtp(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                detector.Accept(packet.Header.PayloadType, packet.Payload);
            }
        }

        session.OnRtpPacketReceived += OnRtp;

        TonePlayer? player = null;
        var outcome = "did not complete";
        string? error = null;
        var answered = false;
        var requestedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? answeredAt = null;

        try
        {
            logger.LogInformation("{Label}: calling {Destination} as {From}", label, destination, callerId);

            requestedAt = DateTimeOffset.UtcNow;
            answered = await agent.Call(descriptor, session, options.DurationSeconds + 30);

            if (!answered)
            {
                // A refusal is a legitimate result, not a harness failure: the DID filter answers 404 to
                // an INVITE for the wrong number, and the loopback guard answers 403 to one from off-box.
                // Both are things worth being able to test on purpose.
                outcome = "not answered (rejected, cancelled, or no SDP agreement)";
                return Audit(answered, player, detector, outcome, error);
            }

            answeredAt = DateTimeOffset.UtcNow;
            outcome = "answered";
            logger.LogInformation(
                "{Label}: answered after {Elapsed:0.0}s; playing {Tone} Hz",
                label, (answeredAt.Value - requestedAt).TotalSeconds, toneHz);

            // DTMF first, tone second. RFC 4733 events share the stream with audio, and sending both at
            // once means two timestamp bases on one SSRC - the same collision
            // PromptPlayer.SuspendBackgroundSilence exists to avoid. The wait ahead of it is the answer/
            // gate race described on DtmfDelaySeconds.
            await DelayAsync(TimeSpan.FromSeconds(options.DtmfDelaySeconds), hungUp.Task, cancellationToken);
            await SendDigitsAsync(agent, options.Digits, cancellationToken);

            player = new TonePlayer(label, new ToneSource(toneHz), session, audio, logger);

            if (options.Scenario == Scenario.Outbound && options.ProxyNumber is { Length: > 0 } proxy)
            {
                // Long enough for the recording reminder to finish and the DTMF collector to exist -
                // see ProxyDelaySeconds. Also leaves the recording several seconds of plain one-party
                // audio before a second leg is mixed into it.
                await DelayAsync(TimeSpan.FromSeconds(options.ProxyDelaySeconds), hungUp.Task, cancellationToken);

                logger.LogInformation("{Label}: dialing proxy *{Proxy}#", label, proxy);
                player.Mute();
                await SendDigitsAsync(agent, $"*{proxy.TrimStart('+')}#", cancellationToken);
                player.Unmute();
            }

            var finished = await DelayAsync(
                TimeSpan.FromSeconds(options.DurationSeconds), hungUp.Task, cancellationToken);

            outcome = finished ? "hung up by CallTree" : "held for the full duration";
        }
        catch (OperationCanceledException)
        {
            outcome = "abandoned";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            outcome = "harness error";
            logger.LogError(ex, "{Label}: failed", label);
        }
        finally
        {
            if (player is not null)
            {
                await player.DisposeAsync();
            }

            session.OnRtpPacketReceived -= OnRtp;

            // Close the audio source before the dialogue goes, or its own timer fires once more against
            // a closed RTP session - the same warning CallTree suppresses on every teardown path.
            await audio.CloseAudio();

            if (agent.IsCallActive)
            {
                agent.Hangup();
            }

            // Close(), not just Hangup(): every SIPUserAgent subscribes to the transport's request event
            // in its constructor and only unsubscribes here. A harness that runs a hundred calls without
            // closing them leaves a hundred handlers inspecting every packet.
            agent.Close();
        }

        return Audit(answered, player, detector, outcome, error);

        LegAudit Audit(bool wasAnswered, TonePlayer? tonePlayer, ToneDetector heard, string how, string? failure) =>
            new()
            {
                Role = LegRole.Caller,
                Label = label,
                SipCallId = descriptor.CallId ?? "-",
                Number = callerId,
                PlayedHz = tonePlayer is null ? null : toneHz,
                Answered = wasAnswered,
                RequestedAt = requestedAt,
                AnsweredAt = answeredAt,
                EndedAt = DateTimeOffset.UtcNow,
                FramesSent = tonePlayer?.FramesSent ?? 0,
                Heard = heard.Result(),
                Outcome = how,
                Error = failure,
            };
    }

    /// <summary>
    /// Keys in a DTMF string one digit at a time, with a human-sized gap between them.
    /// </summary>
    /// <remarks>
    /// The gap is what makes multi-digit entry testable at all. CallTree's PinGate treats a repeat of the
    /// same digit inside 250 ms as one keypress, because RFC 4733 retransmits the end of an event three
    /// times and those arrive a packet interval apart. Sending digits back to back here would collapse
    /// "1112" into "12" exactly as a badly written gate would, and the harness would be reproducing the
    /// bug rather than testing for it.
    /// </remarks>
    private static async Task SendDigitsAsync(SIPUserAgent agent, string digits, CancellationToken cancellationToken)
    {
        foreach (var character in digits)
        {
            var tone = character switch
            {
                >= '0' and <= '9' => (byte)(character - '0'),
                '*' => (byte)10,
                '#' => (byte)11,
                _ => byte.MaxValue,
            };

            if (tone == byte.MaxValue)
            {
                continue;
            }

            await agent.SendDtmf(tone);
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    /// <summary>Waits out a span, returning true if the far end hung up first.</summary>
    private static async Task<bool> DelayAsync(TimeSpan span, Task hungUp, CancellationToken cancellationToken)
    {
        var elapsed = Task.Delay(span, cancellationToken);
        return await Task.WhenAny(elapsed, hungUp) == hungUp;
    }
}
