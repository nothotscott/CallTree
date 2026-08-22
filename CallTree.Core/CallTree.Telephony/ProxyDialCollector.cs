using System.Diagnostics;
using System.Text;
using CallTree.Domain.ValueObjects;
using CallTree.Telephony.Audio;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP.App;

namespace CallTree.Telephony;

/// <summary>
/// Listens for the operator dialing <c>*{NUMBER}#</c> mid-call to trigger an outbound proxy dial from the
/// DID (an anonymised outbound call, in place of adding a party via the handset's own three-way merge).
/// </summary>
/// <remarks>
/// Unlike <see cref="ScreeningGate"/>/<see cref="PinGate"/>, which run once and either pass or fail the
/// whole call, this is a persistent listener: a bad entry (empty digits, or something that doesn't parse
/// as a number) logs and resets to idle rather than ending anything, and a caller can call
/// <see cref="WaitForDialSequenceAsync"/> again afterwards to keep listening for the next attempt. There
/// is no timeout on collection itself - only <paramref name="cancellationToken"/> (via the constructor's
/// <see cref="SIPUserAgent"/>'s eventual hangup) ends the wait.
/// </remarks>
internal sealed class ProxyDialCollector(SIPUserAgent userAgent, ILogger logger)
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>RFC 4733 event IDs: 0–9 are the digits, 10 is '*', 11 is '#'.</summary>
    private const byte StarKey = 10;

    private const byte HashKey = 11;

    /// <summary>Waits for the next complete, valid <c>*{NUMBER}#</c> sequence.</summary>
    public async Task<PhoneNumber> WaitForDialSequenceAsync(CancellationToken cancellationToken)
    {
        var found = new TaskCompletionSource<PhoneNumber>(TaskCreationOptions.RunContinuationsAsynchronously);
        var digits = new StringBuilder();
        var collecting = false;
        var sinceLastTone = Stopwatch.StartNew();
        var gate = new Lock();
        byte? lastTone = null;

        void OnDtmf(byte tone, int duration)
        {
            lock (gate)
            {
                // Same debounce as PinGate: RFC 4733 retransmits the end-of-event packet three times, so
                // a repeat of the same digit within a packet interval or two is one keypress, not several.
                if (tone == lastTone && sinceLastTone.Elapsed < RepeatWindow)
                {
                    return;
                }

                lastTone = tone;
                sinceLastTone.Restart();

                if (tone == StarKey)
                {
                    // (Re)starts entry - matches how real feature codes work, and lets a mis-dial be
                    // corrected by just pressing '*' again rather than needing the whole call to end.
                    collecting = true;
                    digits.Clear();
                    return;
                }

                if (!collecting)
                {
                    // Ordinary DTMF outside a *...# sequence is none of this collector's business.
                    return;
                }

                if (tone == HashKey)
                {
                    collecting = false;
                    var entered = digits.ToString();
                    digits.Clear();

                    if (PhoneNumber.TryParse(entered, out var number))
                    {
                        found.TrySetResult(number);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Proxy dial: '{Digits}' is not a valid number - ignoring, still listening.", entered);
                    }

                    return;
                }

                if (tone <= 9)
                {
                    digits.Append((char)('0' + tone));
                }

                // '*' and '#' are handled above; '*' mid-entry restarts (that branch runs first), anything
                // else above 9 (A-D tones) is not dial material and is silently ignored.
            }
        }

        userAgent.OnDtmfTone += OnDtmf;
        try
        {
            await Task.WhenAny(found.Task, PromptPlayer.WhenCancelled(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return await found.Task;
        }
        finally
        {
            userAgent.OnDtmfTone -= OnDtmf;
        }
    }
}
