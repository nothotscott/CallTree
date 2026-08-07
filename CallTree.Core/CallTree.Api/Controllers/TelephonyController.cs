using CallTree.Api.Settings;
using CallTree.Domain.ValueObjects;
using CallTree.Telephony;
using CallTree.Telephony.Audio;
using CallTree.Telephony.Configuration;
using CallTree.Telephony.Status;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CallTree.Api.Controllers;

/// <summary>
/// Live state of the SIP stack. Read-only, and — like the rest of the API — unauthenticated: it
/// discloses the trunk registrar, the address of record and the public host. See TODO.md.
/// </summary>
[ApiController]
[Route("api/telephony")]
public class TelephonyController(
    TelephonyStatus status,
    TelephonySettingsWatcher settingsWatcher,
    IOptionsMonitor<TelephonyOptions> telephonyOptions,
    PromptLibrary prompts) : ControllerBase
{
    /// <summary>Whether the trunk is registered, and everything needed to work out why it is not.</summary>
    [HttpGet("status")]
    [ProducesResponseType<TelephonyStatusResponse>(StatusCodes.Status200OK)]
    public ActionResult<TelephonyStatusResponse> Status()
    {
        var snapshot = status.Current;
        var telephony = telephonyOptions.CurrentValue;

        return Ok(new TelephonyStatusResponse
        {
            RegistrationState = snapshot.RegistrationState,
            RegistrationMessage = snapshot.RegistrationMessage,
            RegisteredUri = snapshot.RegisteredUri,
            RegistrarContact = snapshot.RegistrarContact,
            RegistrarServer = snapshot.RegistrarServer,
            RegistrationChangedAt = snapshot.RegistrationChangedAt,
            LastRegisteredAt = snapshot.LastRegisteredAt,
            RegistrationCount = snapshot.RegistrationCount,
            ExpirySeconds = snapshot.ExpirySeconds,
            StartedAt = snapshot.StartedAt,
            ListeningEndpoints = snapshot.ListeningEndpoints,
            ContactHost = snapshot.ContactHost,
            SdpAddress = snapshot.SdpAddress,
            RtpPortRange = snapshot.RtpPortRange,
            // Read from configuration rather than the snapshot: both follow configuration live, so the
            // snapshot would go stale the moment either is changed from the settings page.
            DidFilterActive = PhoneNumber.TryParse(telephony.DidNumber, out _),
            CellNumberConfigured = PhoneNumber.TryParse(telephony.MyCellNumber, out _),
            TraceSipEnabled = telephony.TraceSip,
            PromptsRoot = prompts.Root,
            PromptsLoaded = prompts.Loaded,
            PromptsMissing = prompts.Missing,
            PendingRestartKeys = settingsWatcher.PendingRestartKeys,
        });
    }
}
