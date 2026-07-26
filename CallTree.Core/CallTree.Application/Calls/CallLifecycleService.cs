using CallTree.Application.Abstractions;
using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;

namespace CallTree.Application.Calls;

/// <summary>
/// Application-layer handler that drives the Call aggregate in response to telephony events.
/// The Telephony layer resolves this from a fresh DI scope per event.
/// </summary>
public class CallLifecycleService(ICallRepository repository)
{
    public async Task<Guid> StartAsync(
        CallSource source,
        SourceClassification classification,
        PhoneNumber? callerNumber,
        string rawCallerId,
        string sipCallId,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var call = Call.Start(source, classification, callerNumber, rawCallerId, sipCallId, when);
        await repository.AddAsync(call, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return call.Id;
    }

    public async Task AnswerAsync(Guid callId, DateTimeOffset when, CancellationToken cancellationToken = default)
    {
        var call = await GetRequiredAsync(callId, cancellationToken);
        call.Answer(when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Ends the call with the terminal state appropriate to its current status
    /// (Ringing→Failed, Screening→ScreenedOut, Dialing→Missed, InProgress→Completed).
    /// No-op if the call is already terminal.
    /// </summary>
    public async Task EndAsync(
        Guid callId,
        DateTimeOffset when,
        HangupInitiator initiator,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var call = await GetRequiredAsync(callId, cancellationToken);
        if (call.IsTerminal)
        {
            return;
        }

        foreach (var leg in call.Legs.Where(l => l.EndedAt is null))
        {
            leg.End(when, initiator);
        }

        switch (call.Status)
        {
            case CallStatus.Ringing:
                call.Fail(when, reason);
                break;
            case CallStatus.Screening:
                call.ScreenOut(when, reason);
                break;
            case CallStatus.Dialing:
                call.MarkMissed(when, reason);
                break;
            case CallStatus.InProgress:
                call.Complete(when, reason);
                break;
        }

        await repository.SaveChangesAsync(cancellationToken);
    }

    private async Task<Call> GetRequiredAsync(Guid callId, CancellationToken cancellationToken) =>
        await repository.GetByIdAsync(callId, cancellationToken)
            ?? throw new InvalidOperationException($"Call {callId} not found.");
}
