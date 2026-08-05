namespace CallTree.Application.Common;

/// <summary>
/// One page of results, plus enough context for a client to render pager controls without
/// having to recompute the arithmetic (and get it subtly wrong for the empty case).
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    /// <summary>Zero when there is nothing to page through, so <see cref="Page"/> 1 of 0 is a valid empty state.</summary>
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}
