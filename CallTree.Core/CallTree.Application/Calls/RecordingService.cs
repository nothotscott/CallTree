using CallTree.Application.Abstractions;

namespace CallTree.Application.Calls;

/// <summary>
/// The write side of recordings — currently just renaming.
/// </summary>
/// <remarks>
/// Deliberately not part of <see cref="CallLifecycleService"/>: that one exists to turn telephony events
/// into state transitions and is driven from SIPSorcery's threads through <see cref="ICallCommands"/>.
/// A rename is an operator action arriving on an HTTP request, which already has a DI scope of its own,
/// so it needs none of that plumbing and has no business inside the call state machine.
/// </remarks>
public class RecordingService(ICallRepository repository)
{
    /// <summary>
    /// Renames a recording and returns the updated read model, or null when no recording has this id.
    /// </summary>
    /// <remarks>
    /// The summary is built from the aggregate that was just loaded rather than read back through
    /// <see cref="IRecordingQueries"/> — same shape either way, and a second round trip to describe a row
    /// we are already holding buys nothing.
    /// </remarks>
    /// <exception cref="ArgumentException">The name is blank or too long — see <c>Recording.Rename</c>.</exception>
    public async Task<RecordingSummary?> RenameAsync(
        Guid recordingId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var call = await repository.GetByRecordingIdAsync(recordingId, cancellationToken);
        if (call?.Recording is null)
        {
            return null;
        }

        call.Recording.Rename(name);
        await repository.SaveChangesAsync(cancellationToken);

        return RecordingSummary.FromCall(call);
    }
}
