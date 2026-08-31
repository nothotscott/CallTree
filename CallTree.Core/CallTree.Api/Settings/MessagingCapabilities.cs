namespace CallTree.Api.Settings;

/// <summary>
/// What this instance can actually do with SMS, for a UI deciding what is worth putting on screen.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is derivable from <c>GET /api/config</c>, and deliberately has its own endpoint
/// anyway. The config document is the settings page's write model: it carries the config file path, the
/// environment overrides and the pending-restart keys, and it is the response for an endpoint that can
/// also point the trunk somewhere else. Fetching all of that on every page load, from the root layout,
/// to decide whether one navigation link is worth showing would tie the whole frontend to it. These two
/// booleans are the entire question.
/// </para>
/// <para>
/// They are also genuinely two questions. Messaging can be switched on with no API key — a receive-only
/// line, which is the only mode a US long code has before it is 10DLC-registered — and then the message
/// log is worth showing while every column about what was relayed onward is not.
/// </para>
/// </remarks>
public sealed record MessagingCapabilities
{
    /// <summary>Whether the webhook is accepted at all. False means there is nothing to show.</summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Whether an API key is configured, which is exactly whether anything can be sent: forwarded on to
    /// the mobile, sent by a <c>{recipient} body</c> command, or texted back on a failure. False leaves
    /// a receive-only line whose messages end at <c>Recorded</c>.
    /// </summary>
    public required bool CanSend { get; init; }
}
