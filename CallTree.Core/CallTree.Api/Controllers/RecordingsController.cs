using System.ComponentModel.DataAnnotations;
using CallTree.Application.Abstractions;
using CallTree.Application.Calls;
using CallTree.Application.Common;
using CallTree.Domain.Calls;
using CallTree.Telephony.Audio;
using Microsoft.AspNetCore.Mvc;

namespace CallTree.Api.Controllers;

/// <summary>
/// Browsing, playback and naming of recordings. Same posture as <see cref="CallsController"/>: no
/// authentication in front of this, assumed LAN-only, and recordings are the most sensitive thing this
/// API serves. The name is the only thing here anyone can change - everything else is a record of what
/// the telephony layer did.
/// </summary>
[ApiController]
[Route("api/recordings")]
public class RecordingsController(
    IRecordingQueries recordingQueries,
    RecordingService recordingService,
    RecordingStore recordingStore) : ControllerBase
{
    /// <summary>Lists recordings, most recently created first.</summary>
    /// <param name="page">1-based page number. Values below 1 are treated as 1.</param>
    /// <param name="pageSize">Rows per page, capped at <see cref="RecordingListQuery.MaxPageSize"/>.</param>
    /// <param name="search">Optional case-insensitive substring of the recording name.</param>
    [HttpGet]
    [ProducesResponseType<PagedResult<RecordingSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<RecordingSummary>>> List(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var query = RecordingListQuery.Create(page, pageSize, search);
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
    /// Renames a recording. The name is the operator's own label for it; everything else about a
    /// recording is a record of what happened and is not editable.
    /// </summary>
    /// <remarks>
    /// Blank is rejected rather than taken as "put the default back": the caller and the date it would
    /// be rebuilt from are both fields of their own, and a nameless row leaves nothing to click in the
    /// list. See <see cref="RecordingName"/>.
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType<RecordingSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecordingSummary>> Rename(
        Guid id,
        [FromBody] RecordingUpdate update,
        CancellationToken cancellationToken)
    {
        RecordingSummary? recording;
        try
        {
            recording = await recordingService.RenameAsync(id, update.Name, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            // Nothing is expected to reach this today: RecordingUpdate's annotations already reject
            // null, empty, whitespace-only (Required trims) and over-long. It is here so that a rule the
            // domain gains and the annotations do not answers 400 with the domain's own message rather
            // than 500 - the domain stays the single source of what a legal name is.
            ModelState.AddModelError(nameof(RecordingUpdate.Name), ex.Message);
            return ValidationProblem(ModelState);
        }

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

/// <summary>
/// Body of a rename. A record rather than a bare string so that a later editable field (a note, say) is
/// an added property rather than a changed content type.
/// </summary>
/// <remarks>
/// The annotations are what turn a bad name into a 400 with a field message; <c>Recording.Rename</c>
/// enforces the same rules again as a domain invariant, where a breach would be a 500. Both are
/// deliberate - the model binder is the contract, the domain is the guarantee.
/// </remarks>
public sealed record RecordingUpdate
{
    [Required(AllowEmptyStrings = false)]
    [MaxLength(RecordingName.MaxLength)]
    public string Name { get; init; } = "";
}
