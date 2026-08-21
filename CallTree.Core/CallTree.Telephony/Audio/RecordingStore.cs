using System.Globalization;
using CallTree.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CallTree.Telephony.Audio;

/// <summary>Where one recording is going to live.</summary>
/// <param name="RelativePath">
/// What gets stored on the <c>Recording</c> entity. Always forward-slashed, so a database written on
/// Windows still resolves in the Linux container.
/// </param>
/// <param name="FullPath">Absolute path to write to.</param>
public readonly record struct RecordingLocation(string RelativePath, string FullPath);

/// <summary>
/// Decides where recordings go and keeps the recordings root in one place.
/// </summary>
/// <remarks>
/// Files are grouped by month. A single flat directory is fine for a year and miserable after five, and
/// the grouping is cheap to do now and awkward to retrofit once paths are in the database. The filename
/// leads with a sortable UTC timestamp so the directory listing is chronological without a tool, and
/// carries the call id so a row and a file can always be matched up by hand.
/// </remarks>
public sealed class RecordingStore(IOptions<StorageOptions> options, IHostEnvironment environment)
{
    /// <summary>
    /// Absolute path of the recordings root. Relative configuration resolves against the content root,
    /// not the working directory — otherwise it lands somewhere different under `dotnet run`, a published
    /// build, and the container.
    /// </summary>
    public string Root { get; } = Path.IsPathRooted(options.Value.RecordingsRoot)
        ? options.Value.RecordingsRoot
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.RecordingsRoot));

    public RecordingLocation Locate(Guid callId, DateTimeOffset startedAt)
    {
        var utc = startedAt.UtcDateTime;
        var month = utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{utc:yyyyMMdd-HHmmss}-{callId:N}.wav");

        return new RecordingLocation(
            RelativePath: $"{month}/{name}",
            FullPath: Path.Combine(Root, month, name));
    }

    /// <summary>
    /// Resolves a stored relative path back to a file, refusing anything that escapes the root. Nothing
    /// reads recordings yet — Phase 7's streaming endpoint will — but the check belongs with the path
    /// construction rather than with whoever gets there first.
    /// </summary>
    public bool TryResolve(string relativePath, out string fullPath)
    {
        fullPath = Path.GetFullPath(Path.Combine(Root, relativePath));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.Ordinal);
    }
}
