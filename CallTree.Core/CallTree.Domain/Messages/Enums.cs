namespace CallTree.Domain.Messages;

/// <summary>
/// Business direction of a message, not the direction of any individual provider message.
/// </summary>
/// <remarks>
/// The same naming note as <see cref="Calls.CallSource"/> applies, and for the same reason: both kinds
/// arrive at CallTree as an *inbound* message to the DID. An Outbound one is a send command the operator
/// texted in from their own number; an Inbound one is a stranger texting the DID.
/// </remarks>
public enum MessageSource
{
    /// <summary>Came from my cell: a "{recipient} body" command asking CallTree to send something.</summary>
    Outbound,

    /// <summary>Came from anyone else: forwarded to my cell.</summary>
    Inbound,
}

/// <summary>
/// State of the message CallTree received, and of what it did about it.
///
///   Received -> Relaying -> Relayed   (the provider accepted the message we sent on)
///   Received -> Recorded              (receive-only: no API key, so nothing was attempted)
///   Received -> Rejected              (nothing to send: unparseable command, or no cell configured)
///   Relaying -> Failed                (the provider refused the message we sent on)
/// </summary>
/// <remarks>
/// Whether the relayed message was actually *delivered* is not a status here — it is a fact recorded on
/// the <see cref="Relay"/>, the same way a recording is a fact about a call rather than a call status.
/// A carrier can take minutes to say, and it says so on a separate webhook.
/// </remarks>
public enum MessageStatus
{
    Received,

    /// <summary>
    /// Taken in and deliberately not sent on, because this line has no <c>Messaging:ApiKey</c> and so
    /// cannot send anything at all. Terminal, and emphatically not a failure: it is the entire expected
    /// outcome of running the line receive-only, which is a supported way to own a number that only has
    /// to take delivery of verification codes. Without this, every such message would end at
    /// <see cref="Failed"/> with "Messaging:ApiKey is not set" against it, and the log would read as
    /// broken while the instance did exactly what it was configured to do.
    /// </summary>
    Recorded,

    Relaying,
    Relayed,
    Rejected,
    Failed,
}

/// <summary>
/// What the carrier last said about the message CallTree sent on. Provider statuses are collapsed to
/// these because the useful question is only ever "did it arrive, is it still trying, or is it lost".
/// </summary>
public enum RelayDelivery
{
    /// <summary>Accepted by the provider; no carrier verdict yet.</summary>
    Queued,

    /// <summary>Handed to the carrier.</summary>
    Sent,

    Delivered,

    /// <summary>The carrier neither confirmed nor denied. Common on some US routes; usually fine.</summary>
    Unconfirmed,

    /// <summary>The carrier rejected it, or gave up.</summary>
    Failed,
}
