using System.Buffers.Binary;
using CallTree.Telephony.Audio;
using Xunit;

namespace CallTree.Tests;

public class WavAudioTests
{
    /// <summary>Builds a WAVE file in memory, optionally with extra chunks between "fmt " and "data".</summary>
    private static byte[] BuildWav(
        int sampleRate = 8000,
        short channels = 1,
        short bitsPerSample = 16,
        short formatTag = 1,
        byte[]? samples = null,
        bool includeListChunk = false)
    {
        samples ??= [0x01, 0x02, 0x03, 0x04];

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(0); // patched below
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write(formatTag);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8); // byte rate
        writer.Write((short)(channels * bitsPerSample / 8));     // block align
        writer.Write(bitsPerSample);

        if (includeListChunk)
        {
            // Odd-sized chunk, so the reader also has to honour the word-alignment pad byte.
            writer.Write("LIST"u8);
            writer.Write(5);
            writer.Write("INFOx"u8);
            writer.Write((byte)0);
        }

        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);

        writer.Flush();
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        return bytes;
    }

    [Fact]
    public void ReadPcm_ReturnsSamplesAndRate()
    {
        var pcm = WavAudio.ReadPcm(BuildWav(samples: [1, 2, 3, 4, 5, 6, 7, 8]));

        Assert.Equal(8000, pcm.SampleRate);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, pcm.Samples);
    }

    [Fact]
    public void ReadPcm_SkipsChunksBetweenFmtAndData()
    {
        var pcm = WavAudio.ReadPcm(BuildWav(samples: [9, 9, 9, 9], includeListChunk: true));

        Assert.Equal(new byte[] { 9, 9, 9, 9 }, pcm.Samples);
    }

    [Fact]
    public void Duration_IsSampleCountOverRate()
    {
        // 8000 samples * 2 bytes at 8 kHz = exactly one second.
        var pcm = WavAudio.ReadPcm(BuildWav(samples: new byte[16000]));

        Assert.Equal(TimeSpan.FromSeconds(1), pcm.Duration);
    }

    [Fact]
    public void ReadPcm_AcceptsSixteenKilohertz()
    {
        Assert.Equal(16000, WavAudio.ReadPcm(BuildWav(sampleRate: 16000)).SampleRate);
    }

    [Fact]
    public void ReadPcm_ToleratesTruncatedDataChunk()
    {
        // Simulates a file cut short mid-write: the header claims more data than the file holds.
        var wav = BuildWav(samples: [1, 2, 3, 4, 5, 6, 7, 8]);
        var truncated = wav[..^4];

        Assert.Equal(4, WavAudio.ReadPcm(truncated).Samples.Length);
    }

    [Theory]
    [InlineData(2, 16, 8000, 1)]      // stereo
    [InlineData(1, 8, 8000, 1)]       // 8-bit
    [InlineData(1, 16, 44100, 1)]     // 44.1 kHz
    [InlineData(1, 16, 8000, 6)]      // A-law rather than PCM
    public void ReadPcm_RejectsUnsupportedFormats(short channels, short bits, int rate, short formatTag)
    {
        var wav = BuildWav(sampleRate: rate, channels: channels, bitsPerSample: bits, formatTag: formatTag);

        Assert.Throws<InvalidDataException>(() => WavAudio.ReadPcm(wav));
    }

    [Fact]
    public void ReadPcm_RejectsNonRiffData()
    {
        Assert.Throws<InvalidDataException>(() => WavAudio.ReadPcm("not a wav file at all"u8));
    }

    [Fact]
    public void ReadPcm_ReadsTheGeneratedPrompts()
    {
        // Guards the actual assets: a prompt that fails to decode is silence in a live call.
        var promptsRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CallTree.Api", "prompts");
        if (!Directory.Exists(promptsRoot))
        {
            return;
        }

        var files = Directory.GetFiles(promptsRoot, "*.wav");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var pcm = WavAudio.ReadPcm(File.ReadAllBytes(file));
            Assert.True(pcm.Duration > TimeSpan.Zero, $"{Path.GetFileName(file)} decoded to no audio.");
        }
    }
}
