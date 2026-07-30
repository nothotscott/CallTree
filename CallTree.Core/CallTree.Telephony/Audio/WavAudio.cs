using System.Buffers.Binary;

namespace CallTree.Telephony.Audio;

/// <summary>16-bit PCM audio decoded from a RIFF/WAVE container.</summary>
/// <param name="Samples">Raw little-endian 16-bit mono PCM — what SIPSorcery's audio source expects.</param>
public sealed record PcmAudio(byte[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / 2.0 / SampleRate);
}

/// <summary>
/// A minimal RIFF/WAVE reader for prompt playback.
/// </summary>
/// <remarks>
/// SIPSorcery's <c>AudioExtrasSource.SendAudioFromStream</c> takes <em>raw</em> 16-bit PCM at 8 or 16 kHz —
/// hand it a .wav file and the 44-byte header is played as a burst of noise. Prompts are stored as real
/// .wav files so they can be auditioned and edited with ordinary tools, and unwrapped here on load.
/// Chunks are walked rather than assumed at fixed offsets, because real encoders interleave LIST/fact
/// chunks between "fmt " and "data".
/// </remarks>
public static class WavAudio
{
    private const int PcmFormatTag = 1;

    /// <summary>Decodes a WAVE file. Accepts 8 or 16 kHz, 16-bit, mono PCM.</summary>
    /// <exception cref="InvalidDataException">The file is malformed or in an unsupported format.</exception>
    public static PcmAudio ReadPcm(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12
            || !file[..4].SequenceEqual("RIFF"u8)
            || !file.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        int sampleRate = 0, channels = 0, bitsPerSample = 0, formatTag = 0;
        byte[]? samples = null;

        var offset = 12;
        while (offset + 8 <= file.Length)
        {
            var chunkId = file.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(offset + 4, 4));
            var body = offset + 8;

            if (chunkSize < 0 || body + chunkSize > file.Length)
            {
                // Truncated final chunk: take whatever is actually present rather than throwing,
                // so a recording cut short by a crash is still readable.
                chunkSize = file.Length - body;
            }

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("Malformed 'fmt ' chunk.");
                }

                formatTag = BinaryPrimitives.ReadInt16LittleEndian(file.Slice(body, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(file.Slice(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(body + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(file.Slice(body + 14, 2));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                samples = file.Slice(body, chunkSize).ToArray();
            }

            // Chunks are word-aligned: an odd size is followed by a pad byte.
            offset = body + chunkSize + (chunkSize % 2);
        }

        if (samples is null)
        {
            throw new InvalidDataException("WAVE file has no 'data' chunk.");
        }

        if (formatTag != PcmFormatTag)
        {
            throw new InvalidDataException($"Expected uncompressed PCM (format 1) but found format {formatTag}.");
        }

        if (channels != 1)
        {
            throw new InvalidDataException($"Expected mono but found {channels} channels.");
        }

        if (bitsPerSample != 16)
        {
            throw new InvalidDataException($"Expected 16-bit samples but found {bitsPerSample}-bit.");
        }

        if (sampleRate is not (8000 or 16000))
        {
            throw new InvalidDataException($"Expected an 8 or 16 kHz sample rate but found {sampleRate} Hz.");
        }

        return new PcmAudio(samples, sampleRate);
    }
}
