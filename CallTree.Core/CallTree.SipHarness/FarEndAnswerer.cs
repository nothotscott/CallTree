using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace CallTree.SipHarness;

/// <summary>
/// The other end of every leg CallTree places: the operator's mobile on the inbound bridge, and whoever
/// a <c>*{NUMBER}#</c> proxy dial reaches.
/// </summary>
/// <remarks>
/// <para>
/// It answers as whatever number the request URI asks for, without checking. That is what lets one
/// object stand in for the mobile and for an arbitrary proxy-dialled party at the same time, and it is
/// also what makes the pairing check honest: this side has no idea which caller it is supposed to be
/// joined to, so a correct pairing has to be established from the audio rather than assumed from the
/// bookkeeping.
/// </para>
/// <para>
/// It creates a fresh <see cref="SIPUserAgent"/> per INVITE, taken straight off the transport rather
/// than from a long-lived agent's OnIncomingCall. That is not a stylistic choice - a SIPUserAgent holds
/// exactly one dialogue in one field, so an agent that has answered a call will not raise
/// OnIncomingCall for a second one at all, and the INVITE would be dropped with no response and no log
/// line. The instance under test has the same bug (see the write-up), and a harness that shared it would
/// fail every concurrency test for its own reasons.
/// </para>
/// </remarks>
internal sealed class FarEndAnswerer : IAsyncDisposable
{
    private readonly SIPTransport _transport;
    private readonly HarnessOptions _options;
    private readonly MediaFactory _media;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentBag<LegAudit> _audits = [];
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private readonly List<Task> _legs = [];
    private readonly Lock _legsGate = new();

    private int _nextToneIndex;

    public FarEndAnswerer(
        SIPTransport transport,
        HarnessOptions options,
        MediaFactory media,
        int firstToneIndex,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _transport = transport;
        _options = options;
        _media = media;
        _nextToneIndex = firstToneIndex;
        _logger = logger;
        _cancellationToken = cancellationToken;

        _transport.SIPTransportRequestReceived += OnRequestAsync;
    }

    /// <summary>Never answer, just ring - the Missed scenario's whole content.</summary>
    public bool RefuseToAnswer { get; init; }

    public int Answered => _audits.Count(audit => audit.Answered);

    public IReadOnlyList<LegAudit> Audits => [.. _audits];

    /// <summary>Waits for every leg that was started to finish reporting.</summary>
    public async Task DrainAsync()
    {
        Task[] pending;
        lock (_legsGate)
        {
            pending = [.. _legs];
        }

        await Task.WhenAll(pending);
    }

    private Task OnRequestAsync(SIPEndPoint local, SIPEndPoint remote, SIPRequest request)
    {
        // In-dialog requests (ACK, BYE, a re-INVITE) belong to the per-call agent that owns that
        // dialogue; a To tag is what says the dialogue already exists. Only a fresh INVITE is ours.
        if (request.Method != SIPMethodsEnum.INVITE || request.Header.To?.ToTag is not null)
        {
            return Task.CompletedTask;
        }

        // The transport re-raises a retransmitted INVITE, and answering it twice would produce two
        // dialogues for one call.
        if (!_seen.TryAdd(request.Header.CallId, 0))
        {
            return Task.CompletedTask;
        }

        var leg = Task.Run(() => HandleAsync(request), CancellationToken.None);
        lock (_legsGate)
        {
            _legs.Add(leg);
        }

        return Task.CompletedTask;
    }

    private async Task HandleAsync(SIPRequest request)
    {
        var dialled = request.URI.User ?? "unknown";
        var toneHz = Tone.For(Interlocked.Increment(ref _nextToneIndex) - 1);
        var label = $"far end ({dialled})";

        var detector = new ToneDetector(Tone.Series(_options.ToneCount));
        var agent = new SIPUserAgent(_transport, null);
        var (session, audio) = _media.Create(label);

        // Two ways this leg can end, and they are not the same event. OnCallHungup is a BYE on an
        // established dialogue; ServerCallCancelled is a CANCEL on one that never got that far, which is
        // exactly what CallTree sends when its dial timeout expires on a mobile that never picked up. A
        // leg waiting only on the first sits there until its own timeout long after the call it belonged
        // to is over.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnCallHungup += _ => finished.TrySetResult();
        agent.ServerCallCancelled += (_, _) => finished.TrySetResult();

        void OnRtp(IPEndPoint peer, SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                detector.Accept(packet.Header.PayloadType, packet.Payload);
            }
        }

        session.OnRtpPacketReceived += OnRtp;

        TonePlayer? player = null;
        var answered = false;
        var outcome = "did not complete";
        string? error = null;
        var requestedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? answeredAt = null;

        try
        {
            _logger.LogInformation("{Label}: incoming leg from CallTree, Call-ID {CallId}", label, request.Header.CallId);

            // AcceptCall already sends 100 Trying and 180 Ringing, so the caller hears ringback for as
            // long as this waits - which is what makes an answer delay, and never answering at all,
            // meaningful rather than just slow.
            var uas = agent.AcceptCall(request);

            if (RefuseToAnswer)
            {
                _logger.LogInformation("{Label}: not answering - waiting for CallTree's dial timeout", label);

                var cancelled = await Task.WhenAny(
                    finished.Task, Task.Delay(TimeSpan.FromSeconds(90), _cancellationToken)) == finished.Task;

                outcome = cancelled
                    ? "left ringing; CallTree gave up on it"
                    : "left ringing, and CallTree never gave up - no dial timeout fired";
                return;
            }

            if (_options.AnswerDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.AnswerDelaySeconds), _cancellationToken);
            }

            answered = await agent.Answer(uas, session);
            if (!answered)
            {
                outcome = "answer failed (no SDP agreement)";
                return;
            }

            answeredAt = DateTimeOffset.UtcNow;
            outcome = "answered";
            _logger.LogInformation("{Label}: answered; playing {Tone} Hz", label, toneHz);

            player = new TonePlayer(label, new ToneSource(toneHz), session, audio, _logger);

            await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(300), _cancellationToken));
            outcome = finished.Task.IsCompleted ? "hung up by CallTree" : "still up when the run ended";
        }
        catch (OperationCanceledException)
        {
            outcome = "abandoned";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            outcome = "harness error";
            _logger.LogError(ex, "{Label}: failed", label);
        }
        finally
        {
            if (player is not null)
            {
                await player.DisposeAsync();
            }

            session.OnRtpPacketReceived -= OnRtp;
            await audio.CloseAudio();

            if (agent.IsCallActive)
            {
                agent.Hangup();
            }

            agent.Close();

            _audits.Add(new LegAudit
            {
                Role = LegRole.FarEnd,
                Label = label,
                SipCallId = request.Header.CallId,
                Number = dialled,
                PlayedHz = player is null ? null : toneHz,
                Answered = answered,
                RequestedAt = requestedAt,
                AnsweredAt = answeredAt,
                EndedAt = DateTimeOffset.UtcNow,
                FramesSent = player?.FramesSent ?? 0,
                Heard = detector.Result(),
                Outcome = outcome,
                Error = error,
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.SIPTransportRequestReceived -= OnRequestAsync;
        await DrainAsync();
    }
}
