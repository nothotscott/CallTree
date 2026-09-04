namespace CallTree.SipHarness;

/// <summary>Which side of a call the harness was playing.</summary>
internal enum LegRole
{
    /// <summary>A caller the harness placed into CallTree.</summary>
    Caller,

    /// <summary>A leg CallTree placed at the harness - the simulated mobile, or a proxy-dialled party.</summary>
    FarEnd,
}

/// <summary>
/// What one leg did, from the harness's side: what it played, what it heard back, and how it ended.
/// </summary>
/// <remarks>
/// The pairing check is built entirely out of these. A caller and the far end it was joined to prove
/// each other: the far end must have heard the caller's tone and the caller must have heard the far
/// end's. Nothing here records which leg was *supposed* to pair with which, because that is the answer
/// the run is trying to find out - asserting it up front would make a crossed bridge look like a
/// bookkeeping error rather than the bug it is.
/// </remarks>
internal sealed record LegAudit
{
    public required LegRole Role { get; init; }

    /// <summary>A short name for output: "caller 2", "far end (+15559876543)".</summary>
    public required string Label { get; init; }

    /// <summary>The SIP Call-ID, so a failing leg can be found in CallTree's own log.</summary>
    public required string SipCallId { get; init; }

    /// <summary>The number this leg was dialled from, or dialled to.</summary>
    public required string Number { get; init; }

    /// <summary>The tone this leg played, or null when it never got far enough to play one.</summary>
    public int? PlayedHz { get; init; }

    public required bool Answered { get; init; }

    /// <summary>When the INVITE went out (a caller), or arrived (a far-end leg).</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>
    /// When the leg went live, and when it went away. These two are what turn a set of individually
    /// correct calls into evidence about concurrency: three calls that each behaved perfectly but never
    /// once overlapped are three sequential calls, and the timestamps are the only thing that says so.
    /// </summary>
    public DateTimeOffset? AnsweredAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>How long the far end took to pick up. On a queued call this is where the queue shows.</summary>
    public TimeSpan? TimeToAnswer => AnsweredAt is { } answered ? answered - RequestedAt : null;

    public long FramesSent { get; init; }

    public HeardAudio Heard { get; init; }

    /// <summary>How the leg finished, in the harness's words. Shown verbatim in the report.</summary>
    public required string Outcome { get; init; }

    /// <summary>Set when the harness itself failed - a bind error, an exception - rather than the call.</summary>
    public string? Error { get; init; }
}
