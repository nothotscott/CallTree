using System.Buffers;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace CallTree.Telephony.Audio;

/// <summary>What a finished recording turned out to be. Everything after the first two is diagnostics.</summary>
/// <param name="DurationSeconds">Audio written, from the RTP clock — not wall-clock call length.</param>
/// <param name="SizeBytes">Size of the finished file on disk, header included.</param>
/// <param name="SilenceSeconds">How much of the duration was filled in for packets that never arrived.</param>
/// <param name="LateFrames">Packets that turned up after their place in the file had already been written.</param>
/// <param name="Discontinuities">Timestamp jumps too large to be a pause; the stream was resynchronised instead.</param>
public readonly record struct RecordingOutcome(
    double DurationSeconds,
    long SizeBytes,
    double SilenceSeconds,
    int LateFrames,
    int Discontinuities);

/// <summary>
/// Writes one call's received audio to a mono 16-bit WAV.
/// </summary>
/// <remarks>
/// <para>
/// The RTP timestamp drives the file, not a wall clock. For PCMU the timestamp counts samples at 8 kHz,
/// so it states exactly where each packet belongs; writing packets back-to-back as they arrive would
/// instead silently compress every pause in the conversation and drift against the sender's clock over a
/// long call. Working from the timestamp means a gap is measurable, and gaps are filled with real silence
/// so the recording stays in sync with what was actually said.
/// </para>
/// <para>
/// Phase 5's stereo recording cannot reuse this directly: two legs have two unrelated RTP clocks, so
/// interleaving them needs a shared clock to align against. That is a different problem, deliberately not
/// solved here.
/// </para>
/// <para>
/// Packets arrive on SIPSorcery's RTP threads while <see cref="Close"/> is called from the call handler,
/// so everything touching the writer is under one lock. It is uncontended in the normal case: one packet
/// every 20 ms.
/// </para>
/// </remarks>
public sealed class CallRecorder : IDisposable
{
    /// <summary>
    /// Gaps longer than this are treated as a discontinuity — a clock reset, a stray packet from an old
    /// stream, or a re-INVITE — and resynchronised rather than filled. Without the cap a single bogus
    /// timestamp would ask for gigabytes of silence.
    /// </summary>
    private static readonly TimeSpan MaxSilenceFill = TimeSpan.FromSeconds(10);

    /// <summary>How much audio to accumulate between header updates. See <see cref="FlushIfDue"/>.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly WaveFileWriter _writer;
    private readonly RtpJitterBuffer _buffer;
    private readonly ILogger _logger;
    private readonly Guid _callId;
    private readonly uint _maxSilenceSamples;
    private readonly long _flushIntervalSamples;

    private uint _nextTimestamp;
    private bool _started;
    private long _samplesWritten;
    private long _silenceSamples;
    private long _samplesAtLastFlush;
    private int _lateFrames;
    private int _discontinuities;
    private bool _closed;
    private bool _faulted;
    private RecordingOutcome _outcome;

    public CallRecorder(Guid callId, string fullPath, TimeSpan jitterDepth, ILogger logger)
    {
        _callId = callId;
        _logger = logger;
        FullPath = fullPath;
        _buffer = new RtpJitterBuffer(jitterDepth);
        _maxSilenceSamples = (uint)(MaxSilenceFill.TotalSeconds * G711.ClockRate);
        _flushIntervalSamples = (long)(FlushInterval.TotalSeconds * G711.ClockRate);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new WaveFileWriter(fullPath, new WaveFormat(G711.ClockRate, 16, 1));
    }

    public string FullPath { get; }

