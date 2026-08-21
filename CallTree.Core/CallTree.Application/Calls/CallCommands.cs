using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;

namespace CallTree.Application.Calls;

/// <summary>
/// An instruction from the telephony layer to change the state of an existing call.
/// </summary>
/// <remarks>
/// Telephony callbacks are long-lived — a <c>SIPUserAgent</c> and its event handlers outlive any request —
/// but <c>DbContext</c> is scoped. Rather than have every call site remember to open its own DI scope,
/// telephony describes *what happened* as one of these and hands it to <see cref="ICallCommands"/>, which
/// owns the scoping. That keeps the SIP code free of service-locator plumbing.
/// </remarks>
public abstract record CallCommand(Guid CallId, DateTimeOffset When);

/// <summary>An answered call: the caller is now connected to us.</summary>
/// <param name="RequireScreening">
/// Whether a gate still stands between the caller and the call proper — always true for the inbound
/// spam gate, true on the Outbound path only when a PIN is configured.
/// </param>
public sealed record AnswerCall(Guid CallId, DateTimeOffset When, bool RequireScreening)
    : CallCommand(CallId, When);

/// <summary>
/// The Outbound (my cell) caller cleared the PIN gate. There is nothing to dial — they are already the
/// party being recorded — so the call simply proceeds.
/// </summary>
public sealed record PassScreening(Guid CallId, DateTimeOffset When)
    : CallCommand(CallId, When);

/// <summary>Recording has started; the file exists and is being written.</summary>
public sealed record StartRecording(Guid CallId, DateTimeOffset When, string RelativePath, ChannelLayout ChannelLayout)
    : CallCommand(CallId, When);

/// <summary>The recording is closed and its length is known.</summary>
public sealed record FinalizeRecording(Guid CallId, DateTimeOffset When, double DurationSeconds, long SizeBytes)
    : CallCommand(CallId, When);

/// <summary>The IVR spam gate reached a verdict.</summary>
public sealed record RecordScreeningOutcome(Guid CallId, ScreeningOutcome Outcome, DateTimeOffset When, string Reason)
    : CallCommand(CallId, When);

/// <summary>The call is over, from whichever side hung up.</summary>
public sealed record EndCall(Guid CallId, DateTimeOffset When, HangupInitiator Initiator, string Reason)
    : CallCommand(CallId, When);

/// <summary>
/// A new inbound call. Kept apart from <see cref="CallCommand"/> because it creates the aggregate rather
/// than mutating one, and so is the only command that returns an identifier.
/// </summary>
public sealed record StartCall(
    CallSource Source,
    SourceClassification Classification,
    PhoneNumber? CallerNumber,
    string RawCallerId,
    string SipCallId,
    DateTimeOffset When);
