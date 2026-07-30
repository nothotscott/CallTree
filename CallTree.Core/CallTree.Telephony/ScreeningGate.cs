using CallTree.Application.Calls;
using CallTree.Telephony.Audio;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace CallTree.Telephony;

/// <summary>
/// The inbound spam gate: play a prompt, wait for the caller to press a digit, decide whether they pass.
/// </summary>
/// <remarks>
/// DTMF arrives as RFC 4733 telephone-events, surfaced by SIPSorcery as <see cref="SIPUserAgent.OnDtmfTone"/>.
/// A single keypress can raise that event more than once (the tone spans several RTP packets), so the first
/// digit is latched and the rest ignored. Callers who already know the drill can barge in over the prompt.
/// </remarks>
internal sealed class ScreeningGate(
    SIPUserAgent userAgent,
    AudioExtrasSource audioSource,
    PromptLibrary prompts,
    byte expectedDigit,
    TimeSpan timeout,
    ILogger logger)
{
    public async Task<(ScreeningOutcome Outcome, byte? Digit)> RunAsync(Guid callId, CancellationToken cancellationToken)
    {
        var firstDigit = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnDtmf(byte tone, int duration)
        {
            // TrySetResult keeps the first tone and discards the repeats for the same keypress.
            if (firstDigit.TrySetResult(tone))
            {
                logger.LogInformation("Call {CallId}: DTMF digit {Digit} ({Duration}ms)", callId, Describe(tone), duration);
            }
        }

        userAgent.OnDtmfTone += OnDtmf;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            await PlayAsync(PromptNames.Greeting, firstDigit.Task, deadline.Token);

            // The prompt may have finished before the caller pressed anything; keep listening until the
            // deadline. Barge-in during the prompt already resolved the task, so this returns immediately.
            var digit = await WaitForDigitAsync(firstDigit.Task, deadline.Token);

            if (digit is null)
            {
                logger.LogInformation("Call {CallId}: no digit within {Timeout:0.#}s - screened out", callId, timeout.TotalSeconds);
                await PlayAsync(PromptNames.Rejected, interrupt: null, cancellationToken);
                return (ScreeningOutcome.TimedOut, null);
            }

            if (digit != expectedDigit)
            {
                logger.LogInformation("Call {CallId}: expected {Expected} but got {Digit} - screened out",
                    callId, Describe(expectedDigit), Describe(digit.Value));
                await PlayAsync(PromptNames.Rejected, interrupt: null, cancellationToken);
                return (ScreeningOutcome.WrongDigit, digit);
            }

            logger.LogInformation("Call {CallId}: caller pressed {Digit} - screening passed", callId, Describe(digit.Value));
            await PlayAsync(PromptNames.Accepted, interrupt: null, cancellationToken);
            return (ScreeningOutcome.Passed, digit);
        }
        finally
        {
            userAgent.OnDtmfTone -= OnDtmf;
            audioSource.CancelSendAudioFromStream();
        }
    }

    /// <summary>
    /// Plays a prompt, stopping early if <paramref name="interrupt"/> completes or the call drops.
    /// </summary>
    private async Task PlayAsync(string name, Task? interrupt, CancellationToken cancellationToken)
    {
        if (!prompts.TryGet(name, out var prompt) || !userAgent.IsCallActive)
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

    private static async Task<byte?> WaitForDigitAsync(Task<byte> digit, CancellationToken cancellationToken) =>
        await Task.WhenAny(digit, WhenCancelled(cancellationToken)) == digit ? await digit : null;

    /// <summary>A task that completes (never faults) when the token is cancelled.</summary>
    private static Task WhenCancelled(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    /// <summary>RFC 4733 event IDs: 0–9 are the digits, 10 is '*', 11 is '#'.</summary>
    private static string Describe(byte tone) => tone switch
    {
        <= 9 => tone.ToString(),
        10 => "*",
        11 => "#",
        _ => $"event {tone}",
    };
}
