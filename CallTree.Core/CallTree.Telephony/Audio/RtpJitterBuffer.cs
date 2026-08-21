namespace CallTree.Telephony.Audio;

/// <summary>One received audio packet, reduced to the three things the recorder cares about.</summary>
/// <param name="Timestamp">
/// RTP timestamp. For PCMU this counts samples at 8 kHz, so it is the sender's own sample clock and the
/// only reliable way to know where a packet belongs in the recording.
/// </param>
/// <param name="SequenceNumber">Kept for logging and duplicate spotting; ordering is done on the timestamp.</param>
/// <param name="Payload">Undecoded mu-law bytes.</param>
public readonly record struct RtpAudioFrame(uint Timestamp, ushort SequenceNumber, byte[] Payload);

/// <summary>
/// Holds arriving RTP audio briefly so packets that overtook each other in the network can be put back
/// in order before anything is written.
/// </summary>
/// <remarks>
/// This is a reordering buffer, not a playout buffer. A softphone needs a playout clock because it has a
/// speaker to feed at a fixed rate; a recorder does not — the file has no deadline, and the RTP timestamp
/// already says exactly where every packet belongs. So the release rule is depth-based rather than
/// time-based: a frame is handed on once a frame at least <c>depth</c> newer has arrived, which is the
/// point after which nothing earlier can still be expected.
///
/// Timestamps are compared as signed differences so the arithmetic survives the 32-bit wrap (about six
/// days of continuous audio at 8 kHz — irrelevant in practice, wrong to get wrong).
/// </remarks>
public sealed class RtpJitterBuffer(TimeSpan depth, int clockRate = G711.ClockRate)
{
    private readonly uint _depthSamples = (uint)Math.Max(0, depth.TotalSeconds * clockRate);
    private readonly List<RtpAudioFrame> _frames = [];

    private uint _newestTimestamp;
    private bool _hasFrames;

    /// <summary>How many frames are being held right now.</summary>
    public int Count => _frames.Count;

    /// <summary>The configured depth, in samples of the RTP clock.</summary>
    public uint DepthSamples => _depthSamples;

    public void Add(RtpAudioFrame frame)
    {
        if (!_hasFrames || IsLater(frame.Timestamp, _newestTimestamp))
        {
            _newestTimestamp = frame.Timestamp;
            _hasFrames = true;
        }

        // Insertion from the back: in-order arrival is the overwhelmingly common case, so this is a
        // single comparison per packet and the list never grows past a few frames anyway.
        var index = _frames.Count;
        while (index > 0 && IsLater(_frames[index - 1].Timestamp, frame.Timestamp))
        {
            index--;
        }

        _frames.Insert(index, frame);
    }

    /// <summary>Releases the oldest frame, if the buffer has filled to its depth.</summary>
    public bool TryDequeue(out RtpAudioFrame frame) => TryTake(requireDepth: true, out frame);

    /// <summary>
    /// Releases the oldest frame regardless of depth. Used at end of call, when nothing further can
    /// arrive and holding audio back would just truncate the recording.
    /// </summary>
    public bool TryFlush(out RtpAudioFrame frame) => TryTake(requireDepth: false, out frame);

    private bool TryTake(bool requireDepth, out RtpAudioFrame frame)
    {
        if (_frames.Count == 0)
        {
            frame = default;
            return false;
        }

        if (requireDepth && unchecked(_newestTimestamp - _frames[0].Timestamp) < _depthSamples)
        {
            frame = default;
            return false;
        }

        frame = _frames[0];
        _frames.RemoveAt(0);
        return true;
    }

    /// <summary>Whether <paramref name="a"/> is later than <paramref name="b"/>, wrap-safe.</summary>
    private static bool IsLater(uint a, uint b) => unchecked((int)(a - b)) > 0;
}
