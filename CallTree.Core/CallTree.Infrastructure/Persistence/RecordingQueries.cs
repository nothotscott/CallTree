using CallTree.Application.Abstractions;
using CallTree.Application.Calls;
using CallTree.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CallTree.Infrastructure.Persistence;

public class RecordingQueries(CallTreeDbContext dbContext) : IRecordingQueries
{
    private const string LikeEscape = "\\";

    private static string Escape(string search) => search
        .Replace(LikeEscape, LikeEscape + LikeEscape)
        .Replace("%", LikeEscape + "%")
        .Replace("_", LikeEscape + "_");

    public async Task<PagedResult<RecordingSummary>> ListAsync(
        RecordingListQuery query,
        CancellationToken cancellationToken = default)
    {
        var filtered = dbContext.Calls.AsNoTracking().Where(c => c.Recording != null);

        if (query.Search is { } search)
        {
            // LIKE rather than Contains: EF translates Contains to SQLite's instr(), which is
            // case-sensitive, and an operator typing "landlord" expects to find "Landlord". SQLite's
            // LIKE is case-insensitive for ASCII. The wildcards are escaped so a name containing % or _
            // searches for those characters rather than matching everything.
            var pattern = $"%{Escape(search)}%";
            filtered = filtered.Where(c => EF.Functions.Like(c.Recording!.Name, pattern, LikeEscape));
        }

        var totalCount = await filtered.CountAsync(cancellationToken);
        if (totalCount == 0)
        {
            return PagedResult<RecordingSummary>.Empty(query.Page, query.PageSize);
        }

        // Include-then-map for the same reason as CallQueries: the caller-facing number lives on the
        // inbound leg, and PhoneNumber's value converter doesn't translate inside a correlated subquery.
        var calls = await filtered
            .OrderByDescending(c => c.Recording!.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Include(c => c.Legs)
            .Include(c => c.Recording)
            .ToListAsync(cancellationToken);

        var items = calls.Select(RecordingSummary.FromCall).ToList();

        return new PagedResult<RecordingSummary>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<RecordingSummary?> GetAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        var call = await dbContext.Calls
            .AsNoTracking()
            .Include(c => c.Legs)
            .Include(c => c.Recording)
            .FirstOrDefaultAsync(c => c.Recording != null && c.Recording.Id == recordingId, cancellationToken);

        return call is null ? null : RecordingSummary.FromCall(call);
    }

    public async Task<RecordingFileLocation?> GetFileLocationAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default)
    {
        // A narrow projection rather than reusing GetAsync: the streaming endpoint needs the file path,
        // which never belongs in RecordingSummary, and there is no reason to pull the legs along for it.
        var projected = await dbContext.Calls
            .AsNoTracking()
            .Where(c => c.Recording != null && c.Recording.Id == recordingId)
            .Select(c => new { c.Recording!.FilePath, IsFinalized = c.Recording.FinalizedAt != null })
            .FirstOrDefaultAsync(cancellationToken);

        return projected is null ? null : new RecordingFileLocation(projected.FilePath, projected.IsFinalized);
    }
}
