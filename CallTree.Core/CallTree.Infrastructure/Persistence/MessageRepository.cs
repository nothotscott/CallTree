using CallTree.Application.Abstractions;
using CallTree.Domain.Messages;
using Microsoft.EntityFrameworkCore;

namespace CallTree.Infrastructure.Persistence;

public class MessageRepository(CallTreeDbContext dbContext) : IMessageRepository
{
    public async Task AddAsync(Message message, CancellationToken cancellationToken = default) =>
        await dbContext.Messages.AddAsync(message, cancellationToken);

    public Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Messages
            .Include(m => m.Relay)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(string providerMessageId, CancellationToken cancellationToken = default) =>
        dbContext.Messages
            .AsNoTracking()
            .AnyAsync(m => m.ProviderMessageId == providerMessageId, cancellationToken);

    public Task<Message?> GetByRelayProviderMessageIdAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default) =>
        dbContext.Messages
            .Include(m => m.Relay)
            .FirstOrDefaultAsync(
                m => m.Relay != null && m.Relay.ProviderMessageId == providerMessageId,
                cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
