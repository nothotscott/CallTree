using CallTree.Telephony.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Xunit;

namespace CallTree.Tests;

public class BridgedCallRecorderTests : IDisposable
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

    private BridgedCallRecorder NewRecorder(string name) =>
        new(Guid.NewGuid(), PathFor(name), Depth, NullLogger.Instance);

    /// <summary>A frame of constant tone, distinguishable from silence in the output.</summary>
    private static byte[] Tone(byte level = 0x10) => [.. Enumerable.Repeat(level, FrameSamples)];

    private static void Feed(
        BridgedCallRecorder recorder,
        RecordingChannel channel,
        uint firstTimestamp,
        ushort firstSequence,
        int count,
        byte level = 0x10)
    {
        for (var i = 0; i < count; i++)
        {
            recorder.Accept(
                channel,
                G711.PcmuPayloadType,
                unchecked(firstTimestamp + (uint)(i * FrameSamples)),
                (ushort)(firstSequence + i),
                Tone(level));
        }
    }

    /// <summary>
    /// Reads a stereo WAV back with NAudio's own reader. <see cref="WavAudio"/> is out: it is scoped to
    /// mono prompt playback and rejects anything else.
    /// </summary>
    private static (int SampleRate, int Channels, TimeSpan Duration, short[] Samples) ReadStereo(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new WaveFileReader(stream);
        var buffer = new byte[reader.Length];
        var read = reader.Read(buffer, 0, buffer.Length);
        var samples = new short[read / 2];
        Buffer.BlockCopy(buffer, 0, samples, 0, read);
        return (reader.WaveFormat.SampleRate, reader.WaveFormat.Channels, reader.TotalTime, samples);
    }

    private static (int SampleRate, int Channels, TimeSpan Duration, short[] Samples) ReadStereo(string path) =>
        ReadStereo(File.ReadAllBytes(path));

    [Fact]
    public void Writes_an_eight_kilohertz_stereo_wav_that_reads_back()
    {
        var recorder = NewRecorder("basic.wav");

        Feed(recorder, RecordingChannel.Caller, 0, 0, 50); // one second
        Feed(recorder, RecordingChannel.Mobile, 0, 100, 50);
        var outcome = recorder.Close();

        Assert.Equal(1.0, outcome.DurationSeconds);
        Assert.Equal(0, outcome.LateFrames);
        Assert.Equal(0, outcome.Discontinuities);

        var wav = ReadStereo(PathFor("basic.wav"));
        Assert.Equal(8000, wav.SampleRate);
        Assert.Equal(2, wav.Channels);
        Assert.Equal(TimeSpan.FromSeconds(1), wav.Duration);
        Assert.Equal(outcome.SizeBytes, new FileInfo(PathFor("basic.wav")).Length);
    }

    [Fact]
    public void Each_leg_lands_on_its_own_channel()
    {
        var recorder = NewRecorder("channels.wav");

        // The caller leg carries a loud tone, the mobile leg a quiet one, so the channels can be told
        // apart in the interleaved output.
        Feed(recorder, RecordingChannel.Caller, 0, 0, 5, level: 0x10);
        Feed(recorder, RecordingChannel.Mobile, 0, 100, 5, level: 0x50);
        recorder.Close();

        var samples = ReadStereo(PathFor("channels.wav")).Samples;

        // Interleaved L,R,L,R... - left (caller) samples decode 0x10, right (mobile) samples decode 0x50.
        var callerDecoded = G711.Decode(0x10);
        var mobileDecoded = G711.Decode(0x50);
        for (var i = 0; i < FrameSamples * 5; i++)
        {
            Assert.Equal(callerDecoded, samples[i * 2]);
            Assert.Equal(mobileDecoded, samples[(i * 2) + 1]);
        }
    }

    [Fact]
    public void A_gap_on_one_leg_is_filled_with_silence_while_the_other_keeps_going()
    {
        var recorder = NewRecorder("gap.wav");

        // Mobile leg runs continuously for the whole 1.4s. Caller leg supplies the same total length
        // (1600 + 3200 + 6400 = 11200 samples) but with a 400ms (3200-sample) hole in the middle, so the
        // two legs finish even and the only silence in the file is the caller's own gap.
        Feed(recorder, RecordingChannel.Mobile, 0, 100, 70);
        Feed(recorder, RecordingChannel.Caller, 0, 0, 10);       // 200ms, timestamps 0..1599
        Feed(recorder, RecordingChannel.Caller, 4800, 200, 40);  // resumes after a 3200-sample (400ms) gap

        var outcome = recorder.Close();

        Assert.Equal(1.4, outcome.DurationSeconds, precision: 3);
        Assert.Equal(0.4, outcome.SilenceSeconds, precision: 3);
        Assert.Equal(0, outcome.Discontinuities);

        var samples = ReadStereo(PathFor("gap.wav")).Samples;
        // Caller (left) channel during the gap: sample positions 1600..4799.
        for (var i = 1600; i < 4800; i++)
        {
            Assert.Equal(0, samples[i * 2]);
        }
    }

    [Fact]
    public void An_implausible_timestamp_jump_on_one_leg_resynchronises_instead_of_filling()
    {
        var recorder = NewRecorder("jump.wav");

        Feed(recorder, RecordingChannel.Caller, 0, 0, 10);
        Feed(recorder, RecordingChannel.Caller, 8000 * 600, 100, 10); // ten minutes on
        Feed(recorder, RecordingChannel.Mobile, 0, 200, 20); // steady, no jump

        var outcome = recorder.Close();

        Assert.Equal(1, outcome.Discontinuities);
    }

    [Fact]
    public void Close_pads_the_shorter_leg_so_channel_lengths_match()
    {
        var recorder = NewRecorder("uneven.wav");

        Feed(recorder, RecordingChannel.Caller, 0, 0, 10); // 200ms
        Feed(recorder, RecordingChannel.Mobile, 0, 100, 5); // 100ms

        recorder.Close();

        var wav = ReadStereo(PathFor("uneven.wav"));
        Assert.Equal(TimeSpan.FromMilliseconds(200), wav.Duration);
        // Every sample must exist on both channels - Samples.Length is even for stereo, one L/R pair per frame.
        Assert.Equal(0, wav.Samples.Length % 2);
    }

    [Fact]
    public void Non_pcmu_payloads_are_ignored()
    {
        var recorder = NewRecorder("dtmf.wav");

        Feed(recorder, RecordingChannel.Caller, 0, 0, 10);
        Feed(recorder, RecordingChannel.Mobile, 0, 100, 10);
        recorder.Accept(RecordingChannel.Caller, 101, 1600, 200, [0x01, 0x0A, 0x00, 0xA0]);

        var outcome = recorder.Close();

        Assert.Equal(0.2, outcome.DurationSeconds, precision: 3);
    }

    [Fact]
    public void A_recording_of_nothing_is_still_a_readable_wav()
    {
        var outcome = NewRecorder("empty.wav").Close();

        Assert.Equal(0.0, outcome.DurationSeconds);
        Assert.True(outcome.SizeBytes is > 0 and < 128, $"Expected a bare header but the file is {outcome.SizeBytes} bytes.");
        Assert.Empty(ReadStereo(PathFor("empty.wav")).Samples);
    }

    [Fact]
    public void The_file_on_disk_stays_playable_part_way_through_a_call()
    {
        var recorder = NewRecorder("partial.wav");

        Feed(recorder, RecordingChannel.Caller, 0, 0, 500);   // ten seconds, past the five-second flush
        Feed(recorder, RecordingChannel.Mobile, 0, 100, 500);

        var partial = ReadStereo(ReadShared(PathFor("partial.wav")));
        Assert.True(
            partial.Duration >= TimeSpan.FromSeconds(5),
            $"Expected at least 5s readable mid-call but the header claimed {partial.Duration.TotalSeconds:0.0}s.");

        recorder.Close();
        Assert.Equal(TimeSpan.FromSeconds(10), ReadStereo(PathFor("partial.wav")).Duration);
    }

    [Fact]
    public void Close_is_idempotent()
    {
        var recorder = NewRecorder("twice.wav");
        Feed(recorder, RecordingChannel.Caller, 0, 0, 10);
        Feed(recorder, RecordingChannel.Mobile, 0, 100, 10);

        var first = recorder.Close();

        Assert.Equal(first, recorder.Close());
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
