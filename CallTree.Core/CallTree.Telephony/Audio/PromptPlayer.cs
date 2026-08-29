using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace CallTree.Telephony.Audio;

/// <summary>
/// Streams prompts into a live call over the silence the audio source otherwise generates, and owns
/// stopping that silence for the legs a <see cref="PacedRtpRelay"/> takes over.
/// </summary>
/// <param name="isCallActive">
/// Checked before each playback. A prompt sent after the dialogue has gone leaves the audio source's 20 ms
/// timer firing at a closed RTP session, which is where "SendRtpRaw was called ... on a closed RTP
/// session" comes from.
/// </param>
internal sealed class PromptPlayer(AudioExtrasSource audioSource, PromptLibrary prompts, Func<bool> isCallActive)
{
    /// <summary>
    /// Plays a named prompt, stopping early if <paramref name="interrupt"/> completes (barge-in) or the
    /// token is cancelled (the caller hung up). A prompt that did not load is silently skipped — the
    /// missing-prompt warning belongs at startup and on the status page, not once per call.
    /// </summary>
    public async Task PlayAsync(string name, Task? interrupt, CancellationToken cancellationToken)
    {
        if (!prompts.TryGet(name, out var prompt) || !isCallActive())
        {
            return;
        }

        var rate = prompt.SampleRate == 16000
            ? AudioSamplingRatesEnum.Rate16KHz
            : AudioSamplingRatesEnum.Rate8KHz;

        using var stream = new MemoryStream(prompt.Samples, writable: false);
        var playback = audioSource.SendAudioFromStream(stream, rate);

        var stop = interrupt is null
            ? WhenCancelled(cancellationToken)
            : Task.WhenAny(interrupt, WhenCancelled(cancellationToken));

        if (await Task.WhenAny(playback, stop) != playback)
        {
            audioSource.CancelSendAudioFromStream();
        }
    }

    public void Cancel() => audioSource.CancelSendAudioFromStream();

    /// <summary>
    /// Stops the continuous background silence this leg's audio source generates, for as long as
    /// something else is writing to the leg's RTP stream directly - that is, a <see cref="PacedRtpRelay"/>.
    /// </summary>
    /// <remarks>
    /// The silence is not "nothing": it is a real 20 ms timer sending real PCMU packets, and
    /// <c>VoIPMediaSession.Start</c> starts it unconditionally for the life of the session. That is right
    /// while prompts are the only thing feeding the leg, and wrong the moment a relay is also sending on
    /// the same leg with <c>SendRtpRaw</c>. Both share one SSRC and one sequence-number counter, so the
    /// far end receives about 100 packets a second alternating between two unrelated RTP timestamp bases
    /// - the silence timer's own <c>LocalTrack.Timestamp</c>, and the other leg's clock carried through by
    /// the relay - with every second packet silence. A receiver scheduling playout from those timestamps
    /// has no consistent timeline to work from, and RTCP sender reports alternate bases too, because they
    /// echo whichever packet went out last. That is choppy, garbled audio on the relayed direction while
    /// the recording sounds perfect - the recording taps *received* packets and never sees any of it.
    ///
    /// <c>AudioExtrasSource.PauseAudio</c> cannot do this: it only sets a flag the silence timer never
    /// reads. Re-sourcing to <see cref="AudioSourcesEnum.None"/> is what actually disposes that timer,
    /// and unlike <c>CloseAudio</c> it is reversible and leaves <see cref="PlayAsync"/> working, because
    /// prompt playback runs on a separate timer.
    /// </remarks>
    public void SuspendBackgroundSilence() =>
        audioSource.SetSource(new AudioSourceOptions { AudioSource = AudioSourcesEnum.None });

    /// <summary>
    /// Restores the silence stopped by <see cref="SuspendBackgroundSilence"/>. Only needed where the leg
    /// outlives whatever was relaying into it - the proxy dial ending while the operator's own call
    /// carries on. A leg that is being torn down is deliberately left suspended: restarting a 20 ms timer
    /// against a session about to close is exactly what produces "SendRtpRaw was called ... on a closed
    /// RTP session".
    /// </summary>
    public void ResumeBackgroundSilence() =>
        audioSource.SetSource(new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

    /// <summary>A task that completes (never faults) when the token is cancelled.</summary>
    public static Task WhenCancelled(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }
}
