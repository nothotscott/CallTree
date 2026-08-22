using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Application.Calls;

/// <summary>Executes call commands, each in its own unit of work.</summary>
public interface ICallCommands
{
    /// <summary>Creates the call aggregate and returns its identifier.</summary>
    Task<Guid> StartAsync(StartCall command, CancellationToken cancellationToken = default);

    Task ExecuteAsync(CallCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs each command against a <see cref="CallLifecycleService"/> resolved from a fresh DI scope.
/// </summary>
/// <remarks>
/// One scope per command is deliberate. Telephony events arrive on SIPSorcery's threads at arbitrary times
/// over the life of a call, so there is no ambient scope to join and no safe way to share a
/// <c>DbContext</c> between them. This type is a singleton; it holds nothing but the scope factory.
/// </remarks>
public sealed class ScopedCallCommands(IServiceScopeFactory scopeFactory) : ICallCommands
{
    public async Task<Guid> StartAsync(StartCall command, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<CallLifecycleService>();

        return await lifecycle.StartAsync(
            command.Source,
            command.Classification,
            command.CallerNumber,
            command.RawCallerId,
            command.SipCallId,
            command.When,
            cancellationToken);
    }

    public async Task ExecuteAsync(CallCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<CallLifecycleService>();

        switch (command)
        {
            case AnswerCall answer:
                await lifecycle.AnswerAsync(answer.CallId, answer.When, answer.RequireScreening, cancellationToken);
                break;

            case PassScreening passed:
                await lifecycle.PassScreeningAsync(passed.CallId, passed.When, cancellationToken);
                break;

            case BeginDialing dialing:
                await lifecycle.BeginDialingAsync(
                    dialing.CallId, dialing.Target, dialing.SipCallId, dialing.When, cancellationToken);
                break;

            case BridgeCall bridge:
                await lifecycle.BridgeAsync(bridge.CallId, bridge.When, cancellationToken);
                break;

            case StartRecording start:
                await lifecycle.StartRecordingAsync(
                    start.CallId, start.RelativePath, start.ChannelLayout, start.When, cancellationToken);
                break;

            case FinalizeRecording finalize:
                await lifecycle.FinalizeRecordingAsync(
                    finalize.CallId, finalize.DurationSeconds, finalize.SizeBytes, finalize.When, cancellationToken);
                break;

            case RecordScreeningOutcome screening:
                await lifecycle.ScreeningCompletedAsync(
                    screening.CallId, screening.Outcome, screening.When, screening.Reason, cancellationToken);
                break;

            case EndCall end:
                await lifecycle.EndAsync(end.CallId, end.When, end.Initiator, end.Reason, cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command), $"No handler is registered for {command.GetType().Name}.");
        }
    }
}
