namespace CallTree.Telephony.Audio;

/// <summary>
/// G.711 mu-law (PCMU) companding, both directions. Expansion to 16-bit linear PCM is what a call
/// needs, because only PCMU is ever offered; compression exists for the SIP harness, which synthesises
/// its own audio rather than forwarding somebody else's.
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

    /// <summary>Largest magnitude the format can carry, in the 14-bit domain it works in.</summary>
    private const int Clip = 8159;

    private const int SegmentCount = 8;

    /// <summary>Top of the lowest exponent segment; every one above it is <c>(end &lt;&lt; 1) + 1</c>.</summary>
    private const int FirstSegmentEnd = 0x3F;

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

    /// <summary>Compresses one signed 16-bit sample to a mu-law byte.</summary>
    /// <remarks>
    /// The inverse of <see cref="Decode(byte)"/>, and written out for the same reason. Only the SIP
    /// harness needs it - a real call never encodes, because both legs are already PCMU and the relay
    /// forwards payloads untouched - but a harness that synthesises its own test tone has to put it on
    /// the wire somehow, and borrowing NAudio's encoder would leave the two halves of one codec written
    /// in different styles from different sources.
    ///
    /// Three details worth spelling out. The input is shifted down to 14 bits because that is the
    /// format's actual resolution: the bottom two bits of a 16-bit sample cannot be represented, and are
    /// dropped rather than rounded. The result is complemented through <c>mask</c> rather than with
    /// <c>~</c>, which folds the sign in at the same time - 0xFF flips every bit (leaving the sign bit
    /// clear, meaning positive), 0x7F flips all but the top one.
    ///
    /// And the magnitude is taken <em>before</em> the shift, in an int, which is a deliberate departure
    /// from the ITU reference's ordering. The reference shifts first, and an arithmetic shift of a
    /// negative number rounds towards negative infinity rather than towards zero - so -31611 lands one
    /// quantisation step away from where +31611 does, and near a segment boundary that is a different
    /// output code. Doing it this way makes the encoding symmetric about zero and agrees with NAudio
    /// everywhere; the int is also what keeps <c>short.MinValue</c> from wrapping when it is negated,
    /// which is the one input where NAudio does not agree with itself (see G711Tests).
    /// </remarks>
    public static byte Encode(short sample)
    {
        var mask = sample < 0 ? 0x7F : 0xFF;
        var magnitude = Math.Abs((int)sample) >> 2;

        if (magnitude > Clip)
        {
            magnitude = Clip;
        }

        magnitude += Bias >> 2;

        var segment = Segment(magnitude);

        return segment >= SegmentCount
            ? (byte)(0x7F ^ mask)
            : (byte)(((segment << SegmentShift) | ((magnitude >> (segment + 1)) & MantissaMask)) ^ mask);
    }

    /// <summary>Compresses little-endian 16-bit PCM into <paramref name="encoded"/>, one byte per sample.</summary>
    public static void Encode(ReadOnlySpan<byte> pcm, Span<byte> encoded)
    {
        var samples = pcm.Length / BytesPerSample;
        if (encoded.Length < samples)
        {
            throw new ArgumentException(
                $"Need {samples} bytes for {samples} samples but was given {encoded.Length}.",
                nameof(encoded));
        }

        for (var i = 0; i < samples; i++)
        {
            encoded[i] = Encode((short)(pcm[i * 2] | (pcm[(i * 2) + 1] << 8)));
        }
    }

    /// <summary>
    /// Which of the eight exponent segments <paramref name="magnitude"/> falls in: the index of the first
    /// segment whose top end it does not exceed, or <see cref="SegmentCount"/> when it exceeds them all.
    /// The ends double each step (0x3F, 0x7F, 0xFF ...), which is the companding itself - each segment
    /// spans twice the range of the one below with the same four bits of mantissa to describe it.
    /// </summary>
    private static int Segment(int magnitude)
    {
        var segment = 0;
        for (var end = FirstSegmentEnd; segment < SegmentCount && magnitude > end; end = (end << 1) + 1)
        {
            segment++;
        }

        return segment;
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
