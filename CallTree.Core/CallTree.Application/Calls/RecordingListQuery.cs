namespace CallTree.Application.Calls;

/// <summary>
/// Paging for the recordings list. Construct with <see cref="Create"/> — it clamps rather than rejects,
/// same reasoning as <see cref="CallListQuery"/>.
/// </summary>
public sealed record RecordingListQuery
{
    public const int DefaultPageSize = 25;

    /// <summary>An unbounded page size would let one request read the whole table into memory.</summary>
    public const int MaxPageSize = 200;

    /// <summary>1-based.</summary>
    public int Page { get; private init; } = 1;

    public int PageSize { get; private init; } = DefaultPageSize;

    private RecordingListQuery()
    {
    }

    public static RecordingListQuery Create(int? page, int? pageSize) => new()
    {
        Page = Math.Max(1, page ?? 1),
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
    };

    public int Skip => (Page - 1) * PageSize;
}
