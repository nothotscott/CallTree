using CallTree.Telephony.Audio;
using Xunit;

namespace CallTree.Tests;

public class G711Tests
{
    /// <summary>
    /// The one code where the ITU reference expansion and NAudio's lookup table disagree: 0x7F, the
    /// negative-zero code. See <see cref="Negative_zero_decodes_to_zero"/>.
    /// </summary>
    private const byte NegativeZero = 0x7F;

    [Fact]
    public void Decode_matches_naudio_for_every_code_except_negative_zero()
    {
        // NAudio is the oracle, not the implementation: the decode is written out in G711 because the
        // wire format is worth understanding, and this is what makes "written out" safe. All 256 inputs
        // is the whole domain, so there is nothing left to sample.
        for (var i = 0; i <= 255; i++)
        {
            var encoded = (byte)i;
            if (encoded == NegativeZero)
            {
                continue;
            }

            Assert.Equal(NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(encoded), G711.Decode(encoded));
        }
    }

    [Fact]
    public void Negative_zero_decodes_to_zero()
    {
        // NAudio's table answers -1 here. Its table is one of several in circulation; the ITU reference
        // expansion computes 0, and so do we. The difference is one LSB on a code that means "zero", so
        // it is inaudible either way - but a table that cannot represent silence as silence is the wrong
        // one to inherit, and this is the assertion that stops the discrepancy being rediscovered as a
        // bug the next time the two are compared.
        Assert.Equal(-1, NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(NegativeZero));
        Assert.Equal(0, G711.Decode(NegativeZero));
    }

    [Fact]
    public void Digital_silence_decodes_to_zero()
    {
        // 0xFF is what an idle or dead link produces, and it has to be actual silence rather than a
        // constant offset - a DC offset is inaudible on its own and audible the moment two legs are mixed.
        Assert.Equal(0, G711.Decode(0xFF));
    }

    [Fact]
    public void Decode_writes_little_endian_pairs()
    {
        var pcm = new byte[4];

        G711.Decode([0xFF, 0x00], pcm);

        Assert.Equal(0, BitConverter.ToInt16(pcm, 0));
        Assert.Equal(NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(0x00), BitConverter.ToInt16(pcm, 2));
    }

    [Fact]
    public void Decode_refuses_an_undersized_destination()
    {
        Assert.Throws<ArgumentException>(() => G711.Decode(new byte[10], new byte[19]));
    }

    [Fact]
    public void Codes_either_side_of_zero_are_symmetric()
    {
        // Sign-magnitude, not two's complement: the positive and negative halves mirror each other.
        for (var i = 0; i < 128; i++)
        {
            var positive = G711.Decode((byte)(i | 0x80));
            var negative = G711.Decode((byte)i);

            Assert.Equal(positive, (short)-negative);
        }
    }
}
