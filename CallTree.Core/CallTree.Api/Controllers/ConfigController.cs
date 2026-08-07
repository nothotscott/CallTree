using CallTree.Api.Settings;
using CallTree.Domain.ValueObjects;
using CallTree.Telephony;
using CallTree.Telephony.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CallTree.Api.Controllers;

/// <summary>
/// Reads and writes the Telephony and Trunk configuration sections.
/// </summary>
/// <remarks>
/// There is no authentication in front of this, and it is a good deal more dangerous than the call log:
/// it can point the trunk somewhere else, set the DID filter that keeps toll-fraud probes out, and set
/// the trunk password. The assumed posture is LAN-only — see TODO.md before exposing the API beyond the
/// local network. The password is write-only: it is never returned by any response here.
/// </remarks>
[ApiController]
[Route("api/config")]
public class ConfigController(
    IOptionsMonitor<TelephonyOptions> telephonyOptions,
    IOptionsMonitor<TrunkOptions> trunkOptions,
    TelephonySettingsWatcher settingsWatcher,
    RuntimeConfigFile configFile,
    ILogger<ConfigController> logger) : ControllerBase
{
    /// <summary>The effective configuration, after appsettings, the config file and the environment.</summary>
    [HttpGet]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    public ActionResult<SettingsResponse> Get()
    {
        var telephony = telephonyOptions.CurrentValue;
        var trunk = trunkOptions.CurrentValue;

        return Ok(Describe(telephony, trunk, settingsWatcher.PendingRestartKeys));
    }

    /// <summary>
    /// Saves both sections to the config file. Values supplied by an environment variable stay
    /// overridden — the response says which. Settings the running SIP stack only reads at startup are
    /// listed in <c>pendingRestartKeys</c> rather than silently ignored.
    /// </summary>
    [HttpPut]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SettingsResponse>> Update(
        [FromBody] SettingsUpdate update,
        CancellationToken cancellationToken)
    {
        if (!Validate(update))
        {
            return ValidationProblem(ModelState);
        }

        var telephony = SettingsDocument.Apply(telephonyOptions.CurrentValue, update.Telephony);
        var trunk = SettingsDocument.Apply(trunkOptions.CurrentValue, update.Trunk, update.TrunkPassword);

        // Computed before the write, because the file watcher reloads configuration asynchronously:
        // reading the watcher again straight afterwards would usually still describe the old values.
        var pendingRestartKeys = settingsWatcher.PendingRestartKeysFor(telephony, trunk);

        try
        {
            await configFile.WriteAsync(SettingsDocument.Apply(configFile.Read(), update), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not write the configuration file at {Path}", configFile.Path);
            return Problem(
                title: "The configuration could not be saved.",
                detail: $"Writing {configFile.Path} failed: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        logger.LogInformation(
            "Configuration saved to {Path}{PasswordNote}",
            configFile.Path,
            update.TrunkPassword is null ? "" : " (including the trunk password)");

        if (telephony.DidNumber.Length == 0)
        {
            logger.LogWarning(
                "Telephony:DidNumber was saved empty - every INVITE reaching this port will be answered, "
                + "including the dial-plan probes that scanners aim at any open SIP port.");
        }

        return Ok(Describe(telephony, trunk, pendingRestartKeys));
    }

    private SettingsResponse Describe(
        TelephonyOptions telephony,
        TrunkOptions trunk,
        IReadOnlyList<string> pendingRestartKeys)
    {
        var environmentOverrides = SettingsDocument.EnvironmentOverrides();

        return new SettingsResponse
        {
            Telephony = SettingsDocument.ToSettings(telephony),
            Trunk = SettingsDocument.ToSettings(trunk),
            TrunkPasswordSet = trunk.Password.Length > 0,
            TrunkConfigured = trunk.IsConfigured,
            // A key the environment supplies cannot be waiting on a restart: restarting would read the
            // same environment again. Saying otherwise would send the operator to bounce the service
            // for a change that is never going to take effect until the variable goes away.
            PendingRestartKeys = [.. pendingRestartKeys.Except(environmentOverrides)],
            RestartOnlyKeys = TelephonySettingsWatcher.StartupOnlyKeys,
            EnvironmentOverrides = environmentOverrides,
            ConfigFilePath = configFile.Path,
            ConfigFileExists = configFile.Exists,
        };
    }

    /// <summary>
    /// Cross-field checks the data annotations cannot express. Ranges are already enforced by
    /// <c>[ApiController]</c> before this runs.
    /// </summary>
    private bool Validate(SettingsUpdate update)
    {
        // Keys match the property paths the data annotations produce ("Trunk.Port"), so the UI has one
        // shape of error to bind to rather than two.
        if (update.Telephony.RtpPortEnd < update.Telephony.RtpPortStart)
        {
            ModelState.AddModelError(
                "Telephony.RtpPortEnd",
                "The end of the RTP port range must not be below its start.");
        }

        // Blank is allowed and meaningful for both numbers; anything else has to parse, or the setting
        // silently does nothing - an unparseable DID would disable the toll-fraud filter outright.
        ValidateNumber(update.Telephony.MyCellNumber, "Telephony.MyCellNumber");
        ValidateNumber(update.Telephony.DidNumber, "Telephony.DidNumber");

        if (update.Trunk.Host.Length == 0 && update.Trunk.Username.Length > 0)
        {
            ModelState.AddModelError("Trunk.Host", "A trunk username without a host will not register.");
        }

        return ModelState.IsValid;
    }

    private void ValidateNumber(string value, string field)
    {
        if (value.Length > 0 && !PhoneNumber.TryParse(value, out _))
        {
            ModelState.AddModelError(field, $"'{value}' is not a phone number this instance can match on.");
        }
    }
}
