using CallTree.Domain.Calls;

namespace CallTree.Application.Calls;

/// <summary>
/// Filter and paging for the call log. Construct with <see cref="Create"/> — it clamps rather than
/// rejects, because a list view asking for page 0 or an absurd page size wants a sensible page back,
/// not an error dialog. Genuinely malformed input (an unknown enum name) still fails at model binding.
/// </summary>
public sealed record CallListQuery
{
    public const int DefaultPageSize = 25;

    /// <summary>An unbounded page size would let one request read the whole table into memory.</summary>
    public const int MaxPageSize = 200;

    /// <summary>1-based.</summary>
    public int Page { get; private init; } = 1;

    public int PageSize { get; private init; } = DefaultPageSize;

    /// <summary>Null means both business directions.</summary>
    public CallSource? Source { get; private init; }

    /// <summary>Null means every status, including calls still in flight.</summary>
    public CallStatus? Status { get; private init; }

    private CallListQuery()
    {
    }

    public static CallListQuery Create(int? page, int? pageSize, CallSource? source = null, CallStatus? status = null) => new()
    {
        Page = Math.Max(1, page ?? 1),
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
        Source = source,
        Status = status,
    };

    public int Skip => (Page - 1) * PageSize;
}
