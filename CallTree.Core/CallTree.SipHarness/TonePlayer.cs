using CallTree.Telephony.Audio;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace CallTree.SipHarness;

/// <summary>
/// Sends one leg's tone as paced RTP for as long as it is alive.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as CallTree's own <c>PacedRtpRelay</c>: a <see cref="PeriodicTimer"/> at
/// the frame interval, one frame per tick, timestamps advanced by the frame's sample count rather than
/// read off a clock. A harness that sent audio in bursts would be testing CallTree's tolerance for a
/// badly behaved endpoint instead of testing CallTree, and the jitter numbers in its logs would be the
/// harness's own.
/// </para>
/// <para>
/// The constructor's <paramref name="silenceSource"/> is the part that is easy to leave out and
/// expensive to debug. <c>VoIPMediaSession.Start</c> unconditionally starts an
/// <see cref="AudioExtrasSource"/> silence timer on the leg, and it has no idea this class is also
/// sending on the same session. Both share one SSRC and one sequence counter, so the far end receives
/// two interleaved streams on two unrelated timestamp bases, half of them silence - which sounds like a
/// broken codec and analyses as noise. Re-sourcing to <see cref="AudioSourcesEnum.None"/> is what
/// actually disposes that timer; <c>PauseAudio</c> only sets a flag the timer never reads. CallTree
/// learned this the hard way on its relay paths - see <c>PromptPlayer.SuspendBackgroundSilence</c>.
/// </para>
/// </remarks>
internal sealed class TonePlayer : IAsyncDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    private readonly ToneSource _tone;
    private readonly VoIPMediaSession _session;
    private readonly ILogger _logger;
    private readonly string _label;
    private readonly PeriodicTimer _timer = new(FrameInterval);
    private readonly Task _pump;

    private uint _timestamp;
    private long _sent;
    private volatile bool _muted;

    public TonePlayer(
        string label, ToneSource tone, VoIPMediaSession session, AudioExtrasSource silenceSource, ILogger logger)
    {
        _label = label;
        _tone = tone;
        _session = session;
        _logger = logger;

        silenceSource.SetSource(new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

        _pump = PumpAsync();
    }

    public long FramesSent => Interlocked.Read(ref _sent);

    /// <summary>
    /// Stops sending audio without stopping the clock, for the span of a DTMF entry. RFC 4733 events go
    /// out on the same stream with the session's own timestamps, and overlapping them with a tone leaves
    /// the far end deciding between two timelines. Timestamps keep advancing, so the pause reads as an
    /// ordinary gap in the audio rather than as a jump when it ends.
    /// </summary>
    public void Mute() => _muted = true;

    public void Unmute() => _muted = false;

    private async Task PumpAsync()
    {
        while (await _timer.WaitForNextTickAsync())
        {
            var frame = _tone.NextFrame();

            if (!_muted)
            {
                try
                {
                    _session.SendRtpRaw(SDPMediaTypesEnum.audio, frame, _timestamp, 0, G711.PcmuPayloadType);
                    Interlocked.Increment(ref _sent);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "{Label}: tone frame not sent (the session is probably closing).", _label);
                }
            }

            _timestamp += ToneSource.SamplesPerFrame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Dispose();
        await _pump;
    }
}
