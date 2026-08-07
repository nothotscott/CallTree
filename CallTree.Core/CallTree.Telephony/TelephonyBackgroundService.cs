using System.Net;
using CallTree.Application.Calls;
using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;
using CallTree.Telephony.Audio;
using CallTree.Telephony.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace CallTree.Telephony;

/// <summary>
/// Hosts the SIP user agent for the lifetime of the process.
/// Phase 1: registers with the trunk (or test PBX extension), answers every inbound call
/// with silence, logs it, holds 5 seconds, hangs up, and persists the Call aggregate.
/// </summary>
public class TelephonyBackgroundService(
    IOptionsMonitor<TrunkOptions> trunkOptions,
    IOptionsMonitor<TelephonyOptions> telephonyOptions,
    TelephonySettingsWatcher settingsWatcher,
    PromptLibrary prompts,
    ICallCommands calls,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly TimeSpan Phase1HoldDuration = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = loggerFactory.CreateLogger<TelephonyBackgroundService>();
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _registrationUserAgent;
    private IPAddress? _publicAddress;
    private PortRange? _rtpPortRange;
    private string _lastReportedPendingKeys = "";

    /// <summary>
    /// The configured mobile, re-read per call: it costs a parse and means the settings UI can correct a
    /// mistyped number without a restart, which matters because getting it wrong misclassifies every call.
    /// </summary>
    private PhoneNumber? MyCellNumber =>
        PhoneNumber.TryParse(telephonyOptions.CurrentValue.MyCellNumber, out var number) ? number : null;

    /// <summary>Our DID, re-read per call for the same reason.</summary>
    private PhoneNumber? DidNumber =>
        PhoneNumber.TryParse(telephonyOptions.CurrentValue.DidNumber, out var number) ? number : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var trunk = trunkOptions.CurrentValue;
        var telephony = telephonyOptions.CurrentValue;

        // Taken before the trunk check, so that configuring a trunk on an idle instance is correctly
        // reported as needing a restart rather than looking like it took effect.
        settingsWatcher.CaptureStartupSnapshot(telephony, trunk);
        WatchForRestartRequiringChanges();

        if (!trunk.IsConfigured)
        {
            _logger.LogWarning("Trunk is not configured (Trunk:Host / Trunk:Username missing) - telephony is idle.");
            return;
        }

        if (MyCellNumber is null)
        {
            _logger.LogWarning("Telephony:MyCellNumber is not set - all calls will be classified as Inbound.");
        }

        if (DidNumber is null)
        {
            _logger.LogWarning(
                "Telephony:DidNumber is not set - every INVITE reaching this port will be answered, "
                + "including the dial-plan probes that scanners aim at any open SIP port.");
        }

        // Route SIPSorcery's internal logging through the host's logging pipeline.
        SIPSorcery.LogFactory.Set(loggerFactory);

        _rtpPortRange = new PortRange(telephony.RtpPortStart, telephony.RtpPortEnd);

        _sipTransport = new SIPTransport();
        _sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, telephony.SipListenPort)));

        // Registration and everything else goes out over UDP, but a trunk configured to deliver inbound
        // calls over TCP would otherwise reach a closed port with no trace of the attempt on our side.
        if (telephony.ListenOnTcp)
        {
            _sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, telephony.SipListenPort)));
        }

        _sipTransport.SIPTransportRequestReceived += OnTransportRequestReceived;

        ConfigurePublicAddress(telephony);

        AttachSipTracing();

        _logger.LogInformation(
            "SIP listening on {Channels}; advertising {ContactHost} in Contact/SDP; RTP {RtpStart}-{RtpEnd}; SIP trace {TraceState}",
            string.Join(", ", _sipTransport.GetSIPChannels().Select(c => c.ListeningSIPEndPoint.ToString())),
            _sipTransport.ContactHost is { Length: > 0 } host ? host : "(local address - NAT will break inbound calls)",
            telephony.RtpPortStart,
            telephony.RtpPortEnd,
            telephony.TraceSip ? "on" : "off");

        // A persistent UA fields incoming INVITEs; per-leg UAs come with bridging in Phase 4.
        var listenerUserAgent = new SIPUserAgent(_sipTransport, null);
        listenerUserAgent.OnIncomingCall += (ua, request) =>
            _ = HandleIncomingCallAsync(ua, request, stoppingToken);

        StartRegistration(trunk);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
        finally
        {
            _registrationUserAgent?.Stop(sendZeroExpiryRegister: true);
            listenerUserAgent.Close();
            _sipTransport.Shutdown();
        }
    }

    /// <summary>
    /// Points the Contact URI (and the SDP connection address) at our public address instead of the LAN
    /// address SIPSorcery would otherwise substitute in. A hostname is passed through to Contact verbatim
    /// but must be resolved for SDP, which carries a bare IP.
    /// </summary>
    private void ConfigurePublicAddress(TelephonyOptions telephony)
    {
        if (telephony.PublicHost.Length == 0)
        {
            _logger.LogWarning(
                "Telephony:PublicHost is not set. Behind NAT the trunk will be told to reach us at a LAN address "
                + "and inbound calls will fail without ever reaching this process.");
            return;
        }

        _sipTransport!.ContactHost = telephony.PublicHost;

        if (IPAddress.TryParse(telephony.PublicHost, out var parsed))
        {
            _publicAddress = parsed;
            return;
        }

        try
        {
            _publicAddress = Dns.GetHostAddresses(telephony.PublicHost)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resolve Telephony:PublicHost '{PublicHost}' for use in SDP.", telephony.PublicHost);
        }

        if (_publicAddress is null)
        {
            _logger.LogWarning("Telephony:PublicHost '{PublicHost}' did not resolve to an IPv4 address; SDP will advertise the LAN address.", telephony.PublicHost);
        }
    }

    /// <summary>
    /// Logs whole SIP messages on the wire — the only reliable way to see NAT/routing problems.
    /// </summary>
    /// <remarks>
    /// The handlers are attached unconditionally and each one asks the logger whether Trace is enabled
    /// before serialising anything, so the cost when tracing is off is a delegate call and a bool. That
    /// is deliberate: it makes Telephony:TraceSip a pure logging-level switch (see
    /// <see cref="SipTraceLogLevel"/>), which in turn means it can be flipped from the settings UI
    /// while a call is misbehaving instead of requiring a restart that drops the registration and the
    /// call you were trying to look at.
    /// </remarks>
    private void AttachSipTracing()
    {
        var trace = loggerFactory.CreateLogger(SipTrace.CategoryName);

        _sipTransport!.SIPRequestOutTraceEvent += (local, remote, request) =>
        {
            if (trace.IsEnabled(LogLevel.Trace))
            {
                trace.LogTrace("SIP TX {Local} -> {Remote}\n{Message}", local, remote, request.ToString());
            }
        };
        _sipTransport.SIPRequestInTraceEvent += (local, remote, request) =>
        {
            if (trace.IsEnabled(LogLevel.Trace))
            {
                trace.LogTrace("SIP RX {Remote} -> {Local}\n{Message}", remote, local, request.ToString());
            }
        };
        _sipTransport.SIPResponseOutTraceEvent += (local, remote, response) =>
        {
            if (trace.IsEnabled(LogLevel.Trace))
            {
                trace.LogTrace("SIP TX {Local} -> {Remote}\n{Message}", local, remote, response.ToString());
            }
        };
        _sipTransport.SIPResponseInTraceEvent += (local, remote, response) =>
        {
            if (trace.IsEnabled(LogLevel.Trace))
            {
                trace.LogTrace("SIP RX {Remote} -> {Local}\n{Message}", remote, local, response.ToString());
            }
        };

        // Unparseable traffic is worth a warning whether or not tracing is on: it means something is
        // sending us malformed SIP, which no amount of application-level debugging would explain.
        _sipTransport.SIPBadRequestInTraceEvent += (local, remote, message, field, raw) =>
            trace.LogWarning("SIP RX (bad request) {Remote} -> {Local}: {Message} [{Field}]\n{Raw}", remote, local, message, field, raw);
        _sipTransport.SIPBadResponseInTraceEvent += (local, remote, message, field, raw) =>
            trace.LogWarning("SIP RX (bad response) {Remote} -> {Local}: {Message} [{Field}]\n{Raw}", remote, local, message, field, raw);
    }

    /// <summary>
    /// Reports configuration changes the running SIP stack cannot pick up. Without this a setting saved
    /// from the UI looks applied — the file is written, the API confirms it — while the sockets and the
    /// trunk binding carry on with the old values.
    /// </summary>
    private void WatchForRestartRequiringChanges()
    {
        void Report()
        {
            var pending = settingsWatcher.PendingRestartKeys;
            var summary = string.Join(", ", pending);

            // The monitor fires once per section, so a single save produces two callbacks.
            if (summary == _lastReportedPendingKeys)
            {
                return;
            }

            _lastReportedPendingKeys = summary;

            if (pending.Count > 0)
            {
                _logger.LogWarning(
                    "Configuration changed for settings that only apply at startup ({Keys}) - "
                    + "restart the process to apply them. Everything else is already live.",
                    summary);
            }
            else
            {
                _logger.LogInformation("Telephony configuration reloaded; all changes are live.");
            }
        }

        telephonyOptions.OnChange(_ => Report());
        trunkOptions.OnChange(_ => Report());
    }

    private void StartRegistration(TrunkOptions trunk)
    {
        var server = trunk.Port == 5060 ? trunk.Host : $"{trunk.Host}:{trunk.Port}";

        // Honouring a separate auth username needs the long SIPRegistrationUserAgent overload (AOR, realm,
        // contact URI and custom headers all become our responsibility). No provider in use needs it yet,
        // so fail loudly rather than let the setting look like it took effect.
        if (!string.IsNullOrWhiteSpace(trunk.AuthUsername) && trunk.AuthUsername != trunk.Username)
        {
            _logger.LogWarning(
                "Trunk:AuthUsername ('{AuthUsername}') is set but not supported yet — registering as '{Username}' instead.",
                trunk.AuthUsername,
                trunk.Username);
        }

        _registrationUserAgent = new SIPRegistrationUserAgent(
            _sipTransport,
            trunk.Username,
            trunk.Password,
            server,
            trunk.RegistrationExpirySeconds,
            exitOnUnequivocalFailure: false,
            // SIPSorcery defaults this off, producing "Contact: <sip:host:port>" with no user part.
            // Telnyx accepts the REGISTER (digest auth is valid) but cannot tie the binding to a
            // connection, so its registration status stays empty and inbound calls have no destination.
            sendUsernameInContactHeader: true);

        _registrationUserAgent.RegistrationSuccessful += (uri, _) =>
            _logger.LogInformation("SIP registration successful for {Uri}", uri);
        _registrationUserAgent.RegistrationRemoved += (uri, _) =>
            _logger.LogWarning("SIP registration removed for {Uri}", uri);
        _registrationUserAgent.RegistrationTemporaryFailure += (uri, _, message) =>
            _logger.LogWarning("SIP registration temporary failure for {Uri}: {Message}", uri, message);
        _registrationUserAgent.RegistrationFailed += (uri, _, message) =>
            _logger.LogError("SIP registration failed for {Uri}: {Message}", uri, message);

        _registrationUserAgent.Start();
    }

    private async Task HandleIncomingCallAsync(SIPUserAgent userAgent, SIPRequest request, CancellationToken stoppingToken)
    {
        var rawCallerId = request.Header.From.FromURI.User ?? "";
        var remoteEndPoint = request.RemoteSIPEndPoint?.ToString() ?? "unknown";

        // Read once per call rather than per use, so a configuration reload cannot change the answer
        // halfway through handling one INVITE.
        var did = DidNumber;

        if (!IsAddressedToUs(request, did))
        {
            _logger.LogWarning(
                "Rejecting INVITE for {RequestUri} from {RawCallerId} at {RemoteEndPoint} (User-Agent '{UserAgent}') - "
                + "not addressed to {Did}.",
                request.URI.ToString(),
                rawCallerId,
                remoteEndPoint,
                request.Header.UserAgent ?? "-",
                did!.Value);

            var rejection = new UASInviteTransaction(_sipTransport, request, null);
            rejection.SendFinalResponse(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.NotFound, null));
            return;
        }

        var (source, classification, callerNumber) = ClassifyCaller(rawCallerId);

        _logger.LogInformation(
            "Incoming INVITE from {RawCallerId} ({DisplayName}) at {RemoteEndPoint}: classified {Source}/{Classification}, Call-ID {SipCallId}, User-Agent '{UserAgent}'",
            rawCallerId,
            request.Header.From.FromName ?? "-",
            remoteEndPoint,
            source,
            classification,
            request.Header.CallId,
            request.Header.UserAgent ?? "-");

        Guid callId;
        try
        {
            callId = await calls.StartAsync(
                new StartCall(source, classification, callerNumber, rawCallerId, request.Header.CallId, DateTimeOffset.UtcNow),
                stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist incoming call; rejecting.");
            return;
        }

        // Guard so the remote-BYE, screening and local-hangup paths can't each record an ending.
        var ended = 0;
        async Task EndOnceAsync(CallCommand command, string description)
        {
            if (Interlocked.Exchange(ref ended, 1) == 1)
            {
                return;
            }

            _logger.LogInformation("Call {CallId} ended: {Description}", callId, description);
            try
            {
                await calls.ExecuteAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist end of call {CallId}", callId);
            }
        }

        Task EndByHangupAsync(HangupInitiator initiator, string reason) => EndOnceAsync(
            new EndCall(callId, DateTimeOffset.UtcNow, initiator, reason),
            $"{initiator} - {reason}");

        // Lets the screening gate abandon prompt playback the moment the caller drops.
        var hangup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        userAgent.OnCallHungup += dialogue =>
        {
            try
            {
                hangup.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _ = EndByHangupAsync(HangupInitiator.Remote, "remote hangup");
        };

        try
        {
            var serverUserAgent = userAgent.AcceptCall(request);
            var (mediaSession, audioSource) = CreateMediaSession();

            var answered = await userAgent.Answer(serverUserAgent, mediaSession, publicIpAddress: _publicAddress);
            if (!answered)
            {
                await EndByHangupAsync(HangupInitiator.Remote, "not answered (cancelled or answer failed)");
                return;
            }

            await calls.ExecuteAsync(new AnswerCall(callId, DateTimeOffset.UtcNow), stoppingToken);

            if (source == CallSource.Inbound)
            {
                await ScreenAsync(callId, userAgent, audioSource, EndOnceAsync, hangup.Token);
            }
            else
            {
                // Phase 3 replaces this with auto-answer + recording for the Outbound (my cell) path.
                _logger.LogInformation(
                    "Call {CallId} answered; holding {Seconds}s of silence (outbound path is still the phase 1 stub)",
                    callId, Phase1HoldDuration.TotalSeconds);
                try
                {
                    await Task.Delay(Phase1HoldDuration, hangup.Token);
                }
                catch (OperationCanceledException)
                {
                }

                await EndByHangupAsync(HangupInitiator.Local, "phase 1 auto-hangup");
            }

            // Stop the silence generator before tearing down, or its 20 ms timer fires once more against
            // the already-closed RTP session and logs "SendRtpRaw was called ... on a closed RTP session".
            await audioSource.CloseAudio();

            if (userAgent.IsCallActive)
            {
                userAgent.Hangup();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling call {CallId}", callId);
            await EndByHangupAsync(HangupInitiator.Local, $"error: {ex.Message}");
            if (userAgent.IsCallActive)
            {
                userAgent.Hangup();
            }
        }
        finally
        {
            hangup.Dispose();
        }
    }

    /// <summary>Runs the press-1 spam gate and records its outcome.</summary>
    private async Task ScreenAsync(
        Guid callId,
        SIPUserAgent userAgent,
        AudioExtrasSource audioSource,
        Func<CallCommand, string, Task> endOnceAsync,
        CancellationToken cancellationToken)
    {
        // Live: the screening digit and timeout are read when the gate runs, not when the process started.
        var telephony = telephonyOptions.CurrentValue;

        var gate = new ScreeningGate(
            userAgent,
            audioSource,
            prompts,
            telephony.ScreeningDigit,
            TimeSpan.FromSeconds(telephony.ScreeningTimeoutSeconds),
            _logger);

        var (outcome, digit) = await gate.RunAsync(callId, cancellationToken);

        var reason = outcome switch
        {
            ScreeningOutcome.Passed => $"screening passed (pressed {digit})",
            ScreeningOutcome.WrongDigit => $"screened out (pressed {digit}, expected {telephony.ScreeningDigit})",
            _ => $"screened out (no input within {telephony.ScreeningTimeoutSeconds}s)",
        };

        await endOnceAsync(new RecordScreeningOutcome(callId, outcome, DateTimeOffset.UtcNow, reason), reason);
    }

    /// <summary>
    /// Whether the INVITE is for our DID. Scanners sweep the request URI looking for a dial plan that will
    /// place an international call for them, so the URI user is the thing that distinguishes a real call
    /// from a probe — comparison is on the normalised number, since trunks vary on the +1 prefix.
    /// </summary>
    private static bool IsAddressedToUs(SIPRequest request, PhoneNumber? didNumber)
    {
        if (didNumber is null)
        {
            return true;
        }

        return PhoneNumber.TryParse(request.URI.User, out var dialled) && dialled == didNumber;
    }

    private (CallSource Source, SourceClassification Classification, PhoneNumber? CallerNumber) ClassifyCaller(string rawCallerId)
    {
        PhoneNumber.TryParse(rawCallerId, out var callerNumber);

        return callerNumber is not null && callerNumber == MyCellNumber
            ? (CallSource.Outbound, SourceClassification.CallerIdMatch, callerNumber)
            : (CallSource.Inbound, SourceClassification.Default, callerNumber);
    }

    /// <summary>
    /// Builds the media session for a call, returning the audio source alongside it so prompts can be
    /// streamed into the live call. Silence is the baseline; prompts interrupt it.
    /// </summary>
    private (NatAwareVoIPMediaSession Session, AudioExtrasSource AudioSource) CreateMediaSession()
    {
        var audioSource = new AudioExtrasSource(
            new AudioEncoder(),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });

        // Offer PCMU only. G.711 u-law is symmetric 2:1 with 16-bit PCM, which keeps the Phase 3 decode
        // and the Phase 4 payload relay trivial; left unrestricted, Telnyx negotiates G722. DTMF is
        // unaffected — MediaStream adds the RFC 4733 telephone-event payload separately from the codecs.
        audioSource.RestrictFormats(format => format.Codec == AudioCodecsEnum.PCMU);

        var mediaSession = new NatAwareVoIPMediaSession(
            new VoIPMediaSessionConfig
            {
                MediaEndPoint = new MediaEndPoints { AudioSource = audioSource },
                RtpPortRange = _rtpPortRange,
            },
            _publicAddress);
        mediaSession.AcceptRtpFromAny = true;
        return (mediaSession, audioSource);
    }

    /// <summary>Answers OPTIONS keepalives (Asterisk qualify, trunk health probes) so we stay "reachable".</summary>
    private async Task OnTransportRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        if (sipRequest.Method == SIPMethodsEnum.OPTIONS)
        {
            var okResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
            await _sipTransport!.SendResponseAsync(okResponse);
        }
    }
}
