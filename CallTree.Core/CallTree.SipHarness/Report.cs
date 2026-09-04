using System.Text;

namespace CallTree.SipHarness;

/// <summary>
/// Turns a run's legs into a verdict.
/// </summary>
/// <remarks>
/// The pairing rule is the only interesting part. Every caller played a tone nobody else played, so for
/// each caller there must be exactly one far-end leg that heard <em>that</em> tone, and that same leg's
/// own tone must be what the caller heard back. Two callers whose bridges were crossed still pass every
/// "did audio flow" check ever written and fail this one immediately, because caller 1 would be hearing
/// caller 2's mobile.
///
/// Zero far-end legs is not automatically a failure - the Outbound and Screened scenarios are supposed
/// to produce none - so what counts as a pass is scenario-specific and stated per scenario rather than
/// inferred from the counts.
/// </remarks>
internal static class Report
{
    public static bool Print(
        HarnessOptions options,
        IReadOnlyList<LegAudit> callers,
        IReadOnlyList<LegAudit> farEnd,
        IReadOnlyList<RecordedAudio> recordings)
    {
        var output = new StringBuilder();
        output.AppendLine();
        output.AppendLine($"=== {options.Scenario} x{options.Calls} ===");
        output.AppendLine();

        foreach (var leg in callers.Concat(farEnd))
        {
            output.AppendLine($"  {leg.Label}");
            output.AppendLine($"      number     {leg.Number}");
            output.AppendLine($"      call-id    {leg.SipCallId}");
            output.AppendLine($"      outcome    {leg.Outcome}{(leg.Error is null ? "" : $" ({leg.Error})")}");
            output.AppendLine($"      answered   {(leg.TimeToAnswer is { } wait ? $"after {wait.TotalSeconds:0.0}s" : "never")}");
            output.AppendLine($"      played     {(leg.PlayedHz is null ? "nothing" : $"{leg.PlayedHz} Hz, {leg.FramesSent} frames")}");
            output.AppendLine($"      heard      {leg.Heard.Describe()}");
            output.AppendLine();
        }

        var failures = new List<string>();

        var peak = PeakConcurrency(callers);
        if (options.Calls > 1)
        {
            output.AppendLine($"  peak simultaneous callers: {peak} of {options.Calls}");
            output.AppendLine();

            if (peak < options.Calls)
            {
                // The failure this tool was built to catch, and the one every other check misses. A stack
                // that answers calls strictly one at a time still handles each of them perfectly - the
                // audio pairs up, the recordings are right, nothing is crossed - because there was only
                // ever one call in flight to get wrong. What actually happened is that the later INVITEs
                // sat unanswered until an earlier call ended and were picked up off SIP retransmission,
                // which on a real line is a caller listening to silence and giving up.
                failures.Add(
                    $"only {peak} call(s) were ever up at once out of {options.Calls} - the rest were "
                    + "serialised. Look at the per-leg answer times: a call that waited for another to "
                    + "finish was not queued deliberately, its INVITE went unanswered until the line freed up.");
            }
        }

        switch (options.Scenario)
        {
            case Scenario.Inbound:
                CheckAnswered(callers, failures);
                CheckPairs(callers, farEnd, failures);
                break;

            case Scenario.Outbound:
                CheckAnswered(callers, failures);

                // Nothing plays a tone back at an Outbound-source caller unless a proxy dial joined one,
                // so the live check is only that media flowed at all - CallTree is listening and
                // recording, not talking. The recording check below is what actually proves the audio
                // arrived intact.
                foreach (var caller in callers.Where(c => c.Answered && c.Heard.Frames == 0))
                {
                    failures.Add($"{caller.Label} received no RTP at all - CallTree answered but sent nothing back.");
                }

                if (options.ProxyNumber is { Length: > 0 })
                {
                    CheckPairs(callers, farEnd, failures);
                }
                else if (farEnd.Count > 0)
                {
                    failures.Add($"{farEnd.Count} outbound leg(s) were placed on a call with no proxy dial.");
                }

                break;

            case Scenario.Screened:
                foreach (var caller in callers.Where(c => !c.Answered))
                {
                    failures.Add($"{caller.Label} was never answered; the screening gate needs an answered call to run.");
                }

                if (farEnd.Count > 0)
                {
                    failures.Add($"a screened-out caller reached the mobile ({farEnd.Count} leg(s)) - the gate let them through.");
                }

                break;

            case Scenario.Missed:
                if (farEnd.Count != callers.Count)
                {
                    failures.Add($"{callers.Count} caller(s) passed screening but {farEnd.Count} leg(s) were placed to the mobile.");
                }

                foreach (var leg in farEnd.Where(l => l.Answered))
                {
                    failures.Add($"{leg.Label} was answered, but this scenario is supposed to leave it ringing.");
                }

                break;
        }

        if (options.RecordingsRoot is not null)
        {
            output.AppendLine($"  recordings written under {options.RecordingsRoot}");

            // Only two of the four scenarios are supposed to produce a file. A screened-out caller never
            // reaches InProgress, and a missed call never gets a second leg, so a recording appearing on
            // either path is itself the bug - "Recording is a fact, never a status" cuts both ways.
            var shouldRecord = options.Scenario is Scenario.Inbound or Scenario.Outbound;

            if (recordings.Count == 0)
            {
                output.AppendLine("      (none)");

                if (shouldRecord)
                {
                    failures.Add("no recording was written, but this scenario reaches a state that records.");
                }
            }
            else if (!shouldRecord)
            {
                failures.Add(
                    $"{recordings.Count} recording(s) were written on a call that never reached InProgress.");
            }

            foreach (var recording in recordings)
            {
                var layout = recording.Channels == 2 ? "stereo" : "mono";
                output.AppendLine($"      {recording.Path}  {recording.DurationSeconds:0.0}s {layout}");

                for (var channel = 0; channel < recording.Tones.Count; channel++)
                {
                    var name = recording.Channels == 2 ? (channel == 0 ? "caller " : "mobile ") : "mono   ";
                    output.AppendLine($"          {name} {recording.Tones[channel].Describe()}");
                }

                if (recording.Tones.All(tone => tone.Present.Count == 0))
                {
                    failures.Add($"{recording.Path} holds no harness tone on any channel.");
                }
            }

            if (shouldRecord)
            {
                CheckRecordedPairs(callers, farEnd, recordings, failures);
            }

            output.AppendLine();
        }

        if (failures.Count == 0)
        {
            output.AppendLine("  PASS");
        }
        else
        {
            output.AppendLine("  FAIL");
            foreach (var failure in failures)
            {
                output.AppendLine($"      - {failure}");
            }
        }

        Console.WriteLine(output.ToString());
        return failures.Count == 0;
    }

