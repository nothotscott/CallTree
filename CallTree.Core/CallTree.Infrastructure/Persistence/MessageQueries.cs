using CallTree.Application.Abstractions;
using CallTree.Application.Common;
using CallTree.Application.Messages;
using Microsoft.EntityFrameworkCore;

namespace CallTree.Infrastructure.Persistence;

public class MessageQueries(CallTreeDbContext dbContext) : IMessageQueries
{
    private const string LikeEscape = "\\";

    private static string Escape(string search) => search
        .Replace(LikeEscape, LikeEscape + LikeEscape)
        .Replace("%", LikeEscape + "%")
        .Replace("_", LikeEscape + "_");

    public async Task<PagedResult<MessageSummary>> ListAsync(
        MessageListQuery query,
        CancellationToken cancellationToken = default)
    {
        var filtered = dbContext.Messages.AsNoTracking();

        if (query.Source is { } source)
        {
            filtered = filtered.Where(m => m.Source == source);
        }

        if (query.Status is { } status)
        {
            filtered = filtered.Where(m => m.Status == status);
        }

        if (query.Search is { } search)
        {
            // LIKE rather than Contains, for the same reason as RecordingQueries: EF translates
            // Contains to SQLite's case-sensitive instr(), and someone searching "landlord" expects to
            // find "Landlord". Wildcards are escaped so a body containing % or _ searches for those.
            var pattern = $"%{Escape(search)}%";
            filtered = filtered.Where(m => EF.Functions.Like(m.Body, pattern, LikeEscape));
        }

        var totalCount = await filtered.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult<MessageSummary>.Empty(query.Page, query.PageSize);
        }

        // Include-then-map rather than a server-side projection, same as CallQueries: PhoneNumber is
        // stored through a value converter and does not translate inside a projection. A page is at
        // most MaxPageSize messages of one relay each - this is a log, not a hot path.
        var messages = await filtered
            .OrderByDescending(m => m.ReceivedAt)
            .ThenByDescending(m => m.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Include(m => m.Relay)
            .ToListAsync(cancellationToken);

        var items = messages.Select(MessageSummary.FromMessage).ToList();

        return new PagedResult<MessageSummary>(items, query.Page, query.PageSize, totalCount);
    }
}
