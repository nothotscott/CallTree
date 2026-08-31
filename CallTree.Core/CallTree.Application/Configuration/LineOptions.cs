using CallTree.Domain.ValueObjects;

namespace CallTree.Application.Configuration;

/// <summary>
/// The two numbers that identify this installation: the DID it owns, and the operator's own mobile.
/// </summary>
/// <remarks>
/// <para>
/// These live here, in Application, for exactly the reason <see cref="StorageOptions"/> does: two
/// sibling layers need them and neither may reference the other. The SIP stack classifies callers and
/// filters INVITEs by them, and the messaging layer classifies senders, addresses its sends and filters
/// webhooks by the same two values. Binding the <c>Telephony</c> section into a second options type
/// instead would put one setting in two places, which is the trap <c>Telephony:TraceSip</c> used to be
/// in — don't.
/// </para>
/// <para>
/// <see cref="SectionName"/> is deliberately <c>Telephony</c> rather than a section of its own: the keys
/// are <c>Telephony:DidNumber</c> and <c>Telephony:MyCellNumber</c> in every existing
/// <c>config.json</c>, environment variable and deployment note, and a settings type is not worth
/// breaking those over. The section is shared with the Telephony layer's own options type; no key is.
/// </para>
/// </remarks>
public sealed record LineOptions
{
    public const string SectionName = "Telephony";

    /// <summary>
    /// The DID this instance owns. INVITEs and message webhooks addressed to anything else are turned
    /// away before a row is created — an open SIP port attracts constant dial-plan probing, and a
    /// messaging webhook URL is just as public. Blank accepts anything, which is the pre-Phase-2 behaviour.
    /// </summary>
    public string DidNumber { get; init; } = "";

    /// <summary>
    /// The operator's mobile. Calls and texts from this number are classified
    /// <see cref="Domain.Calls.CallSource.Outbound"/> / <see cref="Domain.Messages.MessageSource.Outbound"/>,
    /// and it is where inbound calls are bridged and inbound texts forwarded.
    /// </summary>
    public string MyCellNumber { get; init; } = "";

    /// <summary>The DID as a number, or null when it is unset or unparseable — which disables the filter.</summary>
    public PhoneNumber? Did => PhoneNumber.TryParse(DidNumber, out var number) ? number : null;

    /// <summary>The mobile as a number, or null when it is unset or unparseable.</summary>
    public PhoneNumber? MyCell => PhoneNumber.TryParse(MyCellNumber, out var number) ? number : null;
}