    /// <summary>
    /// The largest number of callers that were live at the same instant.
    /// </summary>
    /// <remarks>
    /// A sweep over the start and end of every answered call rather than a pairwise overlap test: pairwise
    /// answers "did any two overlap", which two overlapping calls plus a third straggler would pass. The
    /// question worth asking is how many were up at the busiest moment, and that is what a real trunk's
    /// concurrency limit is counted in too.
    /// </remarks>
    private static int PeakConcurrency(IReadOnlyList<LegAudit> callers)
    {
        var events = new List<(DateTimeOffset At, int Delta)>();

        foreach (var caller in callers)
        {
            if (caller.AnsweredAt is { } answered && caller.EndedAt is { } ended)
            {
                events.Add((answered, 1));
                events.Add((ended, -1));
            }
        }

        // Ends before starts at the same instant, so two calls that merely touch are not counted as
        // having overlapped.
        events.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Delta.CompareTo(b.Delta));

        int live = 0, peak = 0;
        foreach (var (_, delta) in events)
        {
            live += delta;
            peak = Math.Max(peak, live);
        }

        return peak;
    }

    private static void CheckAnswered(IReadOnlyList<LegAudit> callers, List<string> failures)
    {
        foreach (var caller in callers.Where(c => !c.Answered))
        {
            failures.Add($"{caller.Label} was not answered ({caller.Outcome}).");
        }
    }

    private static void CheckPairs(
        IReadOnlyList<LegAudit> callers, IReadOnlyList<LegAudit> farEnd, List<string> failures)
    {
        foreach (var caller in callers.Where(c => c.Answered))
        {
            var partners = farEnd.Where(f => f.Heard.DominantHz == caller.PlayedHz).ToList();

            if (partners.Count == 0)
            {
                failures.Add(
                    $"{caller.Label} played {caller.PlayedHz} Hz but no leg to the far end ever heard it - "
                    + "the caller's audio is not reaching the other side.");
                continue;
            }

            if (partners.Count > 1)
            {
                failures.Add(
                    $"{caller.Label}'s {caller.PlayedHz} Hz tone was heard on {partners.Count} far-end legs - "
                    + "one caller is being relayed into more than one call.");
                continue;
            }

            var partner = partners[0];
            if (caller.Heard.DominantHz != partner.PlayedHz)
            {
                failures.Add(
                    $"{caller.Label} is paired with {partner.Label} (which heard {caller.PlayedHz} Hz) but heard "
                    + $"{Describe(caller.Heard.DominantHz)} back instead of {partner.PlayedHz} Hz - the return "
                    + "direction is crossed.");
            }
        }
    }

    private static void CheckRecordedPairs(
        IReadOnlyList<LegAudit> callers,
        IReadOnlyList<LegAudit> farEnd,
        IReadOnlyList<RecordedAudio> recordings,
        List<string> failures)
    {
        // A stereo recording is one leg per channel, caller first. The caller's own tone therefore has to
        // be on channel 0 of exactly one file: a recorder handed the two streams the wrong way round
        // still writes a perfectly playable call in which both people are on the wrong side.
        foreach (var caller in callers.Where(c => c.Answered && c.PlayedHz is not null))
        {
            var onCallerChannel = recordings
                .Where(r => r.Tones.Count > 0 && r.Tones[0].Contains(caller.PlayedHz))
                .ToList();

            if (onCallerChannel.Count == 0)
            {
                var elsewhere = recordings.Any(r => r.Tones.Skip(1).Any(t => t.Contains(caller.PlayedHz)));
                failures.Add(elsewhere
                    ? $"{caller.Label}'s {caller.PlayedHz} Hz tone was recorded, but on the far end's channel - the "
                      + "stereo legs are swapped."
                    : $"{caller.Label}'s {caller.PlayedHz} Hz tone appears in no recording.");
            }
            else if (onCallerChannel.Count > 1)
            {
                failures.Add(
                    $"{caller.Label}'s {caller.PlayedHz} Hz tone appears on the caller channel of "
                    + $"{onCallerChannel.Count} recordings - one call is being written into several files.");
            }
        }

        foreach (var leg in farEnd.Where(f => f.Answered && f.PlayedHz is not null))
        {
            if (!recordings.Any(r => r.Tones.Any(t => t.Contains(leg.PlayedHz))))
            {
                failures.Add($"{leg.Label}'s {leg.PlayedHz} Hz tone appears in no recording.");
            }
        }
    }

    private static string Describe(int? hz) => hz is null ? "nothing recognisable" : $"{hz} Hz";
}
