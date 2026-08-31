using CallTree.Domain.Messages;
using CallTree.Domain.ValueObjects;

namespace CallTree.Messaging;

/// <summary>
/// Composes the bodies CallTree sends on the operator's behalf.
/// </summary>
/// <remarks>
/// Pure and separate from the relay so the one thing that can silently break the feature is testable:
/// the provider refuses a body over <see cref="SmsText.MaxLength"/> outright, and forwarding *adds* to
/// a body that may already be at that limit. Everything here budgets for its own prefix rather than
/// trusting the incoming message to have left room.
/// </remarks>
public static class ForwardText
{
    /// <summary>
    /// A message from a stranger, dressed so the operator can see who it is from and reply to them with
    /// a send command. The number is on its own line, in the same grouping the rest of the UI uses, so
    /// it can be read off the notification and typed back in.
    /// </summary>
    public static string ForInbound(PhoneNumber from, string body, int mediaCount)
    {
        var prefix = $"{from.ToDisplayString()}:\n";

        // Attachments are recorded but never fetched or forwarded, so this note is the only trace the
        // operator gets that there is a picture they have not seen. Saying nothing would be worse than
        // saying "there was something here" - see the media note in SmsRelayService.
        var suffix = mediaCount switch
        {
            <= 0 => "",
            1 => "\n[1 attachment, not forwarded]",
            _ => $"\n[{mediaCount} attachments, not forwarded]",
        };

        var budget = SmsText.MaxLength - prefix.Length - suffix.Length;

        return budget <= 0
            ? SmsText.Truncate(prefix + body + suffix)
            : prefix + SmsText.Truncate(body ?? "", budget) + suffix;
    }

    /// <summary>
    /// The notice sent back when a send command could not be carried out. Prefixed so it is obviously
    /// from CallTree rather than a reply from whoever the operator was trying to reach.
    /// </summary>
    public static string ForFailure(string reason) =>
        SmsText.Truncate($"CallTree could not send that: {reason}");
}
