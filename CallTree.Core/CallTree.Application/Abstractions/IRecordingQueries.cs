using CallTree.Application.Calls;
using CallTree.Application.Common;

namespace CallTree.Application.Abstractions;

/// <summary>
/// Read side of recordings, kept separate from <see cref="ICallRepository"/> for the same reason as
/// <see cref="ICallQueries"/>: this returns flat read models for display and never tracks anything.
/// </summary>
public interface IRecordingQueries
{
    /// <summary>Most recently created recording first.</summary>
    Task<PagedResult<RecordingSummary>> ListAsync(RecordingListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Null when no recording has this id.</summary>
    Task<RecordingSummary?> GetAsync(Guid recordingId, CancellationToken cancellationToken = default);

    /// <summary>Null when no recording has this id. See <see cref="RecordingFileLocation"/>.</summary>
    Task<RecordingFileLocation?> GetFileLocationAsync(Guid recordingId, CancellationToken cancellationToken = default);
}
