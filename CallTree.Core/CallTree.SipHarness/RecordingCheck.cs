using System.Buffers.Binary;
using System.Globalization;

namespace CallTree.SipHarness;

/// <summary>What one recorded WAV turned out to contain.</summary>
/// <param name="Path">Path relative to the recordings root, matching what the Recording row stores.</param>
/// <param name="Channels">1 for the mono Outbound path, 2 for a stereo bridge (caller left, mobile right).</param>
/// <param name="Tones">The dominant harness tone on each channel, in channel order.</param>
internal readonly record struct RecordedAudio(
    string Path, int Channels, double DurationSeconds, IReadOnlyList<HeardAudio> Tones);

/// <summary>
/// Decodes the WAVs CallTree wrote during a run and works out which tone landed on which channel.
/// </summary>
/// <remarks>
/// <para>
/// This is the check the rest of the harness exists to make possible. Everything else proves audio
/// crossed the wire correctly; this proves it was also <em>written down</em> correctly, which is a
/// separate pipeline with its own jitter buffer, its own timestamp-driven clock and, on the bridge, its
/// own shared wall clock reconciling two unrelated RTP timelines. A relay can be perfect while the
/// recorder writes the wrong leg into the wrong channel, and only the file says so.
/// </para>
/// <para>
/// It reads stereo, which is why it does not use <c>WavAudio.ReadPcm</c>: that reader deliberately
/// refuses anything but mono, because a prompt that is not mono is a mistake. Refusing stereo is right
/// there and wrong here, and relaxing it to suit a test tool would weaken a real guard.
/// </para>
/// </remarks>
internal static class RecordingCheck
{
    /// <summary>
    /// Every WAV under <paramref name="root"/> written since <paramref name="since"/>, decoded and
    /// scored. Modification time is the filter because CallTree names files by call id, which the
    /// harness never learns - it only ever talks SIP.
    /// </summary>
    public static IReadOnlyList<RecordedAudio> Since(string root, DateTime since, IReadOnlyList<int> candidates)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<RecordedAudio>();

        foreach (var file in Directory.EnumerateFiles(root, "*.wav", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < since || info.Length <= 44)
            {
                continue;
            }

            try
            {
                results.Add(Score(root, file, candidates));
            }
            catch (Exception ex)
            {
                results.Add(new RecordedAudio(
                    Path.GetRelativePath(root, file), 0, 0, [new HeardAudio(0, 0, null, 0, [])]));
                Console.Error.WriteLine($"  ! could not read {file}: {ex.Message}");
            }
        }

        return results;
    }

    private static RecordedAudio Score(string root, string path, IReadOnlyList<int> candidates)
    {
        var (samples, sampleRate, channels) = Read(File.ReadAllBytes(path));
        var frames = samples.Length / channels;
        var tones = new List<HeardAudio>(channels);

        for (var channel = 0; channel < channels; channel++)
        {
            // The detector works on PCMU frames because that is what a live leg delivers, so the file's
            // linear samples are re-encoded to feed it. Mu-law is lossy, but it is the same loss the
            // audio already took on the wire - the file was written from decoded PCMU in the first place.
            var detector = new ToneDetector(candidates, windowSeconds: 30);

            // Analyse the tail, for the same reason the live detector does: the head of a recording is
            // whatever prompt or ringback was playing before the call settled.
            var start = Math.Max(0, frames - (sampleRate * 20));
            var frame = new byte[ToneSource.SamplesPerFrame];
            var filled = 0;

            for (var i = start; i < frames; i++)
            {
                frame[filled++] = CallTree.Telephony.Audio.G711.Encode(samples[(i * channels) + channel]);
                if (filled == frame.Length)
                {
                    detector.Accept(CallTree.Telephony.Audio.G711.PcmuPayloadType, frame);
                    frame = new byte[ToneSource.SamplesPerFrame];
                    filled = 0;
                }
            }

            tones.Add(detector.Result());
        }

        return new RecordedAudio(
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            channels,
            frames / (double)sampleRate,
            tones);
    }

    /// <summary>
    /// A minimal RIFF/WAVE reader that accepts mono or stereo 16-bit PCM. Chunks are walked rather than
    /// assumed at fixed offsets, and a truncated final chunk is taken as far as it goes - a recording cut
    /// short by a killed process is exactly the case this most wants to be able to read.
    /// </summary>
    private static (short[] Samples, int SampleRate, int Channels) Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        byte[]? data = null;

        var offset = 12;
        while (offset + 8 <= file.Length)
        {
            var id = file.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(offset + 4, 4));
            var body = offset + 8;

            if (size < 0 || body + size > file.Length)
            {
                size = file.Length - body;
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(file.Slice(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(body + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(file.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = file.Slice(body, size).ToArray();
            }

            offset = body + size + (size % 2);
        }

        if (data is null || channels is < 1 or > 2 || bitsPerSample != 16 || sampleRate == 0)
        {
            throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"Unsupported WAVE: {channels} channels, {bitsPerSample}-bit, {sampleRate} Hz."));
        }

        var samples = new short[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(data[i * 2] | (data[(i * 2) + 1] << 8));
        }

        return (samples, sampleRate, channels);
    }
}
