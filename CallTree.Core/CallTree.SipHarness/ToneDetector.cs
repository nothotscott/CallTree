using CallTree.Telephony.Audio;

namespace CallTree.SipHarness;

/// <summary>What one leg turned out to have been hearing.</summary>
/// <param name="Frames">RTP audio frames accepted. Zero means no media arrived at all.</param>
/// <param name="Rms">Loudness of the analysis window, 0-1. Distinguishes silence from the wrong tone.</param>
/// <param name="DominantHz">The loudest candidate tone, or null when nothing stood out.</param>
/// <param name="Confidence">
/// How far the winner beat the runner-up. Near 1.0 means one tone and nothing else; near 0 means the
/// window held noise, a prompt, or several tones at once.
/// </param>
/// <param name="Present">
/// Every harness tone audible in the window, loudest first - which is a different question from
/// <paramref name="DominantHz"/> and the reason both exist. A bridged leg should carry exactly one tone,
/// so "which one" is the whole check and a second tone is a crossed call. The Outbound path's mono
/// recording is supposed to carry two, because a proxy dial mixes the third party into the same file
/// rather than opening a second Recording - there the check is "are both of them in here", and asking
/// for a single dominant tone would fail the feature working correctly.
/// </param>
internal readonly record struct HeardAudio(
    long Frames, double Rms, int? DominantHz, double Confidence, IReadOnlyList<int> Present)
{
    public string Describe()
    {
        if (Frames == 0)
        {
            return "no media";
        }

        if (Present.Count == 0)
        {
            return $"{Frames} frames, no tone (rms {Rms:0.000})";
        }

        if (Present.Count > 1)
        {
            return $"{Frames} frames, {string.Join(" + ", Present)} Hz mixed (rms {Rms:0.000})";
        }

        return $"{Frames} frames, {Present[0]} Hz (confidence {Confidence:0.00}, rms {Rms:0.000})";
    }

    public bool Contains(int? hz) => hz is { } wanted && Present.Contains(wanted);
}

/// <summary>
/// Listens to one leg's received RTP and works out which harness tone, if any, is on it.
/// </summary>
/// <remarks>
/// Only the most recent <see cref="WindowSeconds"/> of audio is kept, and that is the design rather than
/// a memory saving. A call carries several seconds of prompts, ringback and screening before the two
/// legs are joined, and all of it is real audio that is not a tone. Analysing the whole call would
/// average the answer with everything that came before it; analysing the tail asks the only question
/// worth asking, which is what was flowing once the call had settled into its steady state.
///
/// Detection is Goertzel rather than an FFT: the candidate frequencies are known up front (the harness
/// assigned them), so there is no reason to compute a whole spectrum to look at a dozen bins. It is also
/// about fifteen lines, which suits a project that would rather read its own DSP than import it.
/// </remarks>
internal sealed class ToneDetector(IReadOnlyList<int> candidates, int windowSeconds = 3)
{
    private const int SampleRate = G711.ClockRate;

    public const int WindowSeconds = 3;

    private readonly short[] _window = new short[SampleRate * windowSeconds];
    private readonly Lock _gate = new();

    private int _next;
    private int _filled;
    private long _frames;

    /// <summary>
    /// Accepts one received RTP payload. Non-PCMU payloads are dropped for the same reason the recorder
    /// drops them: payload 101 is RFC 4733 DTMF sharing the session, and decoding it as audio would put
    /// a burst of broadband noise into the analysis window on every keypress.
    /// </summary>
    public void Accept(int payloadType, byte[]? payload)
    {
        if (payloadType != G711.PcmuPayloadType || payload is not { Length: > 0 })
        {
            return;
        }

        lock (_gate)
        {
            _frames++;

            foreach (var encoded in payload)
            {
                _window[_next] = G711.Decode(encoded);
                _next = (_next + 1) % _window.Length;
                if (_filled < _window.Length)
                {
                    _filled++;
                }
            }
        }
    }

    public HeardAudio Result()
    {
        short[] samples;
        long frames;

        lock (_gate)
        {
            frames = _frames;
            samples = new short[_filled];

            // Unwrap the ring so the samples are in time order. Goertzel is phase-sensitive, and a
            // window spliced together out of order has a discontinuity in the middle of it.
            var start = _filled == _window.Length ? _next : 0;
            for (var i = 0; i < _filled; i++)
            {
                samples[i] = _window[(start + i) % _window.Length];
            }
        }

        if (frames == 0 || samples.Length < SampleRate / 4)
        {
            return new HeardAudio(frames, 0, null, 0, []);
        }

        double sumSquares = 0;
        foreach (var sample in samples)
        {
            var normalised = sample / (double)short.MaxValue;
            sumSquares += normalised * normalised;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length);

        // Well under the harness's own 0.3 amplitude (rms ~0.21), but far enough above the digital
        // silence a leg with nothing on it carries that the two can never be confused.
        if (rms < 0.02)
        {
            return new HeardAudio(frames, rms, null, 0, []);
        }

        var scored = candidates
            .Select(candidate => (Hz: candidate, Power: Goertzel(samples, candidate)))
            .OrderByDescending(entry => entry.Power)
            .ToList();

        var best = scored[0];
        var second = scored.Count > 1 ? scored[1].Power : 0;

        // A tenth of the strongest peak. A tone that is genuinely absent scores spectral leakage, which
        // over a window this long is orders of magnitude down - so the cut sits in a wide empty gap and
        // its exact value does not matter. Two tones deliberately mixed at equal amplitude land within a
        // factor of two of each other and are both kept.
        var floor = best.Power / 10;
        var present = scored.Where(entry => entry.Power >= floor && entry.Power > 0).Select(entry => entry.Hz).ToList();

        // The winner has to actually win before it is called dominant. One clean tone leaves every other
        // candidate in the noise, so the ratio is near 1; two comparable peaks give a ratio near 0, and
        // on a leg that is supposed to carry one voice that is a crossed call.
        var confidence = best.Power <= 0 ? 0 : 1 - (second / best.Power);

        return new HeardAudio(
            frames, rms, confidence < 0.5 ? null : best.Hz, confidence, present);
    }

    /// <summary>
    /// Energy at one frequency, by the Goertzel algorithm: a two-tap recurrence that is exactly one bin
    /// of a DFT. The recurrence runs over the whole window accumulating two state values, and the last
    /// two are all that is needed to recover the magnitude - which is why this costs one multiply per
    /// sample per candidate rather than an entire transform.
    /// </summary>
    private static double Goertzel(ReadOnlySpan<short> samples, int frequencyHz)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequencyHz / SampleRate);
        double previous = 0, beforeThat = 0;

        foreach (var sample in samples)
        {
            var current = (sample / (double)short.MaxValue) + (coefficient * previous) - beforeThat;
            beforeThat = previous;
            previous = current;
        }

        var power = (previous * previous) + (beforeThat * beforeThat) - (coefficient * previous * beforeThat);
        return power / samples.Length;
    }
}
