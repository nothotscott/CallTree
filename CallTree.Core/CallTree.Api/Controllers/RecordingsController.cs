using CallTree.Application.Abstractions;
using CallTree.Application.Calls;
using CallTree.Application.Common;
using CallTree.Telephony.Audio;
using Microsoft.AspNetCore.Mvc;

namespace CallTree.Api.Controllers;

/// <summary>
/// Read-only view of recordings. Same posture as <see cref="CallsController"/>: no authentication in
/// front of this, assumed LAN-only, and recordings are the most sensitive thing this API serves.
/// </summary>
[ApiController]
[Route("api/recordings")]
public class RecordingsController(IRecordingQueries recordingQueries, RecordingStore recordingStore) : ControllerBase
{
    /// <summary>Lists recordings, most recently created first.</summary>
    /// <param name="page">1-based page number. Values below 1 are treated as 1.</param>
    /// <param name="pageSize">Rows per page, capped at <see cref="RecordingListQuery.MaxPageSize"/>.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<RecordingSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<RecordingSummary>>> List(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = RecordingListQuery.Create(page, pageSize);
        return Ok(await recordingQueries.ListAsync(query, cancellationToken));
    }

    /// <summary>Details for one recording, looked up by recording id (not call id).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<RecordingSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecordingSummary>> Get(Guid id, CancellationToken cancellationToken)
    {
        var recording = await recordingQueries.GetAsync(id, cancellationToken);
        return recording is null ? NotFound() : Ok(recording);
    }

    /// <summary>
    /// Streams the WAV itself. Range-enabled so an <c>&lt;audio&gt;</c> element can seek. Not served until
    /// the recording is finalized: NAudio's WaveFileWriter only patches the RIFF header every few seconds
    /// (see CallRecorder.FlushIfDue), so a file still being written can look shorter than what is really
    /// on disk.
    /// </summary>
    [HttpGet("{id:guid}/audio")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetAudio(Guid id, CancellationToken cancellationToken)
    {
        var location = await recordingQueries.GetFileLocationAsync(id, cancellationToken);
        if (location is null)
        {
            return NotFound();
        }

        if (!location.Value.IsFinalized)
        {
            return Conflict("Recording is still in progress.");
        }

        if (!recordingStore.TryResolve(location.Value.RelativePath, out var fullPath)
            || !System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        return PhysicalFile(fullPath, "audio/wav", enableRangeProcessing: true);
    }
}
