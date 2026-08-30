using System.Globalization;
using CallTree.Domain.ValueObjects;

namespace CallTree.Domain.Calls;

/// <summary>
/// The name a recording is born with, before anyone renames it.
/// </summary>
/// <remarks>
/// A recording always has a name, so that the operator has something to recognise it by in a list and
/// something to edit rather than to create. The scheme is caller + recording date because those are the
/// only two facts known at the moment the file opens - what the call was actually about is not.
///
/// The Outbound path deliberately names no caller: on that path the caller *is* the operator's own
/// mobile, so repeating it in every name would say nothing. The party who matters there joins later via
/// the handset's own merge and is invisible to CallTree - see the consent note in CLAUDE.md.
///
/// The format is deterministic on purpose: the AddRecordingName migration backfills existing rows with
/// SQL that reproduces it. That SQL is a one-time snapshot and is not kept in sync - a later change here
/// renames nothing that already exists, which is correct, since by then these are values a user may have
/// edited.
/// </remarks>
public static class RecordingName
{
    /// <summary>Also the column length. Generous for a hand-written label, short enough to render in a table cell.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Caller IDs are stored verbatim up to 256 characters (scanners send junk), which would swamp the
    /// name. Truncating the caller rather than the finished name keeps the date on the end where it is
    /// useful.
    /// </summary>
    public const int MaxCallerLength = 64;

    /// <summary>Invariant and UTC: this is written to the database, not rendered for a viewer.</summary>
    private const string DateFormat = "MMM dd yyyy HH:mm";

    public static string Default(
        CallSource source,
        PhoneNumber? remoteNumber,
        string? rawCallerId,
        DateTimeOffset createdAt)
    {
        var when = createdAt.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

        if (source == CallSource.Outbound)
        {
            return $"Outbound call, {when}";
        }

        var caller = Caller(remoteNumber, rawCallerId);
        var name = caller is null
            ? $"Inbound call, {when}"
            : $"Inbound call from {caller}, {when}";

        // Belt and braces: the caller is already clamped, so this cannot trigger on current data.
        return name.Length <= MaxLength ? name : name[..MaxLength];
    }

    /// <summary>Null when the caller identified itself with nothing usable at all.</summary>
    private static string? Caller(PhoneNumber? remoteNumber, string? rawCallerId)
    {
        if (remoteNumber is not null)
        {
            return remoteNumber.ToDisplayString();
        }

        var raw = rawCallerId?.Trim() ?? "";
        return raw.Length switch
        {
            0 => null,
            <= MaxCallerLength => raw,
            _ => raw[..MaxCallerLength],
        };
    }
}
