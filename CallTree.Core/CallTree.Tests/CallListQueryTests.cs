using CallTree.Application.Calls;
using CallTree.Application.Common;
using Xunit;

namespace CallTree.Tests;

public class CallListQueryTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    public void Create_clamps_page_to_at_least_one(int? requested, int expected)
    {
        Assert.Equal(expected, CallListQuery.Create(requested, null).Page);
    }

    [Theory]
    [InlineData(null, CallListQuery.DefaultPageSize)]
    [InlineData(0, 1)]
    [InlineData(10, 10)]
    [InlineData(CallListQuery.MaxPageSize + 1, CallListQuery.MaxPageSize)]
    [InlineData(100_000, CallListQuery.MaxPageSize)]
    public void Create_clamps_page_size_to_the_allowed_range(int? requested, int expected)
    {
        Assert.Equal(expected, CallListQuery.Create(null, requested).PageSize);
    }

    [Theory]
    [InlineData(1, 25, 0)]
    [InlineData(2, 25, 25)]
    [InlineData(4, 10, 30)]
    public void Skip_is_derived_from_the_clamped_values(int page, int pageSize, int expectedSkip)
    {
        Assert.Equal(expectedSkip, CallListQuery.Create(page, pageSize).Skip);
    }
}

public class PagedResultTests
{
    [Theory]
    [InlineData(0, 25, 0)]
    [InlineData(1, 25, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(5376, 3, 1792)]
    public void TotalPages_rounds_up_and_is_zero_when_empty(int totalCount, int pageSize, int expected)
    {
        var result = new PagedResult<string>([], 1, pageSize, totalCount);
        Assert.Equal(expected, result.TotalPages);
    }

    [Fact]
    public void An_empty_result_reports_no_pages_in_either_direction()
    {
        var result = PagedResult<string>.Empty(1, 25);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalPages);
        Assert.False(result.HasPreviousPage);
        Assert.False(result.HasNextPage);
    }

    [Fact]
    public void The_last_page_reports_no_next_page()
    {
        var result = new PagedResult<string>(["a"], 2, 25, 26);

        Assert.True(result.HasPreviousPage);
        Assert.False(result.HasNextPage);
    }

    [Fact]
    public void A_page_beyond_the_end_is_representable_and_reports_no_next_page()
    {
        // The API clamps page size but not page number, so asking for page 500 of 2 is a valid
        // empty answer rather than an error.
        var result = new PagedResult<string>([], 500, 3, 6);

        Assert.False(result.HasNextPage);
        Assert.True(result.HasPreviousPage);
    }
}

public class RecordingListQueryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  landlord  ", "landlord")]
    public void Create_trims_the_search_and_collapses_blank_to_no_filter(string? requested, string? expected)
    {
        Assert.Equal(expected, RecordingListQuery.Create(null, null, requested).Search);
    }

    [Fact]
    public void Create_clamps_a_search_longer_than_any_name_could_be()
    {
        var search = RecordingListQuery.Create(null, null, new string('x', 5_000)).Search;

        Assert.Equal(RecordingListQuery.MaxSearchLength, search!.Length);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    public void Create_clamps_page_to_at_least_one(int? requested, int expected)
    {
        Assert.Equal(expected, RecordingListQuery.Create(requested, null).Page);
    }

    [Theory]
    [InlineData(null, RecordingListQuery.DefaultPageSize)]
    [InlineData(0, 1)]
    [InlineData(RecordingListQuery.MaxPageSize + 1, RecordingListQuery.MaxPageSize)]
    public void Create_clamps_page_size_to_the_allowed_range(int? requested, int expected)
    {
        Assert.Equal(expected, RecordingListQuery.Create(null, requested).PageSize);
    }
}
