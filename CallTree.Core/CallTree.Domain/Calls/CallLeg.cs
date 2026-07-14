using CallTree.Domain.ValueObjects;

namespace CallTree.Domain.Calls;

/// <summary>One SIP dialog within a <see cref="Call"/>. A bridged inbound call has two.</summary>
public class CallLeg
{
    public Guid Id { get; private set; }
    public LegDirection Direction { get; private set; }

    /// <summary>Normalized remote number, when the caller ID was parseable.</summary>
    public PhoneNumber? RemoteNumber { get; private set; }

    /// <summary>Verbatim caller ID as received (kept for spoofing forensics).</summary>
    public string RawCallerId { get; private set; } = "";

    public string SipCallId { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public HangupInitiator? HangupInitiator { get; private set; }

    private CallLeg()
    {
    }

    internal CallLeg(LegDirection direction, PhoneNumber? remoteNumber, string rawCallerId, string sipCallId, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        Direction = direction;
        RemoteNumber = remoteNumber;
        RawCallerId = rawCallerId;
        SipCallId = sipCallId;
        CreatedAt = createdAt;
    }

    public void MarkAnswered(DateTimeOffset when)
    {
        if (AnsweredAt is not null)
        {
            throw new InvalidOperationException($"Leg {Id} is already answered.");
        }

        AnsweredAt = when;
    }

    public void End(DateTimeOffset when, HangupInitiator initiator)
    {
        if (EndedAt is not null)
        {
            return;
        }

        EndedAt = when;
        HangupInitiator = initiator;
    }
}
