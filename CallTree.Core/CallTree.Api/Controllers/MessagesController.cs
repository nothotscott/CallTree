using CallTree.Api.Settings;
using CallTree.Application.Abstractions;
using CallTree.Application.Common;
using CallTree.Application.Messages;
using CallTree.Domain.Messages;
using CallTree.Messaging.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CallTree.Api.Controllers;

/// <summary>
/// Read-only view of the message log. Same posture as <see cref="CallsController"/>: no authentication
/// in front of it, assumed LAN-only. Note this one serves the *contents* of texts, which is a good deal
/// more sensitive than a call log's metadata — see TODO.md before exposing the API beyond the LAN.
/// </summary>
[ApiController]
[Route("api/messages")]
public class MessagesController(
    IMessageQueries messageQueries,
    IOptionsMonitor<MessagingOptions> messagingOptions) : ControllerBase
{
    /// <summary>
    /// Whether messaging is on, and whether it can send. Cheap enough for the UI's root layout to ask on
    /// every load, which is what lets the navigation omit a Messages link the instance cannot fill.
    /// </summary>
    /// <remarks>
    /// Read from <c>IOptionsMonitor</c> per request like every other <c>Messaging:</c> setting, so it
    /// follows a settings change without a restart. Nothing secret is returned: whether a key exists,
    /// never the key.
    /// </remarks>
    [HttpGet("capabilities")]
    [ProducesResponseType<MessagingCapabilities>(StatusCodes.Status200OK)]
    public ActionResult<MessagingCapabilities> Capabilities()
    {
        var messaging = messagingOptions.CurrentValue;

        return Ok(new MessagingCapabilities
        {
            Enabled = messaging.Enabled,
            CanSend = messaging.IsConfigured,
        });
    }

    /// <summary>Lists messages, most recently received first.</summary>
    /// <param name="page">1-based page number. Values below 1 are treated as 1.</param>
    /// <param name="pageSize">Rows per page, capped at <see cref="MessageListQuery.MaxPageSize"/>.</param>
    /// <param name="source">Optional business-direction filter: Inbound (from a stranger) or Outbound (a send command).</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="search">Optional case-insensitive substring of the received body.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<MessageSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MessageSummary>>> List(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] MessageSource? source,
        [FromQuery] MessageStatus? status,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var query = MessageListQuery.Create(page, pageSize, source, status, search);
        return Ok(await messageQueries.ListAsync(query, cancellationToken));
    }
}
