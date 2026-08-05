using CallTree.Application.Abstractions;
using CallTree.Application.Calls;
using CallTree.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CallTree.Infrastructure.Persistence;

public class CallQueries(CallTreeDbContext dbContext) : ICallQueries
{
    public async Task<PagedResult<CallSummary>> ListAsync(
        CallListQuery query,
        CancellationToken cancellationToken = default)
    {
        var filtered = dbContext.Calls.AsNoTracking();

        if (query.Source is { } source)
        {
            filtered = filtered.Where(c => c.Source == source);
        }

        if (query.Status is { } status)
        {
            filtered = filtered.Where(c => c.Status == status);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult<CallSummary>.Empty(query.Page, query.PageSize);
        }

        // Include-then-map rather than a server-side projection: the number needed for display lives on
        // the inbound leg, and PhoneNumber is stored through a value converter, which does not translate
        // inside a correlated subquery. A page is at most MaxPageSize calls of two legs each, so loading
        // them is cheap - this is a call log, not a hot path.
        var calls = await filtered
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Include(c => c.Legs)
            .Include(c => c.Recording)
            .ToListAsync(cancellationToken);

        var items = calls.Select(CallSummary.FromCall).ToList();

        return new PagedResult<CallSummary>(items, query.Page, query.PageSize, totalCount);
    }
}
