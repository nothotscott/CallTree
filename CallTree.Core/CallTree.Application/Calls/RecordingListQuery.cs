namespace CallTree.Application.Calls;

/// <summary>
/// Paging and filtering for the recordings list. Construct with <see cref="Create"/> — it clamps rather
/// than rejects, same reasoning as <see cref="CallListQuery"/>.
/// </summary>
public sealed record RecordingListQuery
{
    public const int DefaultPageSize = 25;

    /// <summary>An unbounded page size would let one request read the whole table into memory.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// A search longer than a name could ever be matches nothing, so there is no reason to send it to
    /// the database.
    /// </summary>
    public const int MaxSearchLength = Domain.Calls.RecordingName.MaxLength;

    /// <summary>1-based.</summary>
    public int Page { get; private init; } = 1;

    public int PageSize { get; private init; } = DefaultPageSize;

    /// <summary>
    /// Substring of the recording name to match, or null for no filter. Trimmed, and blank collapses to
    /// null so that an empty search box and no search box mean the same thing.
    /// </summary>
    public string? Search { get; private init; }

    private RecordingListQuery()
    {
    }

    public static RecordingListQuery Create(int? page, int? pageSize, string? search = null) => new()
    {
        Page = Math.Max(1, page ?? 1),
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
        Search = Normalize(search),
    };

    public int Skip => (Page - 1) * PageSize;

    private static string? Normalize(string? search)
    {
        var trimmed = search?.Trim() ?? "";
        return trimmed.Length switch
        {
            0 => null,
            <= MaxSearchLength => trimmed,
            _ => trimmed[..MaxSearchLength],
        };
    }
}
