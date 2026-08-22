using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace CallTree.Telephony.Audio;

/// <summary>
/// Relays one direction of a live RTP audio bridge at a fixed cadence, decoupling send timing from
/// receive timing.
/// </summary>
/// <remarks>
/// Forwarding a packet the instant it arrives only sounds smooth if the network delivers audio at a
/// perfectly steady rate, which it does not. Reordering alone (<see cref="RtpJitterBuffer"/>, the same
/// primitive the recording path uses) fixes correctness but not this: a burst of packets arriving close
/// together was still being relayed as a burst - sent back-to-back with no pacing between them - and a
/// receiving endpoint's own adaptive jitter buffer responds to that burstiness by growing its buffering
/// target. Buffers that grow quickly and shrink slowly are the norm, which is what a live call being
/// "choppy, with lag that gradually increases over the call" looks like from the outside, even though
/// nothing on our side is literally accumulating a growing backlog. Pacing the *send* side to a fixed
/// 20 ms tick, independent of how bursty arrival was, is what stops giving the far end a reason to keep
/// growing its own buffer.
/// </remarks>
internal sealed class PacedRtpRelay : IAsyncDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    private readonly Guid _callId;
    private readonly string _direction;
    private readonly ILogger _logger;
    private readonly NatAwareVoIPMediaSession _destination;
    private readonly RtpJitterBuffer _buffer;
    private readonly Lock _gate = new();
    private readonly PeriodicTimer _timer = new(FrameInterval);
    private readonly Task _pump;

    /// <param name="direction">A short label for log lines, e.g. "caller-&gt;mobile" - purely diagnostic.</param>
    public PacedRtpRelay(
        Guid callId, string direction, TimeSpan jitterDepth, NatAwareVoIPMediaSession destination, ILogger logger)
    {
        _callId = callId;
        _direction = direction;
        _logger = logger;
        _destination = destination;
        _buffer = new RtpJitterBuffer(jitterDepth);
        _pump = PumpAsync();
    }

    /// <summary>Offers one received RTP packet. Non-PCMU payloads (notably RFC 4733 DTMF) are dropped.</summary>
    public void Offer(int payloadType, uint timestamp, ushort sequenceNumber, byte[]? payload)
    {
        if (payloadType != G711.PcmuPayloadType || payload is not { Length: > 0 })
        {
            return;
        }

        lock (_gate)
        {
            _buffer.Add(new RtpAudioFrame(timestamp, sequenceNumber, payload));
        }
    }

    /// <summary>
    /// One frame out per tick, never more - this is the whole point. A tick with nothing ready to send
    /// (an underrun - the buffer genuinely has no reordered audio yet) is simply skipped rather than
    /// sending silence; PCMU tolerates gaps in the stream and the far end's own loss concealment handles
    /// it, same as with any two ordinary phones.
    /// </summary>
    private async Task PumpAsync()
    {
        while (await _timer.WaitForNextTickAsync())
        {
            RtpAudioFrame frame;
            bool has;
            lock (_gate)
            {
                has = _buffer.TryDequeue(out frame);
            }

            if (!has)
            {
                continue;
            }

            try
            {
                _destination.SendRtpRaw(SDPMediaTypesEnum.audio, frame.Payload, frame.Timestamp, 0, G711.PcmuPayloadType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call {CallId}: paced relay ({Direction}) failed to send a frame.", _callId, _direction);
            }
        }
    }

    /// <summary>
    /// Stops the pacing timer and waits for the pump loop to actually exit before returning, so the caller
    /// can safely close the destination media session immediately afterward. Disposing a
    /// <see cref="PeriodicTimer"/> makes a pending or future <c>WaitForNextTickAsync</c> return
    /// <see langword="false"/> rather than throw, which is what lets <see cref="PumpAsync"/>'s loop exit
    /// cleanly on its own.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        await _pump;
    }
}
