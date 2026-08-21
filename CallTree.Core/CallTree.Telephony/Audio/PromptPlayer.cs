using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace CallTree.Telephony.Audio;

/// <summary>
/// Streams prompts into a live call over the silence the audio source otherwise generates.
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

    /// <summary>A task that completes (never faults) when the token is cancelled.</summary>
    public static Task WhenCancelled(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }
}
