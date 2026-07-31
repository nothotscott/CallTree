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
public sealed record AnswerCall(Guid CallId, DateTimeOffset When)
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
