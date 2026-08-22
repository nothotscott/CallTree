using CallTree.Domain.Primitives;
using CallTree.Domain.ValueObjects;

namespace CallTree.Domain.Calls;

/// <summary>
/// Aggregate root recording the history and legal state transitions of one call.
/// Live SIP/RTP objects belong to the runtime CallSession in the Telephony layer, never here.
///
/// State machine:
///   Ringing → InProgress                        (Outbound source: answer = start talking)
///   Ringing → Screening → InProgress            (Outbound source with a PIN configured)
///   Ringing → Screening → Dialing → InProgress  (Inbound source: IVR gate, then bridge to cell)
///   Dialing → Missed                            (bridge leg never answered)
///   Terminal: Completed, ScreenedOut, Missed, Failed
///
/// Screening means "this caller is being gated", whichever path they are on — which is what lets a
/// failed PIN land in ScreenedOut rather than looking like a completed call in the log.
/// </summary>
public class Call : AggregateRoot
{
    public Guid Id { get; private set; }
    public CallSource Source { get; private set; }
    public SourceClassification SourceClassification { get; private set; }
    public CallStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string? TerminationReason { get; private set; }
    public Recording? Recording { get; private set; }

    private readonly List<CallLeg> _legs = [];
    public IReadOnlyList<CallLeg> Legs => _legs;

    public CallLeg InboundLeg => _legs.First(l => l.Direction == LegDirection.Inbound);
    public CallLeg? OutboundLeg => _legs.FirstOrDefault(l => l.Direction == LegDirection.Outbound);

    public bool IsTerminal => Status
        is CallStatus.Completed
        or CallStatus.ScreenedOut
        or CallStatus.Missed
        or CallStatus.Failed;

    private Call()
    {
    }

    public static Call Start(
        CallSource source,
        SourceClassification classification,
        PhoneNumber? callerNumber,
        string rawCallerId,
        string sipCallId,
        DateTimeOffset when)
    {
        var call = new Call
        {
            Id = Guid.NewGuid(),
            Source = source,
            SourceClassification = classification,
            Status = CallStatus.Ringing,
            StartedAt = when,
        };
        call._legs.Add(new CallLeg(LegDirection.Inbound, callerNumber, rawCallerId, sipCallId, when));
        call.Raise(new CallStarted(call.Id, source));
        return call;
    }

    /// <summary>
    /// The caller is connected to us.
    /// </summary>
    /// <param name="requireScreening">
    /// Whether the caller still has to get past a gate before the call proper begins. Inbound callers
    /// always do (the press-1 spam gate); an Outbound-source caller only does when a PIN is configured,
    /// which is why the decision is the caller's to state rather than something derived from
    /// <see cref="Source"/> here.
    /// </param>
    public void Answer(DateTimeOffset when, bool requireScreening)
    {
        EnsureStatus(CallStatus.Ringing);
        Status = requireScreening ? CallStatus.Screening : CallStatus.InProgress;
        AnsweredAt = when;
        InboundLeg.MarkAnswered(when);
        Raise(new CallAnswered(Id));
    }

    /// <summary>Caller passed the IVR gate; an outbound leg to my cell is being placed.</summary>
    public CallLeg BeginDialing(PhoneNumber target, string sipCallId, DateTimeOffset when)
    {
        EnsureStatus(CallStatus.Screening);
        Status = CallStatus.Dialing;
        var leg = new CallLeg(LegDirection.Outbound, target, target.Value, sipCallId, when);
        _legs.Add(leg);
        return leg;
    }

    /// <summary>The outbound leg answered; both legs are now bridged.</summary>
    public void Bridge(DateTimeOffset when)
    {
        EnsureStatus(CallStatus.Dialing);
        var leg = OutboundLeg ?? throw new InvalidOperationException($"Call {Id} has no outbound leg to bridge.");
        Status = CallStatus.InProgress;
        leg.MarkAnswered(when);
        Raise(new CallBridged(Id));
    }

    /// <summary>
    /// The caller cleared the gate and the call continues on this leg — the Outbound (my cell) path,
    /// where the caller is already the party to record and there is nothing to dial. The Inbound path
    /// goes through <see cref="BeginDialing"/> and <see cref="Bridge"/> instead.
    /// </summary>
    public void PassScreening(DateTimeOffset when)
    {
        EnsureStatus(CallStatus.Screening);
        Status = CallStatus.InProgress;
        AnsweredAt ??= when;
    }

    public Recording StartRecording(string filePath, ChannelLayout channelLayout, DateTimeOffset when)
    {
        EnsureStatus(CallStatus.InProgress);
        if (Recording is not null)
        {
            throw new InvalidOperationException($"Call {Id} already has a recording.");
        }

        Recording = new Recording(filePath, channelLayout, when);
        return Recording;
    }

    /// <summary>
    /// Closes out the recording once the file is on disk and its length is known. Tolerates a call with
    /// no recording, and one already finalized, because the hangup and error paths both reach here and
    /// losing the call's terminal state over a bookkeeping detail would be the worse failure.
    /// </summary>
    public void FinalizeRecording(double durationSeconds, long sizeBytes, DateTimeOffset when)
    {
        if (Recording is null || Recording.FinalizedAt is not null)
        {
            return;
        }

        Recording.MarkFinalized(durationSeconds, sizeBytes, when);
    }

    public void Complete(DateTimeOffset when, string? reason = null)
    {
        EnsureStatus(CallStatus.InProgress);
        End(CallStatus.Completed, when, reason);
    }

    /// <summary>Caller never passed the IVR gate (timeout, wrong key, or hung up).</summary>
    public void ScreenOut(DateTimeOffset when, string reason)
    {
        EnsureStatus(CallStatus.Screening);
        End(CallStatus.ScreenedOut, when, reason);
    }

    /// <summary>The outbound leg to my cell was never answered.</summary>
    public void MarkMissed(DateTimeOffset when, string reason)
    {
        EnsureStatus(CallStatus.Dialing);
        End(CallStatus.Missed, when, reason);
    }

    public void Fail(DateTimeOffset when, string reason)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException($"Call {Id} is already ended ({Status}).");
        }

        End(CallStatus.Failed, when, reason);
    }

    private void End(CallStatus terminalStatus, DateTimeOffset when, string? reason)
    {
        Status = terminalStatus;
        EndedAt = when;
        TerminationReason = reason;

        // Safety net: any leg the session didn't explicitly end is closed by us.
        foreach (var leg in _legs.Where(l => l.EndedAt is null))
        {
            leg.End(when, HangupInitiator.Local);
        }

        Raise(new CallEnded(Id, terminalStatus));
    }

    private void EnsureStatus(CallStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Call {Id} is {Status}, expected {expected}.");
        }
    }
}
