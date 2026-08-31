using CallTree.Domain.Messages;

namespace CallTree.Application.Messages;

/// <summary>
/// Filter and paging for the message log. Construct with <see cref="Create"/> — it clamps rather than
/// rejects, same reasoning as <see cref="Calls.CallListQuery"/>.
/// </summary>
public sealed record MessageListQuery
{
    public const int DefaultPageSize = 25;

    /// <summary>An unbounded page size would let one request read the whole table into memory.</summary>
    public const int MaxPageSize = 200;

    /// <summary>A search longer than a body could ever be matches nothing.</summary>
    public const int MaxSearchLength = SmsText.MaxLength;

    /// <summary>1-based.</summary>
    public int Page { get; private init; } = 1;

    public int PageSize { get; private init; } = DefaultPageSize;

    /// <summary>Null means both business directions.</summary>
    public MessageSource? Source { get; private init; }

    /// <summary>Null means every status, including messages still being relayed.</summary>
    public MessageStatus? Status { get; private init; }

    /// <summary>
    /// Substring of the body to match, or null for no filter. Trimmed, and blank collapses to null so
    /// an empty search box and no search box mean the same thing.
    /// </summary>
    public string? Search { get; private init; }

    private MessageListQuery()
    {
    }

    public static MessageListQuery Create(
        int? page,
        int? pageSize,
        MessageSource? source = null,
        MessageStatus? status = null,
        string? search = null) => new()
    {
        Page = Math.Max(1, page ?? 1),
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize),
        Source = source,
        Status = status,
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
