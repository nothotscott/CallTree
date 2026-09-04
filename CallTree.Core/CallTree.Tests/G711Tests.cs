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

    [Fact]
    public void Encode_matches_naudio_for_every_16_bit_sample_except_the_most_negative()
    {
        // NAudio as the oracle again, over the entire 65,536-value input domain rather than a sample of
        // it. One input is excluded, and it is a genuine bug in the oracle rather than a difference of
        // opinion - see Most_negative_sample_encodes_to_the_loudest_code.
        //
        // The comparison is a plain loop with a single assertion at the end rather than 65,535 of them:
        // an assertion per iteration turns a millisecond of arithmetic into minutes of xUnit bookkeeping,
        // and a whole test suite nobody wants to run is worse than a slightly less granular failure.
        var mismatches = new List<string>();

        for (var i = short.MinValue + 1; i <= short.MaxValue; i++)
        {
            var sample = (short)i;
            var expected = NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(sample);
            var actual = G711.Encode(sample);

            if (expected != actual && mismatches.Count < 10)
            {
                mismatches.Add($"{sample}: NAudio {expected:X2}, G711 {actual:X2}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Most_negative_sample_encodes_to_the_loudest_code()
    {
        // short.MinValue is the one value with no positive counterpart, so negating it overflows back to
        // itself. NAudio negates before clipping and in a short, so the clip test never fires and the
        // arithmetic runs on a still-negative number: it answers 0x7F, which decodes to silence. The
        // loudest sample the format can be handed comes out as the quietest code it has - an audible
        // click at exactly the peak of a loud passage.
        //
        // G711 clips the magnitude in an int before anything can wrap, so it answers 0x00, the most
        // negative code, matching the ITU reference. The assertion is written both ways round so this
        // reads as a deliberate divergence rather than as a mismatch nobody noticed - the same treatment
        // the negative-zero decode gets above.
        Assert.Equal(0x7F, NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(short.MinValue));
        Assert.Equal(0x00, G711.Encode(short.MinValue));
        Assert.True(G711.Decode(G711.Encode(short.MinValue)) < -32000);
    }

    [Fact]
    public void Encode_then_decode_lands_within_one_quantisation_step()
    {
        // Mu-law is lossy by construction, so a round trip is not an identity - what it must be is
        // *close*, and closer for quiet samples than for loud ones. That is the whole point of
        // companding, so the tolerance is expressed as a fraction of the magnitude rather than a
        // constant: a 4% band holds across five orders of magnitude where any fixed epsilon would either
        // pass everything or fail the quiet end.
        var worst = (Sample: (short)0, Round: (short)0, Excess: 0.0);

        for (var i = (int)short.MinValue; i <= short.MaxValue; i++)
        {
            var value = (short)i;
            var round = G711.Decode(G711.Encode(value));

            // The format truncates rather than rounds, so the error can be a whole quantisation step -
            // and that step is proportional to magnitude, which is the entire point of companding. At the
            // bottom of a segment the step is about a sixteenth of the magnitude; the floor covers the
            // smallest segment plus the two low bits dropped on the way into the 14-bit domain.
            var tolerance = Math.Max(36, (Math.Abs((int)value) / 16.0) + 4);
            var excess = Math.Abs(round - value) - tolerance;

            if (excess > worst.Excess)
            {
                worst = (value, round, excess);
            }
        }

        Assert.True(
            worst.Excess <= 0,
            $"{worst.Sample} round-tripped to {worst.Round}, {worst.Excess:0.#} beyond the tolerance.");
    }

    [Fact]
    public void Silence_round_trips_to_the_byte_an_idle_link_produces()
    {
        // 0xFF is the byte a dead or idle mu-law link is most likely to emit, and it is silence rather
        // than a loud DC offset precisely because the format is stored complemented. Pinning it here
        // means the harness's generated tone starts and ends at true silence, and a recording full of
        // 0xFF reads as "nobody spoke" rather than "something is wrong".
        Assert.Equal(0xFF, G711.Encode(0));
        Assert.Equal(0, G711.Decode(0xFF));
    }

    [Fact]
    public void Encode_saturates_rather_than_wrapping_at_the_top()
    {
        // Everything past the clip point collapses onto one code in each direction. That is saturation,
        // not an error: the format simply has no louder code to give, and the alternative - letting the
        // magnitude wrap - would turn a peak into its opposite.
        Assert.Equal(G711.Encode(short.MaxValue), G711.Encode((short)(short.MaxValue - 3)));
        Assert.Equal(G711.Encode(short.MinValue), G711.Encode((short)(short.MinValue + 3)));
    }

    [Fact]
    public void Span_encode_and_decode_are_inverses_over_a_buffer()
    {
        var pcm = new byte[400];
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var sample = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 8000.0));
            pcm[i * 2] = (byte)sample;
            pcm[(i * 2) + 1] = (byte)(sample >> 8);
        }

        var encoded = new byte[pcm.Length / 2];
        G711.Encode(pcm, encoded);

        var decoded = new byte[pcm.Length];
        G711.Decode(encoded, decoded);

        for (var i = 0; i < encoded.Length; i++)
        {
            Assert.Equal(G711.Decode(encoded[i]), (short)(decoded[i * 2] | (decoded[(i * 2) + 1] << 8)));
        }
    }

}
