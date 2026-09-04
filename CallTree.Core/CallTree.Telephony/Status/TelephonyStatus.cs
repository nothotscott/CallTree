namespace CallTree.Telephony.Status;

/// <summary>Where the trunk registration stands.</summary>
public enum TrunkRegistrationState
{
    /// <summary>No trunk host or username, so the SIP stack never started. "Telephony is idle."</summary>
    NotConfigured,

    /// <summary>A REGISTER has been sent and nothing has come back yet.</summary>
    Registering,

    /// <summary>The registrar accepted the binding.</summary>
    Registered,

    /// <summary>A single attempt failed. The agent keeps retrying.</summary>
    TemporaryFailure,

    /// <summary>Registration failed in a way the agent treats as final.</summary>
    Failed,

    /// <summary>The binding was removed — by us on shutdown, or by the registrar.</summary>
    Removed,
}

/// <summary>
/// An immutable view of what the SIP stack is doing, safe to hand to a request thread.
/// </summary>
/// <remarks>
/// Registration events arrive on SIPSorcery's threads while the API reads on request threads, so the
/// state is replaced wholesale rather than mutated field by field — a status page that showed a
/// registered state next to a failure message would be worse than no status page at all.
/// </remarks>
public sealed record TelephonyStatusSnapshot
{
    public static readonly TelephonyStatusSnapshot Idle = new();

    public TrunkRegistrationState RegistrationState { get; init; } = TrunkRegistrationState.NotConfigured;

    /// <summary>The registrar's explanation of the last failure, verbatim. Null when there is none.</summary>
    public string? RegistrationMessage { get; init; }

    /// <summary>The address of record we register.</summary>
    public string? RegisteredUri { get; init; }

    /// <summary>
    /// The Contact the registrar echoed back in its 200 OK — the binding it will actually dial. The
    /// fastest way to catch a NAT misconfiguration: if this is a LAN address, inbound calls cannot
    /// reach us no matter how healthy the registration looks from this side.
    /// </summary>
    public string? RegistrarContact { get; init; }

    public DateTimeOffset? RegistrationChangedAt { get; init; }

    public DateTimeOffset? LastRegisteredAt { get; init; }

    /// <summary>How many times the binding has been (re-)established since startup.</summary>
    public int RegistrationCount { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The SIP endpoints actually bound, as reported by the transport.</summary>
    public IReadOnlyList<string> ListeningEndpoints { get; init; } = [];

    /// <summary>What we put in Contact. Null means the LAN address, which breaks inbound calls behind NAT.</summary>
    public string? ContactHost { get; init; }

    /// <summary>The address rewritten into SDP. Null means the LAN address, which breaks audio behind NAT.</summary>
    public string? SdpAddress { get; init; }

    public string? RegistrarServer { get; init; }

    public int ExpirySeconds { get; init; }

    public string? RtpPortRange { get; init; }

    /// <summary>
    /// True when the stack was started in loopback simulation mode (<c>Spoof:Enabled</c>): no trunk, no
    /// registration, outbound legs dialled at the harness. Separate from
    /// <see cref="RegistrationState"/> rather than a member of it, because "not registered" and "not
    /// even trying, on purpose" are different answers to different questions and the enum is what the
    /// UI colours a badge from.
    /// </summary>
    public bool Spoofing { get; init; }
}

/// <summary>Holds the current <see cref="TelephonyStatusSnapshot"/>. Singleton.</summary>
public sealed class TelephonyStatus
{
    private readonly Lock _gate = new();
    private TelephonyStatusSnapshot _current = TelephonyStatusSnapshot.Idle;

    public TelephonyStatusSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Replaces the snapshot. The change runs under the lock, so read-modify-write is safe.</summary>
    public void Update(Func<TelephonyStatusSnapshot, TelephonyStatusSnapshot> change)
    {
        lock (_gate)
        {
            _current = change(_current);
        }
    }
}
