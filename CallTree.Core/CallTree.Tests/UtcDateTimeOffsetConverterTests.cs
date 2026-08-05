using CallTree.Infrastructure.Persistence;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// The converter exists so SQLite can sort and range-filter timestamps. Two properties matter and
/// neither is exercised by any phone call: it must round-trip, and its text must sort by instant.
/// </summary>
public class UtcDateTimeOffsetConverterTests
{
    private static readonly UtcDateTimeOffsetConverter Converter = new();

    private static string ToStore(DateTimeOffset value) =>
        (string)Converter.ConvertToProvider(value)!;

    private static DateTimeOffset FromStore(string text) =>
        (DateTimeOffset)Converter.ConvertFromProvider(text)!;

    [Fact]
    public void Round_trips_a_utc_value()
    {
        var value = new DateTimeOffset(2026, 7, 31, 19, 41, 9, TimeSpan.Zero).AddTicks(4854618);

        Assert.Equal(value, FromStore(ToStore(value)));
    }

    [Fact]
    public void Normalizes_a_non_utc_offset_to_the_same_instant()
    {
        var eastern = new DateTimeOffset(2026, 7, 31, 15, 41, 9, TimeSpan.FromHours(-4));

        var stored = ToStore(eastern);

        Assert.EndsWith("+00:00", stored);
        Assert.Equal(eastern.ToUniversalTime(), FromStore(stored));
        Assert.Equal(eastern.UtcDateTime, FromStore(stored).UtcDateTime);
    }

    [Fact]
    public void Reads_rows_written_by_efs_own_mapping()
    {
        // Verbatim from the existing database - trailing zeros trimmed to six fractional digits.
        const string existingRow = "2026-07-31 12:58:35.212599+00:00";

        var parsed = FromStore(existingRow);

        Assert.Equal(new DateTimeOffset(2026, 7, 31, 12, 58, 35, TimeSpan.Zero).AddTicks(2125990), parsed);
        Assert.Equal(existingRow, ToStore(parsed));
    }

    [Fact]
    public void Handles_a_whole_second_where_the_decimal_point_is_omitted()
    {
        var value = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        var stored = ToStore(value);

        Assert.DoesNotContain(".", stored);
        Assert.Equal(value, FromStore(stored));
    }

    [Theory]
    // A trimmed fraction must compare as the zeros it stands for: '+' sorts before any digit.
    [InlineData("2026-07-31 12:58:35.212599+00:00", "2026-07-31 12:58:35.2125991+00:00")]
    [InlineData("2026-07-31 12:58:35+00:00", "2026-07-31 12:58:35.0000001+00:00")]
    [InlineData("2026-07-30 19:19:32.0031406+00:00", "2026-07-31 13:06:15.4660686+00:00")]
    [InlineData("2026-07-31 09:00:00+00:00", "2026-07-31 10:00:00+00:00")]
    public void Text_ordering_matches_chronological_ordering(string earlier, string later)
    {
        Assert.True(FromStore(earlier) < FromStore(later), "test data is not in chronological order");
        Assert.True(string.CompareOrdinal(earlier, later) < 0, "text sorts differently from the instants");
    }

    [Fact]
    public void An_eastern_timestamp_sorts_correctly_against_a_utc_one_once_normalized()
    {
        // The whole point of normalizing: written as-is these two would sort by wall clock, not instant.
        var eastern = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.FromHours(-4)); // 13:00 UTC
        var utc = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        Assert.True(utc < eastern);
        Assert.True(string.CompareOrdinal(ToStore(utc), ToStore(eastern)) < 0);
    }
}
