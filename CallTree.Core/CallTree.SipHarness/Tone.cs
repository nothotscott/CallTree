using CallTree.Telephony.Audio;

namespace CallTree.SipHarness;

/// <summary>
/// The harness's identity marker: every leg it owns plays one steady sine tone, and no two legs in a run
/// play the same one.
/// </summary>
/// <remarks>
/// This is what makes a concurrency test mean anything. "Audio flowed" is easy to satisfy by accident -
/// a bridge that crossed two calls over still moves packets in both directions, and both recordings still
/// have sound in them. A distinct frequency per leg turns every check into an identity check: the far end
/// of caller 3 must hear caller 3's tone and nothing else, and channel 1 of caller 3's recording must
/// contain it too. Crossed legs, a relay wired to the wrong session, a recorder handed the wrong stream -
/// all of them show up as the wrong number rather than as silence.
///
/// The frequencies are 320 Hz apart-ish (base 320, step 90) for two reasons. They sit inside the band a
/// PCMU call actually carries, so nothing is lost to the codec; and no tone in the series is a harmonic
/// of another, since doubling 320 + 90k gives 640 + 180k, which is never 320 + 90m for whole m. A
/// harmonic collision would let a loud leg's overtone masquerade as a quiet leg's fundamental, which is
/// the one way a frequency-based identity check can lie.
/// </remarks>
internal static class Tone
{
    private const int BaseHz = 320;
    private const int StepHz = 90;

    /// <summary>The tone assigned to the <paramref name="index"/>th leg the harness creates.</summary>
    public static int For(int index) => BaseHz + (index * StepHz);

    /// <summary>Every tone that could be in play, which is the candidate set the detector scores.</summary>
    public static IReadOnlyList<int> Series(int count) => [.. Enumerable.Range(0, count).Select(For)];
}

/// <summary>
/// Generates one leg's tone as 20 ms PCMU frames, carrying phase across frame boundaries.
/// </summary>
/// <remarks>
/// Phase continuity is not cosmetic. Restarting the sine at zero every frame puts a discontinuity into
/// the signal 50 times a second, which spreads energy across the whole spectrum - the detector then sees
/// a smear rather than a peak, and every leg starts to look a bit like every other leg. Keeping
/// <c>_phase</c> across calls is what makes the tone a single continuous sine that happens to be sent in
/// pieces.
/// </remarks>
internal sealed class ToneSource(int frequencyHz, double amplitude = 0.3)
{
    public const int SampleRate = G711.ClockRate;

    /// <summary>Samples in one 20 ms PCMU frame - the packetisation every SIP endpoint expects.</summary>
    public const int SamplesPerFrame = SampleRate / 50;

    private readonly double _radiansPerSample = 2 * Math.PI * frequencyHz / SampleRate;
    private double _phase;

    public int FrequencyHz => frequencyHz;

    /// <summary>The next 20 ms of audio, already mu-law encoded and ready to be an RTP payload.</summary>
    public byte[] NextFrame()
    {
        var frame = new byte[SamplesPerFrame];

        for (var i = 0; i < SamplesPerFrame; i++)
        {
            frame[i] = G711.Encode((short)(short.MaxValue * amplitude * Math.Sin(_phase)));

            _phase += _radiansPerSample;
            if (_phase >= 2 * Math.PI)
            {
                _phase -= 2 * Math.PI;
            }
        }

        return frame;
    }
}
