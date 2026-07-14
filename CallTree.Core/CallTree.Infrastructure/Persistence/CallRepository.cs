using CallTree.Application.Abstractions;
using CallTree.Domain.Calls;
using Microsoft.EntityFrameworkCore;

namespace CallTree.Infrastructure.Persistence;

public class CallRepository(CallTreeDbContext dbContext) : ICallRepository
{
    public async Task AddAsync(Call call, CancellationToken cancellationToken = default) =>
        await dbContext.Calls.AddAsync(call, cancellationToken);

    public Task<Call?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Calls
            .Include(c => c.Legs)
            .Include(c => c.Recording)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
