using System.Net;
using CallTree.Application.Calls;
using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;
using CallTree.Telephony.Audio;
using CallTree.Telephony.Configuration;
using CallTree.Telephony.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace CallTree.Telephony;

/// <summary>
/// Hosts the SIP user agent for the lifetime of the process: registers with the trunk, rejects INVITEs
/// not addressed to our DID, and handles the two call paths — the inbound press-1 spam gate, and the
/// Outbound (my cell) path that answers automatically and records.
/// </summary>
/// <remarks>
/// Per-call handling is still inline here. Phase 4 lifts it into a CallSession plus an active-call
/// registry, which it needs anyway for a second concurrent call; doing it before there is a second leg
/// to model would be guessing at the shape.
/// </remarks>
public class TelephonyBackgroundService(
    IOptionsMonitor<TrunkOptions> trunkOptions,
    IOptionsMonitor<TelephonyOptions> telephonyOptions,
    TelephonySettingsWatcher settingsWatcher,
    TelephonyStatus status,
    PromptLibrary prompts,
    RecordingStore recordings,
    ICallCommands calls,
    ILoggerFactory loggerFactory) : BackgroundService
{
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

        var listeningEndpoints = _sipTransport.GetSIPChannels()
            .Select(c => c.ListeningSIPEndPoint.ToString())
            .ToList();

        _logger.LogInformation(
            "SIP listening on {Channels}; advertising {ContactHost} in Contact/SDP; RTP {RtpStart}-{RtpEnd}; SIP trace {TraceState}",
            string.Join(", ", listeningEndpoints),
            _sipTransport.ContactHost is { Length: > 0 } host ? host : "(local address - NAT will break inbound calls)",
            telephony.RtpPortStart,
            telephony.RtpPortEnd,
            telephony.TraceSip ? "on" : "off");

        status.Update(current => current with
        {
            RegistrationState = TrunkRegistrationState.Registering,
            StartedAt = DateTimeOffset.UtcNow,
            ListeningEndpoints = listeningEndpoints,
            ContactHost = _sipTransport.ContactHost is { Length: > 0 } contact ? contact : null,
            SdpAddress = _publicAddress?.ToString(),
            RtpPortRange = $"{telephony.RtpPortStart}-{telephony.RtpPortEnd}",
            ExpirySeconds = trunk.RegistrationExpirySeconds,
        });

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

        status.Update(current => current with { RegistrarServer = server });

        _registrationUserAgent.RegistrationSuccessful += (uri, response) =>
        {
            // The registrar echoes the binding it stored in the 200 OK Contact. That is the address it
            // will actually dial, and the quickest way to catch a NAT problem that leaves registration
            // looking perfectly healthy from this side.
            var contact = response?.Header?.Contact is { Count: > 0 } contacts
                ? string.Join(", ", contacts.Select(c => c.ToString()))
                : null;

            _logger.LogInformation("SIP registration successful for {Uri}; registrar stored {Contact}", uri, contact ?? "(no contact returned)");
            RecordRegistration(TrunkRegistrationState.Registered, uri, message: null, contact);
        };
        _registrationUserAgent.RegistrationRemoved += (uri, _) =>
        {
            _logger.LogWarning("SIP registration removed for {Uri}", uri);
            RecordRegistration(TrunkRegistrationState.Removed, uri, message: null, contact: null);
        };
        _registrationUserAgent.RegistrationTemporaryFailure += (uri, _, message) =>
        {
            _logger.LogWarning("SIP registration temporary failure for {Uri}: {Message}", uri, message);
            RecordRegistration(TrunkRegistrationState.TemporaryFailure, uri, message, contact: null);
        };
        _registrationUserAgent.RegistrationFailed += (uri, _, message) =>
        {
            _logger.LogError("SIP registration failed for {Uri}: {Message}", uri, message);
            RecordRegistration(TrunkRegistrationState.Failed, uri, message, contact: null);
        };

        _registrationUserAgent.Start();
    }

    private void RecordRegistration(TrunkRegistrationState state, SIPURI uri, string? message, string? contact)
    {
        var now = DateTimeOffset.UtcNow;

        status.Update(current => current with
        {
            RegistrationState = state,
            RegistrationMessage = message,
            RegisteredUri = uri?.ToString() ?? current.RegisteredUri,
            RegistrationChangedAt = now,
            // Keep the last known binding on a later failure: "it registered at 09:12 as this contact
            // and has been failing since" is the useful reading, not a blank field.
            RegistrarContact = contact ?? current.RegistrarContact,
            LastRegisteredAt = state == TrunkRegistrationState.Registered ? now : current.LastRegisteredAt,
            RegistrationCount = state == TrunkRegistrationState.Registered
                ? current.RegistrationCount + 1
                : current.RegistrationCount,
        });
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

            var player = new PromptPlayer(audioSource, prompts, () => userAgent.IsCallActive);

            // Inbound callers always face the spam gate. An Outbound-source caller only does when a PIN
            // is configured, and the aggregate has to be told which, because Screening is the state that
            // makes a refusal land in ScreenedOut rather than looking like a normal completed call.
            var requireScreening = source == CallSource.Inbound || telephonyOptions.CurrentValue.OutboundPin.Length > 0;

            await calls.ExecuteAsync(new AnswerCall(callId, DateTimeOffset.UtcNow, requireScreening), stoppingToken);

            if (source == CallSource.Inbound)
            {
                await ScreenAsync(callId, userAgent, player, EndOnceAsync, hangup.Token);
            }
            else
            {
                await RecordOutboundSourceAsync(callId, userAgent, mediaSession, player, EndOnceAsync, hangup.Token);
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

    /// <summary>
    /// The Outbound (my cell) path: optionally take a PIN, disclose that recording is starting, then
    /// record everything received until the caller hangs up.
    /// </summary>
    /// <remarks>
    /// Only received audio is captured, and that is the whole design: the operator adds the real party
    /// with the handset's own three-way merge, so by the time it matters this single leg already carries
    /// both voices mixed. It also means CallTree is never told the merge happened — which is why the
    /// spoken notice below reaches only the operator, and why a periodic tone is the sole disclosure this
    /// path can make to the party who joins later. See TelephonyOptions.RecordingToneIntervalSeconds.
    /// </remarks>
    private async Task RecordOutboundSourceAsync(
        Guid callId,
        SIPUserAgent userAgent,
        NatAwareVoIPMediaSession mediaSession,
        PromptPlayer player,
        Func<CallCommand, string, Task> endOnceAsync,
        CancellationToken cancellationToken)
    {
        var telephony = telephonyOptions.CurrentValue;

        if (telephony.OutboundPin.Length > 0 && !await PassesPinAsync(callId, userAgent, player, telephony, endOnceAsync, cancellationToken))
        {
            return;
        }

        // Before the recorder opens, so the disclosure cannot end up inside the file it is disclosing.
        await player.PlayAsync(PromptNames.RecordingNotice, interrupt: null, cancellationToken);

        var startedAt = DateTimeOffset.UtcNow;
        var location = recordings.Locate(callId, startedAt);

        // Persisted first: StartRecording is only legal while the call is InProgress, so letting the
        // aggregate refuse before a file exists avoids leaving an orphan WAV behind when the caller hung
        // up in the last few milliseconds. The cost is the handful of packets that arrive meanwhile.
        try
        {
            await calls.ExecuteAsync(new StartRecording(callId, startedAt, location.RelativePath, ChannelLayout.Mono));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Call {CallId}: could not register a recording; continuing without one.", callId);
            await WaitForHangupAsync(player, telephony.RecordingToneIntervalSeconds, cancellationToken);
            return;
        }

        CallRecorder recorder;
        try
        {
            recorder = new CallRecorder(
                callId,
                location.FullPath,
                TimeSpan.FromMilliseconds(telephony.JitterBufferMilliseconds),
                _logger);
        }
        catch (Exception ex)
        {
            // The row exists with no file and no FinalizedAt, which is exactly what the Phase 6 repair
            // sweep looks for. Loud, because a recording call that records nothing is the worst outcome.
            _logger.LogError(ex, "Call {CallId}: could not open {Path} for recording.", callId, location.FullPath);
            await WaitForHangupAsync(player, telephony.RecordingToneIntervalSeconds, cancellationToken);
            return;
        }

        void OnRtpPacket(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            if (mediaType != SDPMediaTypesEnum.audio)
            {
                return;
            }

            recorder.Accept(
                packet.Header.PayloadType,
                packet.Header.Timestamp,
                packet.Header.SequenceNumber,
                packet.Payload);
        }

        mediaSession.OnRtpPacketReceived += OnRtpPacket;
        _logger.LogInformation("Call {CallId}: recording to {Path}", callId, location.RelativePath);

        try
        {
            await WaitForHangupAsync(player, telephony.RecordingToneIntervalSeconds, cancellationToken);
        }
        finally
        {
            mediaSession.OnRtpPacketReceived -= OnRtpPacket;

            var outcome = recorder.Close();
            _logger.LogInformation(
                "Call {CallId}: recorded {Duration:0.0}s ({Size:N0} bytes) to {Path}; {Silence:0.0}s filled for lost "
                + "packets, {Late} late, {Discontinuities} discontinuities",
                callId,
                outcome.DurationSeconds,
                outcome.SizeBytes,
                location.RelativePath,
                outcome.SilenceSeconds,
                outcome.LateFrames,
                outcome.Discontinuities);

            try
            {
                // Touches only the Recording row - the aggregate's own columns are unchanged, so EF emits
                // no UPDATE for the Call and this cannot race the hangup handler's EndCall into
                // overwriting the terminal status with a stale one.
                await calls.ExecuteAsync(
                    new FinalizeRecording(callId, DateTimeOffset.UtcNow, outcome.DurationSeconds, outcome.SizeBytes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call {CallId}: recording finished but could not be finalized in the database.", callId);
            }
        }
    }

    /// <summary>Runs the PIN gate, ending the call itself when the caller fails it.</summary>
    private async Task<bool> PassesPinAsync(
        Guid callId,
        SIPUserAgent userAgent,
        PromptPlayer player,
        TelephonyOptions telephony,
        Func<CallCommand, string, Task> endOnceAsync,
        CancellationToken cancellationToken)
    {
        var gate = new PinGate(
            userAgent,
            player,
            telephony.OutboundPin,
            TimeSpan.FromSeconds(telephony.ScreeningTimeoutSeconds),
            _logger);

        var outcome = await gate.RunAsync(callId, cancellationToken);
        if (outcome == PinOutcome.Accepted)
        {
            await calls.ExecuteAsync(new PassScreening(callId, DateTimeOffset.UtcNow));
            return true;
        }

        var (screening, reason) = outcome == PinOutcome.TimedOut
            ? (ScreeningOutcome.TimedOut, $"no PIN within {telephony.ScreeningTimeoutSeconds}s")
            : (ScreeningOutcome.WrongDigit, "wrong PIN");

        await endOnceAsync(new RecordScreeningOutcome(callId, screening, DateTimeOffset.UtcNow, reason), reason);
        return false;
    }

    /// <summary>
    /// Blocks until the caller hangs up (or the host stops), sounding the recording tone on its interval
    /// if one is configured. The tone is sent, not received, so it never appears in the recording.
    /// </summary>
    private static async Task WaitForHangupAsync(PromptPlayer player, int toneIntervalSeconds, CancellationToken cancellationToken)
    {
        try
        {
            if (toneIntervalSeconds <= 0)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return;
            }

            var interval = TimeSpan.FromSeconds(toneIntervalSeconds);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                await player.PlayAsync(PromptNames.RecordingTone, interrupt: null, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The caller hung up, or the host is shutting down. Both are the normal way out.
        }
    }

    /// <summary>Runs the press-1 spam gate and records its outcome.</summary>
    private async Task ScreenAsync(
        Guid callId,
        SIPUserAgent userAgent,
        PromptPlayer player,
        Func<CallCommand, string, Task> endOnceAsync,
        CancellationToken cancellationToken)
    {
        // Live: the screening digit and timeout are read when the gate runs, not when the process started.
        var telephony = telephonyOptions.CurrentValue;

        var gate = new ScreeningGate(
            userAgent,
            player,
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
