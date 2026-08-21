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

    public async Task AnswerAsync(
        Guid callId,
        DateTimeOffset when,
        bool requireScreening,
        CancellationToken cancellationToken = default)
    {
        var call = await GetRequiredAsync(callId, cancellationToken);
        call.Answer(when, requireScreening);
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The Outbound-path PIN gate was cleared; the call carries on without bridging.</summary>
    public async Task PassScreeningAsync(Guid callId, DateTimeOffset when, CancellationToken cancellationToken = default)
    {
        var call = await GetRequiredAsync(callId, cancellationToken);
        call.PassScreening(when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task StartRecordingAsync(
        Guid callId,
        string relativePath,
        ChannelLayout channelLayout,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var call = await GetRequiredAsync(callId, cancellationToken);
        call.StartRecording(relativePath, channelLayout, when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records the finished recording's length and size. Runs after the call has usually already been
    /// ended, since the file is only measurable once it is closed — hence no terminal-state check here.
    /// </summary>
    public async Task FinalizeRecordingAsync(
        Guid callId,
        double durationSeconds,
        long sizeBytes,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var call = await GetRequiredAsync(callId, cancellationToken);
        call.FinalizeRecording(durationSeconds, sizeBytes, when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records the outcome of the IVR screening gate. Phase 2 ends the call either way; Phase 4 will
    /// replace the <see cref="ScreeningOutcome.Passed"/> branch with dialing the cell and bridging.
    /// </summary>
    public async Task ScreeningCompletedAsync(
        Guid callId,
        ScreeningOutcome outcome,
        DateTimeOffset when,
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
            leg.End(when, HangupInitiator.Local);
        }

        if (outcome == ScreeningOutcome.Passed)
        {
            call.CompleteScreening(when, reason);
        }
        else
        {
            call.ScreenOut(when, reason);
        }

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
