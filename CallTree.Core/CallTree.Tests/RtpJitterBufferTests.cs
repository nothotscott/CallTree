using CallTree.Telephony.Audio;
using Xunit;

namespace CallTree.Tests;

public class RtpJitterBufferTests
{
    /// <summary>One 20 ms PCMU packet: 160 samples, one byte each.</summary>
    private const int FrameSamples = 160;

    private static readonly TimeSpan Depth = TimeSpan.FromMilliseconds(60);

    private static RtpAudioFrame Frame(int index) =>
        new((uint)(index * FrameSamples), (ushort)index, new byte[FrameSamples]);

    private static List<ushort> DrainSequence(RtpJitterBuffer buffer)
    {
        var released = new List<ushort>();
        while (buffer.TryDequeue(out var frame))
        {
            released.Add(frame.SequenceNumber);
        }

        return released;
    }

    private static List<ushort> FlushSequence(RtpJitterBuffer buffer)
    {
        var released = new List<ushort>();
        while (buffer.TryFlush(out var frame))
        {
            released.Add(frame.SequenceNumber);
        }

        return released;
    }

    [Fact]
    public void Depth_is_measured_in_samples_of_the_rtp_clock()
    {
        Assert.Equal(480u, new RtpJitterBuffer(Depth).DepthSamples); // 60 ms at 8 kHz
    }

    [Fact]
    public void Nothing_is_released_until_the_buffer_reaches_its_depth()
    {
        var buffer = new RtpJitterBuffer(Depth);

        buffer.Add(Frame(0));
        buffer.Add(Frame(1));
        Assert.Empty(DrainSequence(buffer));

        // The fourth packet puts 60 ms between the newest arrival and the oldest held frame.
        buffer.Add(Frame(2));
        buffer.Add(Frame(3));

        Assert.Equal<ushort[]>([0], [.. DrainSequence(buffer)]);
    }

    [Fact]
    public void Packets_that_overtook_each_other_come_out_in_order()
    {
        var buffer = new RtpJitterBuffer(Depth);

        foreach (var index in new[] { 0, 3, 1, 2, 4, 5, 6, 7 })
        {
            buffer.Add(Frame(index));
        }

        Assert.Equal<ushort[]>([0, 1, 2, 3, 4], [.. DrainSequence(buffer)]);
        Assert.Equal<ushort[]>([5, 6, 7], [.. FlushSequence(buffer)]);
    }

    [Fact]
    public void Flush_releases_everything_still_held()
    {
        var buffer = new RtpJitterBuffer(Depth);
        buffer.Add(Frame(0));
        buffer.Add(Frame(1));

        // Below the depth, so a normal drain would keep holding on to both.
        Assert.Empty(DrainSequence(buffer));

        Assert.Equal<ushort[]>([0, 1], [.. FlushSequence(buffer)]);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Ordering_survives_the_timestamp_wrapping()
    {
        // 32 bits of 8 kHz samples wraps after about six days of continuous audio. Unreachable in
        // practice, and an unsigned comparison would put the frames after the wrap at the front of the
        // queue and silently reverse them.
        var buffer = new RtpJitterBuffer(Depth);
        var beforeWrap = uint.MaxValue - (FrameSamples * 2) + 1;

        for (var i = 0; i < 6; i++)
        {
            buffer.Add(new RtpAudioFrame(unchecked(beforeWrap + (uint)(i * FrameSamples)), (ushort)i, new byte[FrameSamples]));
        }

        Assert.Equal<ushort[]>([0, 1, 2], [.. DrainSequence(buffer)]);
        Assert.Equal<ushort[]>([3, 4, 5], [.. FlushSequence(buffer)]);
    }

    [Fact]
    public void A_zero_depth_buffer_passes_everything_straight_through()
    {
        var buffer = new RtpJitterBuffer(TimeSpan.Zero);

        buffer.Add(Frame(0));

        Assert.Equal<ushort[]>([0], [.. DrainSequence(buffer)]);
    }
}
