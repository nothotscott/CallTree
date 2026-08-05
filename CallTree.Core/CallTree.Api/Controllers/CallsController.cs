using CallTree.Application.Abstractions;
using CallTree.Application.Calls;
using CallTree.Application.Common;
using CallTree.Domain.Calls;
using Microsoft.AspNetCore.Mvc;

namespace CallTree.Api.Controllers;

/// <summary>
/// Read-only view of the call log. There is no authentication in front of this: the assumed posture is
/// LAN-only, and call records are sensitive. See TODO.md before exposing the API beyond the local network.
/// </summary>
[ApiController]
[Route("api/calls")]
public class CallsController(ICallQueries callQueries) : ControllerBase
{
    /// <summary>Lists calls, most recent first.</summary>
    /// <param name="page">1-based page number. Values below 1 are treated as 1.</param>
    /// <param name="pageSize">Rows per page, capped at <see cref="CallListQuery.MaxPageSize"/>.</param>
    /// <param name="source">Optional business-direction filter: Inbound or Outbound.</param>
    /// <param name="status">Optional status filter.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<CallSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CallSummary>>> List(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] CallSource? source,
        [FromQuery] CallStatus? status,
        CancellationToken cancellationToken)
    {
        var query = CallListQuery.Create(page, pageSize, source, status);
        return Ok(await callQueries.ListAsync(query, cancellationToken));
    }
}
