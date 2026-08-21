using System.Diagnostics;
using System.Text;
using CallTree.Telephony.Audio;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP.App;

namespace CallTree.Telephony;

/// <summary>Why a PIN attempt ended.</summary>
internal enum PinOutcome
{
    Accepted,
    Wrong,
    TimedOut,
}

/// <summary>
/// Collects a PIN from the Outbound (my cell) caller before the call is answered for recording.
/// </summary>
/// <remarks>
/// <para>
/// The point is not secrecy — the PIN travels as in-band DTMF over a trunk we do not control — but
/// raising the cost of a spoofed caller ID above "set a From header". Caller ID alone is otherwise the
/// only thing between a stranger and a path that answers automatically and records.
/// </para>
/// <para>
/// Multi-digit collection needs a debounce that the single-digit screening gate does not. One keypress
/// surfaces as several <see cref="SIPUserAgent.OnDtmfTone"/> events, because RFC 4733 retransmits the
/// end-of-event packet three times for redundancy; those repeats arrive within a packet interval or two
/// of each other, while a human pressing the same digit twice cannot come close. So a repeat of the same
/// digit inside <see cref="RepeatWindow"/> counts as the same keypress — which is what stops a PIN like
/// 1112 from collapsing to 12.
/// </para>
/// </remarks>
internal sealed class PinGate(
    SIPUserAgent userAgent,
    PromptPlayer prompts,
    string expectedPin,
    TimeSpan timeout,
    ILogger logger)
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>RFC 4733 event ID for '#', which ends entry early.</summary>
    private const byte HashKey = 11;

    public async Task<PinOutcome> RunAsync(Guid callId, CancellationToken cancellationToken)
    {
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var digits = new StringBuilder();
        var sinceLastTone = Stopwatch.StartNew();
        var gate = new Lock();
        byte? lastTone = null;

        void OnDtmf(byte tone, int duration)
        {
            lock (gate)
            {
                if (tone == lastTone && sinceLastTone.Elapsed < RepeatWindow)
                {
                    return;
                }

                lastTone = tone;
                sinceLastTone.Restart();

                if (tone == HashKey)
                {
                    entered.TrySetResult(digits.ToString());
                    return;
                }

                if (tone > 9)
                {
                    // '*' and the A-D tones are not PIN material; ignore rather than fail the attempt.
                    return;
                }

                digits.Append((char)('0' + tone));

                if (digits.Length >= expectedPin.Length)
                {
                    entered.TrySetResult(digits.ToString());
                }
            }
        }

        userAgent.OnDtmfTone += OnDtmf;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            await prompts.PlayAsync(PromptNames.PinRequest, entered.Task, deadline.Token);

            var attempt = await WaitAsync(entered.Task, deadline.Token);

            if (attempt is null)
            {
                logger.LogWarning(
                    "Call {CallId}: no PIN within {Timeout:0.#}s - refusing the outbound path.",
                    callId, timeout.TotalSeconds);
                await prompts.PlayAsync(PromptNames.Rejected, interrupt: null, cancellationToken);
                return PinOutcome.TimedOut;
            }

            // Ordinal comparison, not constant-time, and deliberately not pretending to be: the channel
            // here is a phone call at roughly one attempt per ten seconds, so timing is nowhere near the
            // weak link and hardening it would be theatre.
            if (!string.Equals(attempt, expectedPin, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Call {CallId}: wrong PIN ({Length} digits) - refusing the outbound path. The caller ID matched "
                    + "the configured mobile, so this is a misdial or a spoofed From header.",
                    callId, attempt.Length);
                await prompts.PlayAsync(PromptNames.Rejected, interrupt: null, cancellationToken);
                return PinOutcome.Wrong;
            }

            logger.LogInformation("Call {CallId}: PIN accepted.", callId);
            return PinOutcome.Accepted;
        }
        finally
        {
            userAgent.OnDtmfTone -= OnDtmf;
            prompts.Cancel();
        }
    }

    private static async Task<string?> WaitAsync(Task<string> entered, CancellationToken cancellationToken) =>
        await Task.WhenAny(entered, PromptPlayer.WhenCancelled(cancellationToken)) == entered ? await entered : null;
}
