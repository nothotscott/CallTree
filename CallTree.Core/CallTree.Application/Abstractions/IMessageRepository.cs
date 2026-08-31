using CallTree.Domain.Messages;

namespace CallTree.Application.Abstractions;

/// <summary>Write side of the message log. Loads whole aggregates so they can be mutated and saved.</summary>
public interface IMessageRepository
{
    Task AddAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>Loads the full aggregate (message + relay).</summary>
    Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a message with this provider id has already been taken in. The provider retries a
    /// webhook on any non-2xx and on a slow response, so without this check one text could be forwarded
    /// several times — and unlike a duplicated call record, a duplicated forward costs money and buzzes
    /// the operator's phone again.
    /// </summary>
    Task<bool> ExistsAsync(string providerMessageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the message whose relay the provider gave this id, for applying a delivery receipt.
    /// Null is ordinary, not an error: receipts also arrive for messages CallTree sent outside a relay,
    /// such as a failure notice to the operator.
    /// </summary>
    Task<Message?> GetByRelayProviderMessageIdAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
