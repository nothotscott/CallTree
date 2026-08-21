namespace CallTree.Telephony.Audio;

/// <summary>
/// G.711 mu-law (PCMU) expansion to 16-bit linear PCM — the only decode this project needs, because
/// only PCMU is ever offered.
/// </summary>
/// <remarks>
/// Written out rather than taken from NAudio's <c>MuLawDecoder</c> because understanding the wire format
/// is half the point of the project; the decode is eight lines and the test asserts it against NAudio's
/// table for all 256 inputs, so "understandable" costs nothing in correctness.
///
/// The format is a floating-point-like companding: a sign bit, a 3-bit exponent (the "segment") and a
/// 4-bit mantissa, stored complemented. Complementing is what makes digital silence (0xFF) the byte a
/// dead or idle link is most likely to produce, and it keeps the low-amplitude codes dense — the whole
/// point of companding is that quiet passages get finer resolution than loud ones.
///
/// One deliberate difference from NAudio: code 0x7F ("negative zero") decodes to 0 here, matching the
/// ITU reference expansion, where NAudio's lookup table answers -1. One LSB on a code that means zero,
/// so it is inaudible either way — but silence should decode as silence, and the test pins the
/// disagreement down so it is not rediscovered as a bug.
/// </remarks>
public static class G711
{
    /// <summary>PCMU's RTP clock rate. Also its sample rate: one byte in, one sample out.</summary>
    public const int ClockRate = 8000;

    /// <summary>Static RTP payload type for PCMU (RFC 3551).</summary>
    public const int PcmuPayloadType = 0;

    /// <summary>Bytes one decoded sample occupies in the output.</summary>
    public const int BytesPerSample = 2;

    private const int Bias = 0x84;
    private const int SignBit = 0x80;
    private const int SegmentMask = 0x70;
    private const int SegmentShift = 4;
    private const int MantissaMask = 0x0F;

    // 256 entries: decoding is a lookup, not arithmetic, on the hot path.
    private static readonly short[] DecodeTable = BuildDecodeTable();

    /// <summary>Expands one mu-law byte to a signed 16-bit sample.</summary>
    public static short Decode(byte encoded) => DecodeTable[encoded];

    /// <summary>
    /// Expands a whole RTP payload into little-endian 16-bit PCM.
    /// <paramref name="pcm"/> must have room for <c>encoded.Length * 2</c> bytes.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> encoded, Span<byte> pcm)
    {
        if (pcm.Length < encoded.Length * BytesPerSample)
        {
            throw new ArgumentException(
                $"Need {encoded.Length * BytesPerSample} bytes for {encoded.Length} samples but was given {pcm.Length}.",
                nameof(pcm));
        }

        for (var i = 0; i < encoded.Length; i++)
        {
            var sample = DecodeTable[encoded[i]];
            pcm[i * 2] = (byte)sample;
            pcm[(i * 2) + 1] = (byte)(sample >> 8);
        }
    }

    private static short[] BuildDecodeTable()
    {
        var table = new short[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = Expand((byte)i);
        }

        return table;
    }

    /// <summary>ITU-T G.711 mu-law expansion, following the reference implementation.</summary>
    private static short Expand(byte encoded)
    {
        var value = (byte)~encoded;

        // Rebuild the mantissa at the bottom of the segment, then add the bias back — it is added
        // before encoding so that the sign-magnitude representation has no gap around zero.
        var magnitude = ((value & MantissaMask) << 3) + Bias;
        magnitude <<= (value & SegmentMask) >> SegmentShift;

        return (short)((value & SignBit) != 0 ? Bias - magnitude : magnitude - Bias);
    }
}
