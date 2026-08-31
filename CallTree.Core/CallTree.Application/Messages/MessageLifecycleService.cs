using CallTree.Application.Abstractions;
using CallTree.Domain.Messages;
using CallTree.Domain.ValueObjects;

namespace CallTree.Application.Messages;

/// <summary>
/// Application-layer handler that drives the <see cref="Message"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Calls.CallLifecycleService"/> this is not reached through a command type and a
/// scope factory, because it does not need to be: every message write originates in an HTTP webhook
/// from the provider, which already has a DI scope of its own. That is the same rule
/// <see cref="Calls.RecordingService"/> follows — <see cref="Calls.ICallCommands"/> exists to carry
/// scoping plumbing for SIPSorcery's long-lived callbacks, and a request handler needs none of it.
/// </para>
/// <para>
/// Each step saves. That is deliberate rather than wasteful: the send to the provider happens between
/// <see cref="BeginRelayAsync"/> and <see cref="RelayAcceptedAsync"/>, so a process killed mid-send
/// leaves a row that honestly says <c>Relaying</c>, and the received message is durable — and therefore
/// deduplicable — before anything is sent on.
/// </para>
/// </remarks>
public class MessageLifecycleService(IMessageRepository repository)
{
    /// <summary>Whether this provider message has already been taken in. See the port for why.</summary>
    public Task<bool> AlreadyReceivedAsync(string providerMessageId, CancellationToken cancellationToken = default) =>
        repository.ExistsAsync(providerMessageId, cancellationToken);

    public async Task<Guid> ReceiveAsync(
        MessageSource source,
        PhoneNumber from,
        PhoneNumber to,
        string body,
        int mediaCount,
        string providerMessageId,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var message = Message.Receive(source, from, to, body, mediaCount, providerMessageId, when);
        await repository.AddAsync(message, cancellationToken);
        await repository.SaveChangesAsync(cancellationToken);
        return message.Id;
    }

    /// <summary>Records that a message is about to be sent on, before it is sent.</summary>
    public async Task BeginRelayAsync(
        Guid messageId,
        PhoneNumber recipient,
        string body,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var message = await GetRequiredAsync(messageId, cancellationToken);
        message.BeginRelay(recipient, body, when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task RelayAcceptedAsync(
        Guid messageId,
        string providerMessageId,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var message = await GetRequiredAsync(messageId, cancellationToken);
        message.RelayAccepted(providerMessageId, when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task RelayFailedAsync(
        Guid messageId,
        string reason,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var message = await GetRequiredAsync(messageId, cancellationToken);
        message.RelayFailed(reason, when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Closes a message that was kept without any relay being attempted. See the aggregate.</summary>
    public async Task RecordOnlyAsync(
        Guid messageId,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var message = await GetRequiredAsync(messageId, cancellationToken);
        message.RecordOnly(when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task RejectAsync(
        Guid messageId,
        string reason,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var message = await GetRequiredAsync(messageId, cancellationToken);
        message.Reject(reason, when);
        await repository.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Applies a carrier delivery receipt, keyed by the provider's id for the message CallTree sent.
    /// Returns false when no relay has that id, which is ordinary — receipts also arrive for the
    /// failure notices texted back to the operator, which are not relays of anything.
    /// </summary>
    public async Task<bool> RecordDeliveryAsync(
        string providerMessageId,
        RelayDelivery delivery,
        string? error,
        DateTimeOffset when,
        CancellationToken cancellationToken = default)
    {
        var message = await repository.GetByRelayProviderMessageIdAsync(providerMessageId, cancellationToken);
        if (message is null)
        {
            return false;
        }

        message.RecordDelivery(delivery, error, when);
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<Message> GetRequiredAsync(Guid messageId, CancellationToken cancellationToken) =>
        await repository.GetByIdAsync(messageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {messageId} not found.");
}
