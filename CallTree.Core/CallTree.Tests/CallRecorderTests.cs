using CallTree.Telephony.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CallTree.Tests;

public class CallRecorderTests : IDisposable
{
    /// <summary>One 20 ms PCMU packet.</summary>
    private const int FrameSamples = 160;

    private static readonly TimeSpan Depth = TimeSpan.FromMilliseconds(60);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "calltree-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private CallRecorder NewRecorder(string name) =>
        new(Guid.NewGuid(), PathFor(name), Depth, NullLogger.Instance);

    /// <summary>A frame of constant tone, so silence and audio can be told apart in the output.</summary>
    private static byte[] Tone(byte level = 0x10) => [.. Enumerable.Repeat(level, FrameSamples)];

    /// <summary>Feeds <paramref name="count"/> consecutive 20 ms frames starting at a timestamp.</summary>
    private static void Feed(CallRecorder recorder, uint firstTimestamp, ushort firstSequence, int count, byte level = 0x10)
    {
        for (var i = 0; i < count; i++)
        {
            recorder.Accept(
                G711.PcmuPayloadType,
                unchecked(firstTimestamp + (uint)(i * FrameSamples)),
                (ushort)(firstSequence + i),
                Tone(level));
        }
    }

    /// <summary>Feeds the attached secondary leg, mirroring <see cref="Feed"/> for the primary.</summary>
    private static void FeedSecondary(CallRecorder recorder, uint firstTimestamp, ushort firstSequence, int count, byte level = 0x50)
    {
        for (var i = 0; i < count; i++)
        {
            recorder.AcceptSecondary(
                G711.PcmuPayloadType,
                unchecked(firstTimestamp + (uint)(i * FrameSamples)),
                (ushort)(firstSequence + i),
                Tone(level));
        }
    }

    private static PcmAudio Read(string path) => WavAudio.ReadPcm(File.ReadAllBytes(path));

    private static short[] ToShorts(byte[] bytes)
    {
        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
    }

    [Fact]
    public void Writes_an_eight_kilohertz_mono_wav_that_reads_back()
    {
        var recorder = NewRecorder("basic.wav");

        Feed(recorder, 0, 0, 50); // one second
        var outcome = recorder.Close();

        Assert.Equal(1.0, outcome.DurationSeconds);
        Assert.Equal(0, outcome.LateFrames);
        Assert.Equal(0, outcome.Discontinuities);

        var pcm = Read(PathFor("basic.wav"));
        Assert.Equal(8000, pcm.SampleRate);
        Assert.Equal(TimeSpan.FromSeconds(1), pcm.Duration);
        Assert.Equal(outcome.SizeBytes, new FileInfo(PathFor("basic.wav")).Length);
    }

    [Fact]
    public void A_gap_in_the_stream_becomes_silence_of_exactly_the_missing_length()
    {
        // The whole reason the RTP timestamp drives the file: writing packets back to back would make a
        // pause vanish and leave everything after it out of step with what was actually said.
        var recorder = NewRecorder("gap.wav");

        Feed(recorder, 0, 0, 10);        // 200 ms of audio, ending at timestamp 1600
        Feed(recorder, 16_000, 100, 10); // resumes two seconds after the start: a 1.8 s hole

        var outcome = recorder.Close();

        Assert.Equal(2.2, outcome.DurationSeconds, precision: 3);
        Assert.Equal(1.8, outcome.SilenceSeconds, precision: 3);
        Assert.Equal(0, outcome.Discontinuities);

        var samples = Read(PathFor("gap.wav")).Samples;
        Assert.Equal(17_600 * 2, samples.Length);

        // The filled region is real silence, not whatever the pooled buffer happened to hold.
        Assert.True(
            samples.Skip(FrameSamples * 10 * 2).Take(8000).All(b => b == 0),
            "The filled gap contained something other than silence.");
    }

    [Fact]
    public void An_implausible_timestamp_jump_resynchronises_instead_of_filling()
    {
        // A single bogus timestamp would otherwise ask for however many gigabytes of silence it implies.
        var recorder = NewRecorder("jump.wav");

        Feed(recorder, 0, 0, 10);
        Feed(recorder, 8000 * 600, 100, 10); // ten minutes on

        var outcome = recorder.Close();

        Assert.Equal(1, outcome.Discontinuities);
        Assert.Equal(0.4, outcome.DurationSeconds, precision: 3); // 200 ms either side, nothing between
        Assert.Equal(0.0, outcome.SilenceSeconds);
    }

