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
/// Writes one Outbound-source call's audio to a mono 16-bit WAV: the primary (operator's own) leg for the
/// whole call, plus an optional second leg that can be attached and detached any number of times during
/// the call — the DID-initiated outbound proxy dial (<c>*{NUMBER}#</c>). While attached, both legs are
/// decoded, reordered and silence-filled independently (see <see cref="RtpLegAccumulator"/>, which also
/// backs <see cref="BridgedCallRecorder"/>), then summed sample-for-sample into the same continuous mono
/// stream — clamped rather than wrapped, since two speakers rarely peak at once but occasionally do.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately mono and deliberately one continuous file, matching the native-handset-merge case this
/// path already handles: the operator's phone carrier mixes a merged-in party's audio before it ever
/// reaches CallTree, so a single leg already carries both voices there. A DID-initiated proxy leg is
/// different - it is a second, CallTree-controlled RTP stream, not something the carrier mixes for us -
/// but the recording this class produces should look the same either way: one file, one channel, for the
/// whole call, regardless of how many times a proxy leg comes and goes during it.
/// </para>
/// <para>
/// With no secondary leg attached this behaves exactly as it always has: each packet is decoded and
/// written as soon as it is reordered into place, so the file is durable on disk immediately rather than
/// only at the next flush. With one attached, writing waits for a sample from *both* legs before it can
/// go out - packet arrival on either leg is what drives the shared position, the same principle
/// <see cref="BridgedCallRecorder"/> uses for its two permanently-separate channels. A leg with nothing
/// queued is still understood to be advancing in real time, because its own silence-fill keeps it topped
/// up during a gap on that leg alone.
/// </para>
/// <para>
/// The header is re-patched periodically so a process killed mid-call leaves a file that still plays up to
/// the last flush instead of one every tool reads as empty.
/// </para>
/// </remarks>
public sealed class CallRecorder : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly WaveFileWriter _writer;
    private readonly ILogger _logger;
    private readonly Guid _callId;
    private readonly TimeSpan _jitterDepth;
    private readonly RtpLegAccumulator _primary;
    private readonly long _flushIntervalSamples;
    private readonly short[] _sampleBuffer = new short[1];

    private RtpLegAccumulator? _secondary;
    private long _secondarySilenceSamples;
    private int _secondaryLateFrames;
    private int _secondaryDiscontinuities;

    private long _samplesWritten;
    private long _samplesAtLastFlush;
    private bool _closed;
    private bool _faulted;
    private RecordingOutcome _outcome;

    public CallRecorder(Guid callId, string fullPath, TimeSpan jitterDepth, ILogger logger)
    {
        _callId = callId;
        _logger = logger;
        _jitterDepth = jitterDepth;
        FullPath = fullPath;
        _primary = new RtpLegAccumulator(jitterDepth, "primary");
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
    /// Offers one received RTP packet from the primary (operator's own) leg. Anything that is not PCMU is
    /// ignored — notably the RFC 4733 telephone-event payload, which shares the stream and would be
    /// written as a burst of noise.
    /// </summary>
    public void Accept(int payloadType, uint timestamp, ushort sequenceNumber, byte[]? payload)
    {
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

            _primary.Accept(new RtpAudioFrame(timestamp, sequenceNumber, payload), _logger, _callId);

            try
            {
                Drain();
                FlushIfDue();
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }
    }

    /// <summary>Offers one received RTP packet from the attached secondary (proxy-dialed) leg, if any.</summary>
    public void AcceptSecondary(int payloadType, uint timestamp, ushort sequenceNumber, byte[]? payload)
    {
        if (payloadType != G711.PcmuPayloadType || payload is not { Length: > 0 })
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _faulted || _secondary is null)
            {
                return;
            }

            _secondary.Accept(new RtpAudioFrame(timestamp, sequenceNumber, payload), _logger, _callId);

            try
            {
                Drain();
                FlushIfDue();
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }
    }

    /// <summary>
    /// Starts mixing a second leg in - the proxy-dialed party has answered. A fresh
    /// <see cref="RtpLegAccumulator"/> each time, since every proxy call is an unrelated RTP stream with
    /// its own clock origin.
    /// </summary>
    public void AttachSecondaryLeg()
    {
        lock (_gate)
        {
            if (_secondary is not null)
            {
                _logger.LogWarning("Call {CallId}: a secondary leg is already attached; ignoring.", _callId);
                return;
            }

            _secondary = new RtpLegAccumulator(_jitterDepth, "proxy");
        }
    }

    /// <summary>
    /// Stops mixing the second leg - the proxy-dialed party hung up. Drains whatever it was still holding
    /// (summed with the primary where one is available, alone otherwise) before dropping it, so its tail
    /// isn't lost; any primary backlog left over stays queued and drains normally now that mixing has
    /// stopped. Safe to call again later if the operator places another proxy dial in the same call.
    /// </summary>
    public void DetachSecondaryLeg()
    {
        lock (_gate)
        {
            if (_secondary is null)
            {
                return;
            }

            try
            {
                DrainSecondaryTail();
            }
            catch (Exception ex)
            {
                Fault(ex);
            }

            _secondary = null;
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
                try
                {
                    _primary.Flush(_logger, _callId);
                    DrainSecondaryTail();

                    while (_primary.Pending.Count > 0)
                    {
                        WriteSample(_primary.Pending.Dequeue());
                    }
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
                SilenceSeconds: Math.Round((_primary.SilenceSamples + _secondarySilenceSamples) / (double)G711.ClockRate, 3),
                LateFrames: _primary.LateFrames + _secondaryLateFrames,
                Discontinuities: _primary.Discontinuities + _secondaryDiscontinuities);

            return _outcome;
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// With no secondary attached, drains the primary alone as soon as it arrives - unchanged from before
    /// this class could mix a second leg in. With one attached, only writes once both legs have a sample
    /// for this position, so packet arrival on either leg is what advances the file.
    /// </summary>
    private void Drain()
    {
        if (_secondary is null)
        {
            while (_primary.Pending.Count > 0)
            {
                WriteSample(_primary.Pending.Dequeue());
            }
            return;
        }

        while (_primary.Pending.Count > 0 && _secondary.Pending.Count > 0)
        {
            WriteMixedSample();
        }
    }

    /// <summary>
    /// Flushes the secondary's jitter buffer, pairs off whatever it can against the primary, then writes
    /// any secondary tail alone (there is no primary sample left to pair it with, but the audio itself
    /// must not be dropped). Folds the secondary's diagnostics into the running totals first, since it is
    /// about to be replaced or cleared. A no-op if nothing is attached.
    /// </summary>
    private void DrainSecondaryTail()
    {
        if (_secondary is null)
        {
            return;
        }

        _secondary.Flush(_logger, _callId);

        while (_primary.Pending.Count > 0 && _secondary.Pending.Count > 0)
        {
            WriteMixedSample();
        }

        while (_secondary.Pending.Count > 0)
        {
            WriteSample(_secondary.Pending.Dequeue());
        }

        _secondarySilenceSamples += _secondary.SilenceSamples;
        _secondaryLateFrames += _secondary.LateFrames;
        _secondaryDiscontinuities += _secondary.Discontinuities;
    }

    private void WriteMixedSample()
    {
        var primary = _primary.Pending.Dequeue();
        var secondary = _secondary!.Pending.Dequeue();
        WriteSample((short)Math.Clamp(primary + secondary, short.MinValue, short.MaxValue));
    }

    private void WriteSample(short value)
    {
        _sampleBuffer[0] = value;
        _writer.WriteSamples(_sampleBuffer, 0, 1);
        _samplesWritten++;
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