    /// <summary>Audio written so far. Safe to read from another thread.</summary>
    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(_samplesWritten / (double)G711.ClockRate);
            }
        }
    }

    /// <summary>
    /// Offers one received RTP packet. Anything that is not PCMU is ignored — notably the RFC 4733
    /// telephone-event payload, which shares the stream and would be written as a burst of noise.
    /// </summary>
    public void Accept(int payloadType, uint timestamp, ushort sequenceNumber, byte[]? payload)
    {
        // Null-tolerant because this runs on SIPSorcery's receive loop: an exception thrown here does not
        // fail the recording, it stops the call taking delivery of any further RTP.
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

            _buffer.Add(new RtpAudioFrame(timestamp, sequenceNumber, payload));

            while (_buffer.TryDequeue(out var frame))
            {
                WriteFrame(frame);
            }

            FlushIfDue();
        }
    }

    /// <summary>
    /// Drains what is still buffered, finishes the file and reports what was written. Idempotent: the
    /// hangup and error paths both call it.
    /// </summary>
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
                while (_buffer.TryFlush(out var frame))
                {
                    WriteFrame(frame);
                }
            }

            try
            {
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call {CallId}: failed to close the recording at {Path}", _callId, FullPath);
            }

            var sizeBytes = 0L;
            try
            {
                var file = new FileInfo(FullPath);
                sizeBytes = file.Exists ? file.Length : 0;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Call {CallId}: could not measure the recording at {Path}", _callId, FullPath);
            }

            _outcome = new RecordingOutcome(
                DurationSeconds: Math.Round(_samplesWritten / (double)G711.ClockRate, 3),
                SizeBytes: sizeBytes,
                SilenceSeconds: Math.Round(_silenceSamples / (double)G711.ClockRate, 3),
                LateFrames: _lateFrames,
                Discontinuities: _discontinuities);

            return _outcome;
        }
    }

    public void Dispose() => Close();

    private void WriteFrame(RtpAudioFrame frame)
    {
        var samples = frame.Payload.Length;

        if (!_started)
        {
            _started = true;
            _nextTimestamp = frame.Timestamp;
        }

        var offset = unchecked((int)(frame.Timestamp - _nextTimestamp));

        if (offset < 0)
        {
            // Its place in the file has already been written past, and a WAV cannot be inserted into.
            // Dropping it loses 20 ms; rewinding would corrupt everything after it.
            _lateFrames++;
            return;
        }

        if (offset > 0)
        {
            if (offset > _maxSilenceSamples)
            {
                _discontinuities++;
                _logger.LogWarning(
                    "Call {CallId}: RTP timestamp jumped {Seconds:0.#}s (seq {Sequence}) - resynchronising rather than "
                    + "filling silence. The recording will be shorter than the call by that much.",
                    _callId,
                    offset / (double)G711.ClockRate,
                    frame.SequenceNumber);
            }
            else
            {
                WriteSilence(offset);
            }
        }

        var pcmBytes = samples * G711.BytesPerSample;
        var pcm = ArrayPool<byte>.Shared.Rent(pcmBytes);
        try
        {
            G711.Decode(frame.Payload, pcm.AsSpan(0, pcmBytes));
            _writer.Write(pcm, 0, pcmBytes);
            _samplesWritten += samples;
        }
        catch (Exception ex)
        {
            Fault(ex);
            return;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pcm);
        }

        _nextTimestamp = unchecked(frame.Timestamp + (uint)samples);
    }

    private void WriteSilence(int samples)
    {
        const int ChunkSamples = G711.ClockRate; // one second at a time

        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSamples * G711.BytesPerSample);
        try
        {
            // Rented arrays come back dirty, and "silence" written from stale audio is worse than noise.
            Array.Clear(chunk);

            var remaining = samples;
            while (remaining > 0)
            {
                var take = Math.Min(remaining, ChunkSamples);
                _writer.Write(chunk, 0, take * G711.BytesPerSample);
                remaining -= take;
            }

            _samplesWritten += samples;
            _silenceSamples += samples;
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>
    /// Periodically patches the RIFF sizes in the header, so a process killed mid-call leaves a file that
    /// still plays up to the last flush instead of one every tool reads as empty.
    /// </summary>
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

    /// <summary>
    /// Stops writing after an I/O failure. Carrying on would log once per packet, fifty times a second,
    /// and the call itself is still worth completing.
    /// </summary>
    private void Fault(Exception ex)
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        _logger.LogError(
            ex,
            "Call {CallId}: recording to {Path} failed after {Seconds:0.#}s; the call continues without it.",
            _callId,
            FullPath,
            _samplesWritten / (double)G711.ClockRate);
    }
}
