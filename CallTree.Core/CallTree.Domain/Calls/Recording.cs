namespace CallTree.Domain.Calls;

public class Recording
{
    public Guid Id { get; private set; }

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

    internal Recording(string filePath, ChannelLayout channelLayout, DateTimeOffset createdAt)
    {
        Id = Guid.NewGuid();
        FilePath = filePath;
        ChannelLayout = channelLayout;
        CreatedAt = createdAt;
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
