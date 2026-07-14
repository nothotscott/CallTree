namespace CallTree.Domain.Calls;

/// <summary>Business direction of the call, not the SIP direction of any individual leg.</summary>
public enum CallSource
{
    /// <summary>Call originated from my cell (auto-record path).</summary>
    Outbound,

    /// <summary>Call from anyone else (IVR screening + bridge path).</summary>
    Inbound,
}

/// <summary>Why the call was classified with its <see cref="CallSource"/> — caller ID is spoofable.</summary>
public enum SourceClassification
{
    Default,
    CallerIdMatch,
    PinVerified,
}

public enum CallStatus
{
    Ringing,
    Screening,
    Dialing,
    InProgress,
    Completed,
    ScreenedOut,
    Missed,
    Failed,
}

public enum LegDirection
{
    Inbound,
    Outbound,
}

public enum HangupInitiator
{
    Remote,
    Local,
}

public enum ChannelLayout
{
    Mono,

    /// <summary>Stereo file with one call leg per channel (left = caller, right = my cell).</summary>
    StereoPerLeg,
}
