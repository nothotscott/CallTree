using CallTree.Domain.Calls;

namespace CallTree.Application.Abstractions;

public interface ICallRepository
{
    Task AddAsync(Call call, CancellationToken cancellationToken = default);

    /// <summary>Loads the full aggregate (legs + recording).</summary>
    Task<Call?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the full aggregate owning a recording. Renaming is the one write that arrives keyed by
    /// recording id rather than call id - the operator is looking at a recording, not a call - and the
    /// <see cref="Recording"/> is only reachable through its <see cref="Call"/>.
    /// </summary>
    Task<Call?> GetByRecordingIdAsync(Guid recordingId, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
