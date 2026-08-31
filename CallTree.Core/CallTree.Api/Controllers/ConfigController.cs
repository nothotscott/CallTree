using CallTree.Api.Settings;
using CallTree.Application.Configuration;
using CallTree.Domain.ValueObjects;
using CallTree.Messaging.Configuration;
using CallTree.Telephony;
using CallTree.Telephony.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CallTree.Api.Controllers;

/// <summary>
/// Reads and writes the Telephony, Trunk and Messaging configuration sections.
/// </summary>
/// <remarks>
/// There is no authentication in front of this, and it is a good deal more dangerous than the call log:
/// it can point the trunk somewhere else, set the DID filter that keeps toll-fraud probes out, and set
/// the trunk password. The assumed posture is LAN-only — see TODO.md before exposing the API beyond the
/// local network. The trunk password, the outbound PIN and the messaging API key are all write-only:
/// none is ever returned by any response here.
/// </remarks>
[ApiController]
[Route("api/config")]
public class ConfigController(
    IOptionsMonitor<TelephonyOptions> telephonyOptions,
    IOptionsMonitor<LineOptions> lineOptions,
    IOptionsMonitor<TrunkOptions> trunkOptions,
    IOptionsMonitor<MessagingOptions> messagingOptions,
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
        var line = lineOptions.CurrentValue;
        var trunk = trunkOptions.CurrentValue;
        var messaging = messagingOptions.CurrentValue;

        return Ok(Describe(telephony, line, trunk, messaging, settingsWatcher.PendingRestartKeys));
    }

    /// <summary>
    /// Saves every section to the config file. Values supplied by an environment variable stay
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

        var telephony = SettingsDocument.Apply(telephonyOptions.CurrentValue, update.Telephony, update.OutboundPin);
        var line = SettingsDocument.Apply(lineOptions.CurrentValue, update.Telephony);
        var trunk = SettingsDocument.Apply(trunkOptions.CurrentValue, update.Trunk, update.TrunkPassword);
        var messaging = SettingsDocument.Apply(messagingOptions.CurrentValue, update.Messaging, update.MessagingApiKey);

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
            "Configuration saved to {Path}{PasswordNote}{ApiKeyNote}",
            configFile.Path,
            update.TrunkPassword is null ? "" : " (including the trunk password)",
            update.MessagingApiKey is null ? "" : " (including the messaging API key)");

        if (line.DidNumber.Length == 0)
        {
            logger.LogWarning(
                "Telephony:DidNumber was saved empty - every INVITE reaching this port will be answered, "
                + "including the dial-plan probes that scanners aim at any open SIP port. Inbound messages "
                + "addressed to any number will be accepted too.");
        }

        if (update.OutboundPin is { Length: 0 })
        {
            logger.LogWarning(
                "Telephony:OutboundPin was cleared - the outbound path now answers and records on a caller ID "
                + "match alone, and caller ID is trivially spoofable.");
        }

        if (messaging.Enabled && !messaging.RequireSignature)
        {
            logger.LogWarning(
                "Messaging:RequireSignature is off - the webhook accepts requests from anyone who finds the "
                + "URL, and reaching it is enough to make this instance send a text.");
        }

        // Information, not a warning: this is a supported mode rather than a half-finished setup. It is
        // the only way to run a US long code that carriers have not approved for sending (no 10DLC
        // registration), and messages taken in this way end at Recorded rather than Failed.
        if (messaging.Enabled && messaging.ApiKey.Length == 0)
        {
            logger.LogInformation(
                "Messaging is enabled with no Messaging:ApiKey - this line is receive-only. Texts to the DID "
                + "are recorded and nothing is sent on, including the forward to Telephony:MyCellNumber.");
        }

        return Ok(Describe(telephony, line, trunk, messaging, pendingRestartKeys));
    }

    private SettingsResponse Describe(
        TelephonyOptions telephony,
        LineOptions line,
        TrunkOptions trunk,
        MessagingOptions messaging,
        IReadOnlyList<string> pendingRestartKeys)
    {
        var environmentOverrides = SettingsDocument.EnvironmentOverrides();

        return new SettingsResponse
        {
            Telephony = SettingsDocument.ToSettings(telephony, line),
            Trunk = SettingsDocument.ToSettings(trunk),
            Messaging = SettingsDocument.ToSettings(messaging),
            TrunkPasswordSet = trunk.Password.Length > 0,
            OutboundPinSet = telephony.OutboundPin.Length > 0,
            MessagingApiKeySet = messaging.ApiKey.Length > 0,
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

        ValidateMessaging(update.Messaging);

        return ModelState.IsValid;
    }

    private void ValidateMessaging(MessagingSettings messaging)
    {
        var publicKey = messaging.PublicKey.Trim();

        // Refused rather than warned about: a key that is not 32 bytes of base64 can never verify a
        // signature, so every webhook would be turned away and the whole feature would look dead with
        // nothing but a 403 in the log to say why.
        if (publicKey.Length > 0 && !IsEd25519PublicKey(publicKey))
        {
            ModelState.AddModelError(
                "Messaging.PublicKey",
                "The webhook public key must be the base64 Ed25519 key from the provider's portal (32 bytes decoded).");
        }

        // The combination that silently accepts nothing. Better to refuse the save and say which field
        // is missing than to store a configuration whose only symptom is that no message ever arrives.
        if (messaging.Enabled && messaging.RequireSignature && publicKey.Length == 0)
        {
            ModelState.AddModelError(
                "Messaging.PublicKey",
                "Messaging is enabled with signature checking on, so the provider's webhook public key is "
                + "required - without it every webhook is refused.");
        }
    }

    private static bool IsEd25519PublicKey(string value)
    {
        Span<byte> decoded = stackalloc byte[64];
        return Convert.TryFromBase64String(value, decoded, out var written) && written == 32;
    }

    private void ValidateNumber(string value, string field)
    {
        if (value.Length > 0 && !PhoneNumber.TryParse(value, out _))
        {
            ModelState.AddModelError(field, $"'{value}' is not a phone number this instance can match on.");
        }
    }
}
