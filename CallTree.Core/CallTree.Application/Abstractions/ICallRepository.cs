using CallTree.Domain.Calls;

namespace CallTree.Application.Abstractions;

public interface ICallRepository
{
    Task AddAsync(Call call, CancellationToken cancellationToken = default);

    /// <summary>Loads the full aggregate (legs + recording).</summary>
    Task<Call?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
