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

    /// <summary>Registrar host and port in the form a SIP URI or REGISTER destination expects.</summary>
    private static string TrunkServer(TrunkOptions trunk) =>
        trunk.Port == 5060 ? trunk.Host : $"{trunk.Host}:{trunk.Port}";

    private void StartRegistration(TrunkOptions trunk)
    {
        var server = TrunkServer(trunk);

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
                var (screeningOutcome, screeningReason) = await ScreenAsync(callId, userAgent, player, hangup.Token);

                if (screeningOutcome == ScreeningOutcome.Passed)
                {
                    await BridgeToMobileAsync(callId, userAgent, mediaSession, player, EndOnceAsync, hangup);
                }
                else
                {
                    await EndOnceAsync(
                        new RecordScreeningOutcome(callId, screeningOutcome, DateTimeOffset.UtcNow, screeningReason),
                        screeningReason);
                }
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
        await player.PlayAsync(PromptNames.RecordingReminder, interrupt: null, cancellationToken);

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
            // Runs for the call's whole duration, independent of any proxy dial - the tone is an ongoing
            // consent mechanism for the operator's own leg, not something a proxy segment should pause.
            var toneTask = WaitForHangupAsync(player, telephony.RecordingToneIntervalSeconds, cancellationToken);
            var proxyDialCollector = new ProxyDialCollector(userAgent, _logger);

            while (true)
            {
                // A fresh dial-wait each iteration; toneTask is not recreated, or two overlapping
                // WaitForHangupAsync loops would fight over the same shared audio source.
                var dialTask = proxyDialCollector.WaitForDialSequenceAsync(cancellationToken);

                if (await Task.WhenAny(toneTask, dialTask) == toneTask)
                {
                    break;
                }

                PhoneNumber target;
                try
                {
                    target = await dialTask;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Deliberately not intercepted while this runs: DTMF during an active proxy segment
                // belongs to the proxy-dialed party (e.g. navigating their own phone menu), not to a new
                // *{NUMBER}# attempt.
                await RunProxyDialAsync(callId, userAgent, mediaSession, player, recorder, target, cancellationToken);
            }

            await toneTask;
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

    /// <summary>
    /// One <c>*{NUMBER}#</c> attempt on the Outbound-source path: places the outbound leg from the DID,
    /// and if it answers, discloses the recording to the third party, relays RTP both directions, mixes
    /// their audio into the already-open <paramref name="recorder"/>, and waits until either the proxy
    /// party hangs up (ends only this segment) or the operator does (the caller's own cancellation ends
    /// the whole call, observed here the same way).
    /// </summary>
    private async Task RunProxyDialAsync(
        Guid callId,
        SIPUserAgent primaryAgent,
        NatAwareVoIPMediaSession primaryMedia,
        PromptPlayer primaryPlayer,
        CallRecorder recorder,
        PhoneNumber target,
        CancellationToken cancellationToken)
    {
        var did = DidNumber;
        if (did is null)
        {
            _logger.LogError(
                "Call {CallId}: dialed a proxy call to {Target} but Telephony:DidNumber is not set or is not "
                + "a valid number - ignoring.",
                callId,
                target);
            return;
        }

        var result = await PlaceOutboundLegAsync(callId, target, did, primaryPlayer, cancellationToken);
        if (!result.Answered)
        {
            _logger.LogInformation("Call {CallId}: proxy dial to {Destination} did not answer.", callId, result.Destination);
            return;
        }

        var proxyAgent = result.Agent;
        var proxyMedia = result.Media;
        var proxyAudio = result.Audio;

        try
        {
            // Disclosed to the third party before mixing starts, so the notice itself never lands inside
            // the recording it discloses - the same rule the operator's own reminder already follows.
            var proxyPlayer = new PromptPlayer(proxyAudio, prompts, () => proxyAgent.IsCallActive);
            await proxyPlayer.PlayAsync(PromptNames.RecordingNotice, interrupt: null, cancellationToken);

            recorder.AttachSecondaryLeg();

            // Paced, not just reordered - see PacedRtpRelay's remarks for why sending a packet the instant
            // it arrives is still choppy (with gradually increasing lag) even after reordering alone.
            var jitterDepth = TimeSpan.FromMilliseconds(telephonyOptions.CurrentValue.JitterBufferMilliseconds);
            var toProxyRelay = new PacedRtpRelay(callId, "primary->proxy", jitterDepth, proxyMedia, _logger);
            var toPrimaryRelay = new PacedRtpRelay(callId, "proxy->primary", jitterDepth, primaryMedia, _logger);

            void RelayToProxy(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
            {
                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    toProxyRelay.Offer(packet.Header.PayloadType, packet.Header.Timestamp, packet.Header.SequenceNumber, packet.Payload);
                }
            }

            void RelayToPrimary(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
            {
                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    toPrimaryRelay.Offer(packet.Header.PayloadType, packet.Header.Timestamp, packet.Header.SequenceNumber, packet.Payload);
                }
            }

            void MixProxyAudio(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
            {
                if (mediaType == SDPMediaTypesEnum.audio)
                {
                    recorder.AcceptSecondary(
                        packet.Header.PayloadType, packet.Header.Timestamp, packet.Header.SequenceNumber, packet.Payload);
                }
            }

            primaryMedia.OnRtpPacketReceived += RelayToProxy;
            proxyMedia.OnRtpPacketReceived += RelayToPrimary;
            proxyMedia.OnRtpPacketReceived += MixProxyAudio;

            _logger.LogInformation("Call {CallId}: proxy dial connected to {Destination}", callId, result.Destination);

            try
            {
                // A proxy-party hangup ends only this segment - the operator's own call keeps going. Its
                // own completion source rather than the shared-CTS trick BridgeToMobileAsync uses for the
                // mobile leg, since that trick's whole point is to end the *entire* call - the opposite of
                // what belongs here.
                var proxyHungUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                proxyAgent.OnCallHungup += dialogue => proxyHungUp.TrySetResult();

                await Task.WhenAny(proxyHungUp.Task, PromptPlayer.WhenCancelled(cancellationToken));
            }
            finally
            {
                primaryMedia.OnRtpPacketReceived -= RelayToProxy;
                proxyMedia.OnRtpPacketReceived -= RelayToPrimary;
                proxyMedia.OnRtpPacketReceived -= MixProxyAudio;
                // Awaited so the pump loops are fully stopped before the media sessions get closed below.
                await toProxyRelay.DisposeAsync();
                await toPrimaryRelay.DisposeAsync();
                recorder.DetachSecondaryLeg();
            }
        }
        finally
        {
            await proxyAudio.CloseAudio();
            if (proxyAgent.IsCallActive)
            {
                proxyAgent.Hangup();
            }
        }
    }

    /// <summary>What placing one outbound leg through the trunk turned out to be.</summary>
    /// <param name="Answered">
    /// Whether the leg is now live. When <see langword="false"/>, <see cref="PlaceOutboundLegAsync"/> has
    /// already closed <paramref name="Audio"/> and hung up <paramref name="Agent"/> - there is nothing
    /// left for the caller to do. When <see langword="true"/>, the caller owns all three and must
    /// eventually tear them down itself.
    /// </param>
    private readonly record struct OutboundLegResult(
        bool Answered, SIPUserAgent Agent, NatAwareVoIPMediaSession Media, AudioExtrasSource Audio, string Destination);

    /// <summary>
    /// Places one outbound leg through the trunk, from <paramref name="callerId"/> to
    /// <paramref name="target"/>, playing <see cref="PromptNames.Ringing"/> to
    /// <paramref name="ringbackPlayer"/> for as long as the attempt is in flight. Deliberately agnostic
    /// about *why* - the Inbound bridge (dialing the configured mobile) and the Outbound-source proxy dial
    /// (dialing whatever number the operator entered) both call this, and a future Web-softphone trigger
    /// should be able to as well without this method changing.
    /// </summary>
    private async Task<OutboundLegResult> PlaceOutboundLegAsync(
        Guid callId,
        PhoneNumber target,
        PhoneNumber callerId,
        PromptPlayer ringbackPlayer,
        CancellationToken cancellationToken)
    {
        var trunk = trunkOptions.CurrentValue;
        var telephony = telephonyOptions.CurrentValue;

        var agent = new SIPUserAgent(_sipTransport, null);
        var (media, audio) = CreateMediaSession();

        var server = TrunkServer(trunk);
        var dst = $"sip:{target.Value.TrimStart('+')}@{server}";
        var from = $"<sip:{callerId.Value.TrimStart('+')}@{server}>";
        _logger.LogInformation("Call {CallId}: dialing {Destination} from {From}", callId, dst, from);

        // The simple SIPUserAgent.Call(dst, username, password, ...) overload builds its own
        // SIPCallDescriptor with no From set, which left the trunk to infer a caller ID from the SIP
        // username - not a phone number, and exactly what Telnyx rejected with
        // "403 Caller Origination Number is Invalid". Building the descriptor ourselves is the only way
        // to pin From to whichever number we are dialing out as.
        var callDescriptor = new SIPCallDescriptor(
            trunk.Username,
            trunk.Password,
            dst,
            from,
            to: null,
            routeSet: null,
            customHeaders: null,
            authUsername: null,
            callDirection: SIPCallDirection.Out,
            contentType: null,
            content: null,
            mangleIPAddress: null);

        // Ringing plays for as long as the attempt is in flight - otherwise whoever is waiting sits in
        // dead silence. Its own token is linked (not cancellationToken itself) so it can be stopped the
        // instant the dial resolves, rather than only on cancellation, without racing whatever the caller
        // plays next onto the same audio source.
        using var ringingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ringingTask = PlayRingingAsync(ringbackPlayer, ringingCts.Token);

        bool answered;
        try
        {
            var callTask = agent.Call(callDescriptor, media, telephony.DialTimeoutSeconds);
            var abandoned = PromptPlayer.WhenCancelled(cancellationToken);

            answered = await Task.WhenAny(callTask, abandoned) != abandoned && await callTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Call {CallId}: placing a call to {Destination} failed.", callId, dst);
            answered = false;
        }
        finally
        {
            // Cancel-and-await rather than fire-and-forget: PlayAsync must actually stop touching the
            // shared audio source before the caller is allowed to use it for anything else.
            ringingCts.Cancel();
            await ringingTask;
        }

        if (!answered)
        {
            await audio.CloseAudio();
            if (agent.IsCallActive)
            {
                agent.Hangup();
            }
        }

        return new OutboundLegResult(answered, agent, media, audio, dst);
    }

    /// <summary>
    /// The Inbound bridge: place a second SIP leg to the configured mobile, and if it answers, relay RTP
    /// both directions and record both legs until either side hangs up. If it never answers, the caller
    /// hears <see cref="PromptNames.Apology"/> and the call lands in <see cref="CallStatus.Missed"/>.
    /// </summary>
    private async Task BridgeToMobileAsync(
        Guid callId,
        SIPUserAgent inboundAgent,
        NatAwareVoIPMediaSession inboundMedia,
        PromptPlayer inboundPlayer,
        Func<CallCommand, string, Task> endOnceAsync,
        CancellationTokenSource hangup)
    {
        var cancellationToken = hangup.Token;
        var target = MyCellNumber;

        if (target is null)
        {
            _logger.LogError(
                "Call {CallId}: Telephony:MyCellNumber is not set or is not a valid number - nothing to bridge to.",
                callId);
            await endOnceAsync(
                new EndCall(callId, DateTimeOffset.UtcNow, HangupInitiator.Local, "no mobile number configured"),
                "no mobile number configured");
            return;
        }

        // The outbound leg's From has to be a number the trunk actually recognises as ours - our own
        // username isn't a phone number, and using it (or nothing) is what Telnyx rejects with
        // "403 Caller Origination Number is Invalid". The DID is the one number the account is guaranteed
        // to accept as our caller ID.
        var did = DidNumber;
        if (did is null)
        {
            _logger.LogError(
                "Call {CallId}: Telephony:DidNumber is not set or is not a valid number - the trunk needs it "
                + "as the outbound leg's caller ID.",
                callId);
            await endOnceAsync(
                new EndCall(callId, DateTimeOffset.UtcNow, HangupInitiator.Local, "no DID configured to dial out as"),
                "no DID configured to dial out as");
            return;
        }

        // A locally-generated correlation id. SIPSorcery mints the real wire Call-ID internally when
        // Call() sends the INVITE and does not surface it beforehand - confirm against Telephony:TraceSip
        // output during phone validation if the two need to line up exactly.
        var outboundSipCallId = $"bridge-{Guid.NewGuid():N}";
        await calls.ExecuteAsync(
            new BeginDialing(callId, DateTimeOffset.UtcNow, target, outboundSipCallId), cancellationToken);

        var result = await PlaceOutboundLegAsync(callId, target, did, inboundPlayer, cancellationToken);

        if (!result.Answered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The caller hung up while the mobile was still ringing. The inbound leg's own
                // OnCallHungup handler has already ended the call, and PlaceOutboundLegAsync has already
                // cleaned up - nothing more to do.
                return;
            }

            _logger.LogInformation(
                "Call {CallId}: {Destination} did not answer within {Timeout}s",
                callId, result.Destination, telephonyOptions.CurrentValue.DialTimeoutSeconds);
            await inboundPlayer.PlayAsync(PromptNames.Apology, interrupt: null, cancellationToken);
            await endOnceAsync(
                new EndCall(callId, DateTimeOffset.UtcNow, HangupInitiator.Remote, "mobile did not answer"),
                "mobile did not answer");
            return;
        }

        var outboundAgent = result.Agent;
        var outboundMedia = result.Media;
        var outboundAudio = result.Audio;

        try
        {
            // Subscribed after answer rather than before dialing (as the pre-refactor version did):
            // OnCallHungup fires for an established dialogue being torn down, which cannot happen before
            // PlaceOutboundLegAsync has already reported Answered. The gap between that result and this
            // subscription is synchronous - no await in between - so the exposure is negligible.
            outboundAgent.OnCallHungup += dialogue =>
            {
                // Cancel the shared token directly rather than relying on inboundAgent.Hangup() below to
                // raise its own OnCallHungup - that would leave RunBridgeAsync's wait unblocked only if
                // SIPSorcery re-raises the event for a locally-initiated hangup, which is not guaranteed.
                try
                {
                    hangup.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _ = endOnceAsync(
                    new EndCall(callId, DateTimeOffset.UtcNow, HangupInitiator.Remote, "mobile hung up"),
                    "mobile hung up");
                if (inboundAgent.IsCallActive)
                {
                    inboundAgent.Hangup();
                }
            };

            await calls.ExecuteAsync(new BridgeCall(callId, DateTimeOffset.UtcNow), cancellationToken);
            _logger.LogInformation("Call {CallId}: bridged to {Destination}", callId, result.Destination);

            await RunBridgeAsync(callId, inboundMedia, outboundMedia, cancellationToken);
        }
        finally
        {
            await outboundAudio.CloseAudio();
            if (outboundAgent.IsCallActive)
            {
                outboundAgent.Hangup();
            }
        }
    }

    /// <summary>
    /// Both legs are up: relay PCMU each direction (no transcode - both legs are PCMU) and record the
    /// bridge to one stereo WAV until either side hangs up.
    /// </summary>
    private async Task RunBridgeAsync(
        Guid callId,
        NatAwareVoIPMediaSession inboundMedia,
        NatAwareVoIPMediaSession outboundMedia,
        CancellationToken cancellationToken)
    {
        var telephony = telephonyOptions.CurrentValue;
        var jitterDepth = TimeSpan.FromMilliseconds(telephony.JitterBufferMilliseconds);

        // Paced, not just reordered: see PacedRtpRelay's remarks for why sending a packet the instant it
        // arrives was still choppy (with gradually increasing lag) even after reordering fixed correctness.
        var toOutboundRelay = new PacedRtpRelay(callId, "caller->mobile", jitterDepth, outboundMedia, _logger);
        var toInboundRelay = new PacedRtpRelay(callId, "mobile->caller", jitterDepth, inboundMedia, _logger);

        // Non-PCMU payloads (notably 101, the RFC 4733 telephone-event stream) are dropped at the door -
        // relaying DTMF is out of scope, and decoding it as audio would inject noise into either leg.
        void RelayToOutbound(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                toOutboundRelay.Offer(packet.Header.PayloadType, packet.Header.Timestamp, packet.Header.SequenceNumber, packet.Payload);
            }
        }

        void RelayToInbound(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                toInboundRelay.Offer(packet.Header.PayloadType, packet.Header.Timestamp, packet.Header.SequenceNumber, packet.Payload);
            }
        }

        inboundMedia.OnRtpPacketReceived += RelayToOutbound;
        outboundMedia.OnRtpPacketReceived += RelayToInbound;

        var startedAt = DateTimeOffset.UtcNow;
        var location = recordings.Locate(callId, startedAt);

        BridgedCallRecorder? recorder = null;
        try
        {
            await calls.ExecuteAsync(new StartRecording(callId, startedAt, location.RelativePath, ChannelLayout.StereoPerLeg));
            recorder = new BridgedCallRecorder(
                callId, location.FullPath, TimeSpan.FromMilliseconds(telephony.JitterBufferMilliseconds), _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Call {CallId}: could not start the bridged recording; the call continues without one.", callId);
        }

        void RecordCaller(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                recorder?.Accept(
                    RecordingChannel.Caller, packet.Header.PayloadType, packet.Header.Timestamp, packet.Header.SequenceNumber, packet.Payload);
            }
        }

        void RecordMobile(IPEndPoint remote, SDPMediaTypesEnum mediaType, RTPPacket packet)
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                recorder?.Accept(
                    RecordingChannel.Mobile, packet.Header.PayloadType, packet.Header.Timestamp, packet.Header.SequenceNumber, packet.Payload);
            }
        }

        if (recorder is not null)
        {
            inboundMedia.OnRtpPacketReceived += RecordCaller;
            outboundMedia.OnRtpPacketReceived += RecordMobile;
            _logger.LogInformation("Call {CallId}: recording bridge to {Path}", callId, location.RelativePath);
        }

        try
        {
            await PromptPlayer.WhenCancelled(cancellationToken);
        }
        finally
        {
            inboundMedia.OnRtpPacketReceived -= RelayToOutbound;
            outboundMedia.OnRtpPacketReceived -= RelayToInbound;
            // Awaited so the pump loops are fully stopped before the media sessions get closed below.
            await toOutboundRelay.DisposeAsync();
            await toInboundRelay.DisposeAsync();

            if (recorder is not null)
            {
                inboundMedia.OnRtpPacketReceived -= RecordCaller;
                outboundMedia.OnRtpPacketReceived -= RecordMobile;

                var outcome = recorder.Close();
                _logger.LogInformation(
                    "Call {CallId}: bridge recorded {Duration:0.0}s ({Size:N0} bytes) to {Path}; {Silence:0.0}s filled "
                    + "for lost packets, {Late} late, {Discontinuities} discontinuities",
                    callId,
                    outcome.DurationSeconds,
                    outcome.SizeBytes,
                    location.RelativePath,
                    outcome.SilenceSeconds,
                    outcome.LateFrames,
                    outcome.Discontinuities);

                try
                {
                    await calls.ExecuteAsync(
                        new FinalizeRecording(callId, DateTimeOffset.UtcNow, outcome.DurationSeconds, outcome.SizeBytes));
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex, "Call {CallId}: bridge recording finished but could not be finalized in the database.", callId);
                }
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
    /// Loops <see cref="PromptNames.Ringing"/> to the caller for as long as the bridge's outbound leg is
    /// ringing, roughly matching the North American ringback cadence (the prompt itself is the ~2s "on"
    /// portion; the gap here is the "off" portion). Exits the moment the token is cancelled - the dial
    /// resolved one way or another - rather than running the file out first.
    /// </summary>
    private static async Task PlayRingingAsync(PromptPlayer player, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await player.PlayAsync(PromptNames.Ringing, interrupt: null, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The dial attempt resolved - answered, failed, or abandoned. Normal exit.
        }
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

    /// <summary>
    /// Runs the press-1 spam gate. A pass is handed to <see cref="BridgeToMobileAsync"/> by the caller
    /// rather than ended here - there is a bridge to place, not a call to finish.
    /// </summary>
    private async Task<(ScreeningOutcome Outcome, string Reason)> ScreenAsync(
        Guid callId,
        SIPUserAgent userAgent,
        PromptPlayer player,
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

        return (outcome, reason);
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
