namespace CallTree.Domain.Messages;

/// <summary>
/// The size limits an SMS body has to live inside, and the one operation that enforces them.
/// </summary>
/// <remarks>
/// 1600 characters is the provider's hard cap on a single API send (it segments concatenated messages
/// itself below that and refuses outright above it). It matters here rather than only at the HTTP edge
/// because forwarding *adds* a prefix to a body that may already be at the limit: without truncation a
/// maximum-length text to the DID would be rejected by the provider and silently never reach the
/// operator's phone, which is the one failure this whole feature exists to avoid.
/// </remarks>
public static class SmsText
{
    /// <summary>Longest body the provider will accept in one send.</summary>
    public const int MaxLength = 1600;

    /// <summary>Appended in place of what was cut, so a truncated message says that it was truncated.</summary>
    public const string Ellipsis = "… [truncated]";

    /// <summary>
    /// Cuts <paramref name="text"/> down to <paramref name="limit"/> characters, marking it when
    /// something was removed. A limit too small to hold the marker just hard-cuts — the caller has
    /// already lost the message at that point and a marker alone would be worse than a fragment.
    /// </summary>
    public static string Truncate(string text, int limit = MaxLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0)
        {
            return "";
        }

        if (text.Length <= limit)
        {
            return text;
        }

        return limit <= Ellipsis.Length
            ? text[..limit]
            : text[..(limit - Ellipsis.Length)] + Ellipsis;
    }
}
