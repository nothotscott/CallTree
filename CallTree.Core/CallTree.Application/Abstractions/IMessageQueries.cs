using CallTree.Application.Common;
using CallTree.Application.Messages;

namespace CallTree.Application.Abstractions;

/// <summary>
/// Read side of the message log, kept apart from <see cref="IMessageRepository"/> for the same reason
/// <see cref="ICallQueries"/> is kept apart from <see cref="ICallRepository"/>: this returns flat,
/// untracked read models for display and never loads an aggregate to mutate.
/// </summary>
public interface IMessageQueries
{
    /// <summary>Most recently received first.</summary>
    Task<PagedResult<MessageSummary>> ListAsync(MessageListQuery query, CancellationToken cancellationToken = default);
}
