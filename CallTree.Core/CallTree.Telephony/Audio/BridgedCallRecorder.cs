using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CallTree.Telephony.Audio;

/// <summary>Which leg a packet offered to <see cref="BridgedCallRecorder"/> belongs to.</summary>
public enum RecordingChannel
{
    /// <summary>The inbound caller. Written to the left channel.</summary>
    Caller,

    /// <summary>The bridged leg to the configured mobile. Written to the right channel.</summary>
    Mobile,
}

/// <summary>
/// Writes a bridged call's two legs to one stereo 16-bit WAV (left = caller, right = mobile), matching
/// <see cref="ChannelLayout.StereoPerLeg"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CallRecorder"/>'s primary leg cannot drive this by itself: its file position is driven by
/// one leg's own RTP timestamp, and a bridge has two legs with two unrelated RTP clocks - there is nothing
/// to align them to. This class gives each leg its own <see cref="RtpLegAccumulator"/> (reordering and
/// silence-fill: a gap is filled, a jump over ten seconds is a discontinuity and is resynchronised
/// instead), each accumulating decoded samples into its own queue rather than writing straight to the WAV.
/// The shared wall clock is wherever packet arrival on *either* leg drives a drain of both queues together
/// - not either leg's RTP clock directly. A leg with nothing queued is understood to still be advancing in
/// real time, because its own silence-fill keeps its queue topped up during a gap on that leg alone.
/// </para>
/// <para>
/// Like <see cref="CallRecorder"/>, the header is re-patched periodically so a process killed mid-call
/// leaves a file that still plays up to the last flush rather than one every tool reads as empty.
/// </para>
/// </remarks>
public sealed class BridgedCallRecorder : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly WaveFileWriter _writer;
    private readonly ILogger _logger;
    private readonly Guid _callId;
    private readonly RtpLegAccumulator _caller;
    private readonly RtpLegAccumulator _mobile;
    private readonly long _flushIntervalSamples;
    private readonly short[] _frameBuffer = new short[2];

    private long _samplesWritten;
    private long _samplesAtLastFlush;
    private bool _closed;
    private bool _faulted;
    private RecordingOutcome _outcome;

    public BridgedCallRecorder(Guid callId, string fullPath, TimeSpan jitterDepth, ILogger logger)
    {
        _callId = callId;
        _logger = logger;
        FullPath = fullPath;
        _caller = new RtpLegAccumulator(jitterDepth, "caller");
        _mobile = new RtpLegAccumulator(jitterDepth, "mobile");
        _flushIntervalSamples = (long)(FlushInterval.TotalSeconds * G711.ClockRate);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new WaveFileWriter(fullPath, new WaveFormat(G711.ClockRate, 16, 2));
    }

    public string FullPath { get; }

    /// <summary>Offers one received RTP packet from either leg. Non-PCMU payloads are ignored.</summary>
    public void Accept(RecordingChannel channel, int payloadType, uint timestamp, ushort sequenceNumber, byte[]? payload)
    {
        // Null-tolerant because this runs on SIPSorcery's receive loop: an exception here must not take
        // down the bridge, only the recording.
        if (payloadType != G711.PcmuPayloadType || payload is not { Length: > 0 })
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _faulted)
            {
                return;
            }

            var leg = channel == RecordingChannel.Caller ? _caller : _mobile;
            leg.Accept(new RtpAudioFrame(timestamp, sequenceNumber, payload), _logger, _callId);

            try
            {
                Drain(requireBoth: true);
                FlushIfDue();
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }
    }

    /// <summary>Drains what is still buffered, finishes the file and reports what was written.</summary>
    public RecordingOutcome Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return _outcome;
            }

            _closed = true;

            if (!_faulted)
            {
                try
                {
                    _caller.Flush(_logger, _callId);
                    _mobile.Flush(_logger, _callId);
                    Drain(requireBoth: true);

                    // Whichever leg still has a residual tail (its jitter buffer held more frames than the
                    // other's at the moment both were flushed) is padded with silence rather than
                    // truncated - a stereo WAV cannot have mismatched channel lengths.
                    Drain(requireBoth: false);
                }
                catch (Exception ex)
                {
                    Fault(ex);
                }
            }

            try
            {
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call {CallId}: failed to close the bridged recording at {Path}", _callId, FullPath);
            }

            var sizeBytes = 0L;
            try
            {
                var file = new FileInfo(FullPath);
                sizeBytes = file.Exists ? file.Length : 0;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Call {CallId}: could not measure the bridged recording at {Path}", _callId, FullPath);
            }

            _outcome = new RecordingOutcome(
                DurationSeconds: Math.Round(_samplesWritten / (double)G711.ClockRate, 3),
                SizeBytes: sizeBytes,
                SilenceSeconds: Math.Round((_caller.SilenceSamples + _mobile.SilenceSamples) / (double)G711.ClockRate, 3),
                LateFrames: _caller.LateFrames + _mobile.LateFrames,
                Discontinuities: _caller.Discontinuities + _mobile.Discontinuities);

            return _outcome;
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// Writes interleaved frames while both queues have one, or (<paramref name="requireBoth"/> false, used
    /// only when closing) drains whichever queue still has a tail, padding the other with silence.
    /// </summary>
    private void Drain(bool requireBoth)
    {
        var frame = _frameBuffer;

        while (true)
        {
            var callerHas = _caller.Pending.Count > 0;
            var mobileHas = _mobile.Pending.Count > 0;

            if (requireBoth ? !(callerHas && mobileHas) : !(callerHas || mobileHas))
            {
                return;
            }

            frame[0] = callerHas ? _caller.Pending.Dequeue() : (short)0;
            frame[1] = mobileHas ? _mobile.Pending.Dequeue() : (short)0;

            _writer.WriteSamples(frame, 0, 2);
            _samplesWritten++;
        }
    }

    private void FlushIfDue()
    {
        if (_samplesWritten - _samplesAtLastFlush < _flushIntervalSamples)
        {
            return;
        }

        _samplesAtLastFlush = _samplesWritten;
        try
        {
            _writer.Flush();
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private void Fault(Exception ex)
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        _logger.LogError(
            ex,
            "Call {CallId}: bridged recording to {Path} failed after {Seconds:0.#}s; the call continues without it.",
            _callId,
            FullPath,
            _samplesWritten / (double)G711.ClockRate);
    }
}
