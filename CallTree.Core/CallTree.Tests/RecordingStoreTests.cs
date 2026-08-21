using CallTree.Application.Configuration;
using CallTree.Telephony.Audio;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace CallTree.Tests;

public class RecordingStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 9, 14, 5, 30, TimeSpan.Zero);
    private static readonly Guid CallId = Guid.Parse("0f4d1b2a-3c5e-4a7b-9d8f-1122334455aa");

    private sealed class FakeEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CallTree.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static RecordingStore Store(string recordingsRoot, string contentRoot = "/srv/app") =>
        new(Options.Create(new StorageOptions { RecordingsRoot = recordingsRoot }), new FakeEnvironment(contentRoot));

    [Fact]
    public void A_relative_root_resolves_against_the_content_root()
    {
        // Not the working directory: that differs between `dotnet run`, a published build and the
        // container, and recordings landing somewhere different in each is a silent data-loss bug.
        var contentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "calltree-content"));

        var store = Store("data/recordings", contentRoot);

        Assert.Equal(Path.Combine(contentRoot, "data", "recordings"), store.Root);
    }

    [Fact]
    public void An_absolute_root_is_taken_as_given()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "recordings"));

        Assert.Equal(absolute, Store(absolute).Root);
    }

    [Fact]
    public void Files_are_grouped_by_month_and_named_for_the_call()
    {
        var location = Store(Path.GetTempPath()).Locate(CallId, T0);

        Assert.Equal("2026-08/20260809-140530-0f4d1b2a3c5e4a7b9d8f1122334455aa.wav", location.RelativePath);
    }

    [Fact]
    public void The_stored_path_is_forward_slashed_whatever_the_platform()
    {
        // The database can be written on Windows and read in the Linux container. A backslash in the
        // stored path would resolve to a filename containing a backslash there, not to a directory.
        var location = Store(Path.GetTempPath()).Locate(CallId, T0);

        Assert.DoesNotContain('\\', location.RelativePath);
        Assert.EndsWith(Path.Combine("2026-08", "20260809-140530-0f4d1b2a3c5e4a7b9d8f1122334455aa.wav"), location.FullPath);
    }

    [Fact]
    public void The_name_uses_utc_so_the_listing_stays_chronological()
    {
        // Same instant, two offsets. A local-time name would sort the same call into two different
        // months depending on where the host happens to be.
        var utc = Store(Path.GetTempPath()).Locate(CallId, T0);
        var offset = Store(Path.GetTempPath()).Locate(CallId, T0.ToOffset(TimeSpan.FromHours(-7)));

        Assert.Equal(utc.RelativePath, offset.RelativePath);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("2026-08/../../../secrets.txt")]
    public void Paths_that_escape_the_recordings_root_are_refused(string relativePath)
    {
        var store = Store(Path.Combine(Path.GetTempPath(), "calltree-recordings"));

        Assert.False(store.TryResolve(relativePath, out _));
    }

    [Fact]
    public void A_path_inside_the_root_resolves()
    {
        var store = Store(Path.Combine(Path.GetTempPath(), "calltree-recordings"));
        var location = store.Locate(CallId, T0);

        Assert.True(store.TryResolve(location.RelativePath, out var resolved));
        Assert.Equal(location.FullPath, resolved);
    }
}
