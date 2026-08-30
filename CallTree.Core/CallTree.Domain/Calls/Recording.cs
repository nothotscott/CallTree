namespace CallTree.Domain.Calls;

public class Recording
{
    public Guid Id { get; private set; }

    /// <summary>
    /// What the operator calls this recording. Never blank: a default naming the caller and the date is
    /// assigned when the file opens (see <see cref="RecordingName"/>), so there is always something to
    /// edit rather than something to create.
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>Path relative to the configured recordings root (the root itself is config, not data).</summary>
    public string FilePath { get; private set; } = "";

    public ChannelLayout ChannelLayout { get; private set; }
    public double? DurationSeconds { get; private set; }
    public long? SizeBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Null means the writer never finished (crash mid-call) — candidate for the startup repair sweep.</summary>
    public DateTimeOffset? FinalizedAt { get; private set; }

    private Recording()
    {
    }

    internal Recording(string filePath, ChannelLayout channelLayout, DateTimeOffset createdAt, string name)
    {
        Id = Guid.NewGuid();
        FilePath = filePath;
        ChannelLayout = channelLayout;
        CreatedAt = createdAt;
        Name = name;
    }

    /// <summary>
    /// Renames the recording. Blank is rejected rather than treated as "restore the default": a nameless
    /// row would leave nothing to click in the list, and the caller and date it would have been rebuilt
    /// from are both already columns of their own.
    /// </summary>
    /// <exception cref="ArgumentException">The name is blank, or longer than <see cref="RecordingName.MaxLength"/>.</exception>
    public void Rename(string name)
    {
        var trimmed = (name ?? "").Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A recording name cannot be blank.", nameof(name));
        }

        if (trimmed.Length > RecordingName.MaxLength)
        {
            throw new ArgumentException(
                $"A recording name cannot exceed {RecordingName.MaxLength} characters.", nameof(name));
        }

        Name = trimmed;
    }

    public void MarkFinalized(double durationSeconds, long sizeBytes, DateTimeOffset when)
    {
        if (FinalizedAt is not null)
        {
            throw new InvalidOperationException($"Recording {Id} is already finalized.");
        }

        DurationSeconds = durationSeconds;
        SizeBytes = sizeBytes;
        FinalizedAt = when;
    }
}