    [Fact]
    public void A_packet_that_arrives_after_its_place_was_written_is_dropped()
    {
        var recorder = NewRecorder("late.wav");

        Feed(recorder, 8000, 100, 10);                       // starts one second in
        recorder.Accept(G711.PcmuPayloadType, 0, 5, Tone()); // belongs a second earlier

        var outcome = recorder.Close();

        // A WAV cannot be inserted into. Dropping it costs 20 ms; rewinding would corrupt the rest.
        Assert.Equal(1, outcome.LateFrames);
        Assert.Equal(0.2, outcome.DurationSeconds, precision: 3);
    }

    [Fact]
    public void Out_of_order_packets_within_the_buffer_depth_are_put_back_in_order()
    {
        var recorder = NewRecorder("reordered.wav");

        foreach (var index in new[] { 0, 1, 3, 2, 4, 5, 6, 7, 8, 9 })
        {
            recorder.Accept(G711.PcmuPayloadType, (uint)(index * FrameSamples), (ushort)index, Tone());
        }

        var outcome = recorder.Close();

        // Nothing dropped and nothing filled: the swap was undone before anything reached the file.
        Assert.Equal(0, outcome.LateFrames);
        Assert.Equal(0.0, outcome.SilenceSeconds);
        Assert.Equal(0.2, outcome.DurationSeconds, precision: 3);
    }

    [Fact]
    public void Non_pcmu_payloads_are_ignored()
    {
        // Payload 101 is the RFC 4733 telephone-event stream sharing the same RTP session. Decoding it
        // as audio would write a burst of noise into the recording every time a digit is pressed.
        var recorder = NewRecorder("dtmf.wav");

        Feed(recorder, 0, 0, 10);
        recorder.Accept(101, 1600, 200, [0x01, 0x0A, 0x00, 0xA0]);

        var outcome = recorder.Close();

        Assert.Equal(0.2, outcome.DurationSeconds, precision: 3);
    }

    [Fact]
    public void A_recording_of_nothing_is_still_a_readable_wav()
    {
        // The caller answered and hung up before a single RTP packet arrived. A row will point at this
        // file, so it has to be a valid empty WAV rather than a zero-byte stub.
        var outcome = NewRecorder("empty.wav").Close();

        Assert.Equal(0.0, outcome.DurationSeconds);
        Assert.True(outcome.SizeBytes is > 0 and < 128, $"Expected a bare header but the file is {outcome.SizeBytes} bytes.");
        Assert.Empty(Read(PathFor("empty.wav")).Samples);
    }

    [Fact]
    public void The_file_on_disk_stays_playable_part_way_through_a_call()
    {
        // If the process is killed mid-call the header is whatever the last flush left behind. This
        // asserts the flush really does patch the RIFF sizes rather than only pushing bytes at the
        // filesystem - otherwise a crashed recording reads as empty in every tool that trusts the header.
        var recorder = NewRecorder("partial.wav");

        Feed(recorder, 0, 0, 500); // ten seconds, past the five-second flush interval

        var partial = WavAudio.ReadPcm(ReadShared(PathFor("partial.wav")));
        Assert.True(
            partial.Duration >= TimeSpan.FromSeconds(5),
            $"Expected at least 5s readable mid-call but the header claimed {partial.Duration.TotalSeconds:0.0}s.");

        recorder.Close();
        Assert.Equal(TimeSpan.FromSeconds(10), Read(PathFor("partial.wav")).Duration);
    }

    [Fact]
    public void Close_is_idempotent()
    {
        var recorder = NewRecorder("twice.wav");
        Feed(recorder, 0, 0, 10);

        var first = recorder.Close();

        Assert.Equal(first, recorder.Close());
    }

    [Fact]
    public void Packets_offered_after_close_are_ignored()
    {
        var recorder = NewRecorder("after-close.wav");
        Feed(recorder, 0, 0, 10);
        var outcome = recorder.Close();

        Feed(recorder, 1600, 100, 10);

        Assert.Equal(outcome, recorder.Close());
        Assert.Equal(0.2, Read(PathFor("after-close.wav")).Duration.TotalSeconds, precision: 3);
    }

