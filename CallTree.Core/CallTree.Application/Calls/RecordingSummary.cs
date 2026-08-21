using CallTree.Domain.Calls;

namespace CallTree.Application.Calls;

/// <summary>
/// One row of the recordings list, and also the detail view — there is nothing a detail page needs that
/// the list doesn't already carry. Deliberately separate from <see cref="CallSummary"/>: this is keyed by
/// recording id, not call id, and carries just enough call context (who, when) that a recording is
/// identifiable without a second request back into the call log.
/// </summary>
public sealed record RecordingSummary
{
    public required Guid Id { get; init; }

    public required Guid CallId { get; init; }

    /// <summary>Business direction of the call this recording belongs to.</summary>
    public required CallSource CallSource { get; init; }

    /// <summary>E.164 remote number of the inbound leg, or null when the caller ID would not parse.</summary>
    public string? RemoteNumber { get; init; }

    /// <summary>Caller ID exactly as received. Shown when <see cref="RemoteNumber"/> is null.</summary>
    public required string RawCallerId { get; init; }

    public required DateTimeOffset CallStartedAt { get; init; }

    public required ChannelLayout ChannelLayout { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null means the writer never finished (crash mid-call) — candidate for a repair sweep.</summary>
    public DateTimeOffset? FinalizedAt { get; init; }

    public double? DurationSeconds { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>Requires a call that actually has a recording — filter for that before calling this.</summary>
    public static RecordingSummary FromCall(Call call)
    {
        var recording = call.Recording
            ?? throw new InvalidOperationException($"Call {call.Id} has no recording.");

        // Legs are ordered by nothing in particular, and an aggregate always has an inbound leg,
        // but read models should not throw on odd historical data - hence FirstOrDefault.
        var inboundLeg = call.Legs.FirstOrDefault(l => l.Direction == LegDirection.Inbound);

        return new RecordingSummary
        {
            Id = recording.Id,
            CallId = call.Id,
            CallSource = call.Source,
            RemoteNumber = inboundLeg?.RemoteNumber?.Value,
            RawCallerId = inboundLeg?.RawCallerId ?? "",
            CallStartedAt = call.StartedAt,
            ChannelLayout = recording.ChannelLayout,
            CreatedAt = recording.CreatedAt,
            FinalizedAt = recording.FinalizedAt,
            DurationSeconds = recording.DurationSeconds,
            SizeBytes = recording.SizeBytes,
        };
    }
}
