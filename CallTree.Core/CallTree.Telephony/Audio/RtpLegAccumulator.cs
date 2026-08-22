using Microsoft.Extensions.Logging;

namespace CallTree.Telephony.Audio;

/// <summary>
/// One leg's RTP reordering, gap-fill and discontinuity handling, decoding into a pending sample queue
/// rather than writing straight to a WAV. Shared by <see cref="CallRecorder"/> (an optional second leg,
/// summed into the primary's mono stream) and <see cref="BridgedCallRecorder"/> (two legs, kept on
/// separate stereo channels) - both need identical rules for what one independently-clocked RTP stream
/// means: a gap under <see cref="MaxSilenceFill"/> is filled with real silence, a jump over it is a
/// discontinuity and the stream resynchronises instead of asking for however much silence the jump implies,
/// and timestamp comparisons are signed so the 32-bit wrap does not reorder anything.
/// </summary>
internal sealed class RtpLegAccumulator(TimeSpan jitterDepth, string legName)
{
    private static readonly TimeSpan MaxSilenceFill = TimeSpan.FromSeconds(10);

    private readonly RtpJitterBuffer _buffer = new(jitterDepth);
    private readonly uint _maxSilenceSamples = (uint)(MaxSilenceFill.TotalSeconds * G711.ClockRate);

    private uint _nextTimestamp;
    private bool _started;

    public Queue<short> Pending { get; } = new();
    public long SilenceSamples { get; private set; }
    public int LateFrames { get; private set; }
    public int Discontinuities { get; private set; }

    public void Accept(RtpAudioFrame frame, ILogger logger, Guid callId)
    {
        _buffer.Add(frame);
        while (_buffer.TryDequeue(out var ready))
        {
            Enqueue(ready, logger, callId);
        }
    }

    /// <summary>Drains whatever the jitter buffer is still holding, regardless of depth. Call at leg end.</summary>
    public void Flush(ILogger logger, Guid callId)
    {
        while (_buffer.TryFlush(out var ready))
        {
            Enqueue(ready, logger, callId);
        }
    }

    private void Enqueue(RtpAudioFrame frame, ILogger logger, Guid callId)
    {
        if (!_started)
        {
            _started = true;
            _nextTimestamp = frame.Timestamp;
        }

        var offset = unchecked((int)(frame.Timestamp - _nextTimestamp));

        if (offset < 0)
        {
            LateFrames++;
            return;
        }

        if (offset > 0)
        {
            if (offset > _maxSilenceSamples)
            {
                Discontinuities++;
                logger.LogWarning(
                    "Call {CallId}: {Leg} leg RTP timestamp jumped {Seconds:0.#}s (seq {Sequence}) - "
                    + "resynchronising rather than filling silence.",
                    callId,
                    legName,
                    offset / (double)G711.ClockRate,
                    frame.SequenceNumber);
            }
            else
            {
                for (var i = 0; i < offset; i++)
                {
                    Pending.Enqueue(0);
                }

                SilenceSamples += offset;
            }
        }

        foreach (var encoded in frame.Payload)
        {
            Pending.Enqueue(G711.Decode(encoded));
        }

        _nextTimestamp = unchecked(frame.Timestamp + (uint)frame.Payload.Length);
    }
}