    [Fact]
    public void A_secondary_leg_attached_from_the_start_sums_both_into_one_mono_stream()
    {
        // Attaching before any primary audio arrives avoids any ordering ambiguity from the jitter
        // buffer's own reordering window - both legs start clean and in lockstep.
        var recorder = NewRecorder("mixed-from-start.wav");
        recorder.AttachSecondaryLeg();

        Feed(recorder, 0, 0, 20, level: 0x10); // 400ms
        FeedSecondary(recorder, 0, 100, 20, level: 0x50); // 400ms, same span

        var outcome = recorder.Close();

        Assert.Equal(0.4, outcome.DurationSeconds, precision: 3);

        var expected = (short)Math.Clamp(G711.Decode(0x10) + G711.Decode(0x50), short.MinValue, short.MaxValue);
        Assert.All(ToShorts(Read(PathFor("mixed-from-start.wav")).Samples), s => Assert.Equal(expected, s));
    }

    [Fact]
    public void AcceptSecondary_before_any_attach_is_ignored()
    {
        var recorder = NewRecorder("no-attach.wav");

        Feed(recorder, 0, 0, 10);
        FeedSecondary(recorder, 0, 100, 10); // nothing attached - must be a no-op, not a crash

        var outcome = recorder.Close();

        // Behaves exactly like a plain single-leg recording: the stray secondary packets left no trace.
        Assert.Equal(0.2, outcome.DurationSeconds, precision: 3);
        var expected = G711.Decode(0x10);
        Assert.All(ToShorts(Read(PathFor("no-attach.wav")).Samples), s => Assert.Equal(expected, s));
    }

    /// <summary>Feeds both legs in lockstep, one frame each per iteration - mirrors two concurrent real-time RTP streams.</summary>
    private static void FeedBoth(
        CallRecorder recorder, uint primaryTimestamp, ushort primarySequence, uint secondaryTimestamp, ushort secondarySequence, int count)
    {
        for (var i = 0; i < count; i++)
        {
            recorder.Accept(
                G711.PcmuPayloadType, unchecked(primaryTimestamp + (uint)(i * FrameSamples)), (ushort)(primarySequence + i), Tone());
            recorder.AcceptSecondary(
                G711.PcmuPayloadType, unchecked(secondaryTimestamp + (uint)(i * FrameSamples)), (ushort)(secondarySequence + i), Tone(0x50));
        }
    }

    [Fact]
    public void Detaching_stops_mixing_and_the_file_reverts_to_primary_only()
    {
        var recorder = NewRecorder("detach.wav");

        recorder.AttachSecondaryLeg();
        FeedBoth(recorder, primaryTimestamp: 0, primarySequence: 0, secondaryTimestamp: 0, secondarySequence: 100, count: 10); // 200ms mixed
        recorder.DetachSecondaryLeg();

        Feed(recorder, 1600, 10, 10); // 200ms primary-only afterwards

        var outcome = recorder.Close();

        // 200ms mixed + 200ms primary-only = 400ms of primary audio, plus at most one jitter-buffer
        // depth's worth (60ms configured here) of secondary tail that had no primary counterpart yet at
        // the exact moment of detach - the same bounded reconciliation BridgedCallRecorder documents for
        // its own leg-length mismatch at Close(). Nothing is ever lost; this just isn't exactly 400ms.
        Assert.InRange(outcome.DurationSeconds, 0.4, 0.4 + Depth.TotalSeconds);
    }

    [Fact]
    public void A_second_attach_after_detach_mixes_again()
    {
        // Simulates the operator dialing *{NUMBER}# twice in the same call.
        var recorder = NewRecorder("reattach.wav");

        recorder.AttachSecondaryLeg();
        FeedBoth(recorder, primaryTimestamp: 0, primarySequence: 0, secondaryTimestamp: 0, secondarySequence: 100, count: 10);
        recorder.DetachSecondaryLeg();

        Feed(recorder, 1600, 10, 5); // 100ms primary-only in between the two proxy calls

        recorder.AttachSecondaryLeg();
        FeedBoth(recorder, primaryTimestamp: 2400, primarySequence: 15, secondaryTimestamp: 0, secondarySequence: 200, count: 10);
        recorder.DetachSecondaryLeg();

        var outcome = recorder.Close();

        // 200ms mixed + 100ms alone + 200ms mixed again = 500ms of primary audio, plus up to two
        // detaches' worth of bounded secondary-tail reconciliation (see the test above).
        Assert.InRange(outcome.DurationSeconds, 0.5, 0.5 + (2 * Depth.TotalSeconds));
    }

    /// <summary>Reads a file the recorder still holds open for writing.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
