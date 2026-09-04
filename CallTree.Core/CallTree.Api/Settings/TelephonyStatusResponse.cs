using CallTree.Telephony.Status;

namespace CallTree.Api.Settings;

/// <summary>
/// What the SIP stack is doing right now: the answer to "is my trunk up, and if not, why not".
/// </summary>
/// <remarks>
/// Deliberately more than the registration state. Every field here corresponds to a failure that
/// otherwise presents identically — as a caller hearing a busy tone with nothing at all in the log.
/// </remarks>
public sealed record TelephonyStatusResponse
{
    public required TrunkRegistrationState RegistrationState { get; init; }

    /// <summary>The registrar's own words about the last failure. Null when there has not been one.</summary>
    public required string? RegistrationMessage { get; init; }

    public required string? RegisteredUri { get; init; }

    /// <summary>
    /// The binding the registrar echoed in its 200 OK — the address it will actually dial. A LAN
    /// address here means inbound calls cannot arrive however healthy registration looks locally.
    /// </summary>
    public required string? RegistrarContact { get; init; }

    public required string? RegistrarServer { get; init; }

    public required DateTimeOffset? RegistrationChangedAt { get; init; }

    public required DateTimeOffset? LastRegisteredAt { get; init; }

    public required int RegistrationCount { get; init; }

    public required int ExpirySeconds { get; init; }

    public required DateTimeOffset? StartedAt { get; init; }

    public required IReadOnlyList<string> ListeningEndpoints { get; init; }

    /// <summary>What we advertise in Contact. Null means the LAN address, which breaks inbound calls behind NAT.</summary>
    public required string? ContactHost { get; init; }

    /// <summary>What we rewrite into SDP. Null means the LAN address, which breaks audio behind NAT.</summary>
    public required string? SdpAddress { get; init; }

    public required string? RtpPortRange { get; init; }

    /// <summary>
    /// True when this process is running against the local SIP harness rather than a trunk. Worth
    /// surfacing prominently: every other field on this response describes a line that cannot receive a
    /// real call.
    /// </summary>
    public required bool Spoofing { get; init; }

    /// <summary>False when the DID filter is off and every dial-plan probe reaching the port is answered.</summary>
    public required bool DidFilterActive { get; init; }

    /// <summary>False when no caller ID can be classified as the operator's own.</summary>
    public required bool CellNumberConfigured { get; init; }

    public required bool TraceSipEnabled { get; init; }

    public required string PromptsRoot { get; init; }

    public required IReadOnlyList<string> PromptsLoaded { get; init; }

    /// <summary>Required prompts that did not load; those IVR steps will be silent.</summary>
    public required IReadOnlyList<string> PromptsMissing { get; init; }

    /// <summary>Saved settings the running stack has not picked up. Same list the settings page shows.</summary>
    public required IReadOnlyList<string> PendingRestartKeys { get; init; }
}
