using CallTree.Application.Calls;
using CallTree.Application.Common;

namespace CallTree.Application.Abstractions;

/// <summary>
/// Read side of the call log, kept separate from <see cref="ICallRepository"/>. The repository loads
/// whole aggregates so they can be mutated and saved; this returns flat read models for display and
/// never tracks anything.
/// </summary>
public interface ICallQueries
{
    /// <summary>Most recent call first.</summary>
    Task<PagedResult<CallSummary>> ListAsync(CallListQuery query, CancellationToken cancellationToken = default);
}
