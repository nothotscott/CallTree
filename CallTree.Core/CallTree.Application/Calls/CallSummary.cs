using CallTree.Domain.Calls;

namespace CallTree.Application.Calls;

/// <summary>
/// One row of the call log. A read model, deliberately separate from the <see cref="Call"/> aggregate:
/// the aggregate exists to enforce transitions, and exposing it directly would leak that surface into
/// the API contract and freeze it against UI changes.
/// </summary>
public sealed record CallSummary
{
    public required Guid Id { get; init; }

    /// <summary>Business direction. Both kinds arrive as inbound SIP INVITEs.</summary>
    public required CallSource Source { get; init; }

    /// <summary>Why <see cref="Source"/> was decided. Caller ID is spoofable, so this is worth showing.</summary>
    public required SourceClassification SourceClassification { get; init; }

    public required CallStatus Status { get; init; }

    /// <summary>E.164 remote number of the inbound leg, or null when the caller ID would not parse.</summary>
    public string? RemoteNumber { get; init; }

    /// <summary>Caller ID exactly as received. Shown when <see cref="RemoteNumber"/> is null.</summary>
    public required string RawCallerId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? AnsweredAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string? TerminationReason { get; init; }

    /// <summary>Total call time. Null while the call is still in flight.</summary>
    public double? DurationSeconds =>
        EndedAt is { } ended ? Math.Round((ended - StartedAt).TotalSeconds, 3) : null;

    /// <summary>Talk time — from answer rather than from the first ring. Null if never answered.</summary>
    public double? TalkTimeSeconds =>
        EndedAt is { } ended && AnsweredAt is { } answered
            ? Math.Round((ended - answered).TotalSeconds, 3)
            : null;

    public required bool HasRecording { get; init; }

    /// <summary>Length of the recording, when one exists and was finalized.</summary>
    public double? RecordingDurationSeconds { get; init; }

    public static CallSummary FromCall(Call call)
    {
        // Legs are ordered by nothing in particular, and an aggregate always has an inbound leg,
        // but read models should not throw on odd historical data - hence FirstOrDefault.
        var inboundLeg = call.Legs.FirstOrDefault(l => l.Direction == LegDirection.Inbound);

        return new CallSummary
        {
            Id = call.Id,
            Source = call.Source,
            SourceClassification = call.SourceClassification,
            Status = call.Status,
            RemoteNumber = inboundLeg?.RemoteNumber?.Value,
            RawCallerId = inboundLeg?.RawCallerId ?? "",
            StartedAt = call.StartedAt,
            AnsweredAt = call.AnsweredAt,
            EndedAt = call.EndedAt,
            TerminationReason = call.TerminationReason,
            HasRecording = call.Recording is not null,
            RecordingDurationSeconds = call.Recording?.DurationSeconds,
        };
    }
}
