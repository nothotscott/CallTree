using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CallTree.Infrastructure.Persistence;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as UTC text that SQLite can sort and compare.
///
/// SQLite has no date type, and EF's built-in DateTimeOffset mapping refuses to translate ORDER BY
/// ("SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses") because rows
/// written with different UTC offsets would not sort by instant. Range filters have the same problem,
/// quietly rather than loudly.
///
/// Normalizing to UTC on write removes the ambiguity, so ordinary text ordering is ordering by instant.
/// The format is byte-for-byte the one EF already uses for this type, so existing rows are read and
/// written unchanged and no data migration is needed.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, string>
{
    // Capital F trims trailing zeros, matching EF's SqliteDateTimeOffsetTypeMapping. Values are
    // therefore variable length - which still sorts correctly, because '+' (0x2B) precedes every
    // digit, so a truncated fraction compares as the zeros it stands for.
    private const string Format = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";

    public UtcDateTimeOffsetConverter()
        : base(
            value => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
            text => DateTimeOffset.ParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal))
    {
    }
}
