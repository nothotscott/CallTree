using System.Net;
using CallTree.Application.Calls;
using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;
using CallTree.Telephony.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    IOptions<TrunkOptions> trunkOptions,
    IOptions<TelephonyOptions> telephonyOptions,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly TimeSpan Phase1HoldDuration = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = loggerFactory.CreateLogger<TelephonyBackgroundService>();
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _registrationUserAgent;
    private PhoneNumber? _myCellNumber;
    private IPAddress? _publicAddress;
    private PortRange? _rtpPortRange;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var trunk = trunkOptions.Value;
        var telephony = telephonyOptions.Value;

        if (!trunk.IsConfigured)
        {
            _logger.LogWarning("Trunk is not configured (Trunk:Host / Trunk:Username missing) — telephony is idle.");
            return;
        }

        if (PhoneNumber.TryParse(telephony.MyCellNumber, out var myCell))
        {
            _myCellNumber = myCell;
        }
        else
        {
            _logger.LogWarning("Telephony:MyCellNumber is not set — all calls will be classified as Inbound.");
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

        if (telephony.TraceSip)
        {
            EnableSipTracing();
        }

        _logger.LogInformation(
            "SIP listening on {Channels}; advertising {ContactHost} in Contact/SDP; RTP {RtpStart}-{RtpEnd}; SIP trace {TraceState}",
            string.Join(", ", _sipTransport.GetSIPChannels().Select(c => c.ListeningSIPEndPoint.ToString())),
            _sipTransport.ContactHost is { Length: > 0 } host ? host : "(local address — NAT will break inbound calls)",
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

    /// <summary>Logs whole SIP messages on the wire — the only reliable way to see NAT/routing problems.</summary>
    private void EnableSipTracing()
    {
        var trace = loggerFactory.CreateLogger("CallTree.Telephony.SipTrace");

        _sipTransport!.SIPRequestOutTraceEvent += (local, remote, request) =>
            trace.LogTrace("SIP TX {Local} -> {Remote}\n{Message}", local, remote, request.ToString());
        _sipTransport.SIPRequestInTraceEvent += (local, remote, request) =>
            trace.LogTrace("SIP RX {Remote} -> {Local}\n{Message}", remote, local, request.ToString());
        _sipTransport.SIPResponseOutTraceEvent += (local, remote, response) =>
            trace.LogTrace("SIP TX {Local} -> {Remote}\n{Message}", local, remote, response.ToString());
        _sipTransport.SIPResponseInTraceEvent += (local, remote, response) =>
            trace.LogTrace("SIP RX {Remote} -> {Local}\n{Message}", remote, local, response.ToString());
        _sipTransport.SIPBadRequestInTraceEvent += (local, remote, message, field, raw) =>
            trace.LogWarning("SIP RX (bad request) {Remote} -> {Local}: {Message} [{Field}]\n{Raw}", remote, local, message, field, raw);
        _sipTransport.SIPBadResponseInTraceEvent += (local, remote, message, field, raw) =>
            trace.LogWarning("SIP RX (bad response) {Remote} -> {Local}: {Message} [{Field}]\n{Raw}", remote, local, message, field, raw);
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
            callId = await WithLifecycleAsync(lifecycle => lifecycle.StartAsync(
                source, classification, callerNumber, rawCallerId, request.Header.CallId, DateTimeOffset.UtcNow, stoppingToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist incoming call; rejecting.");
            return;
        }

        // Guard so remote-BYE and local-hangup paths can't both record an ending.
        var ended = 0;
        async Task RecordEndOnceAsync(HangupInitiator initiator, string reason)
        {
            if (Interlocked.Exchange(ref ended, 1) == 1)
            {
                return;
            }

            _logger.LogInformation("Call {CallId} ended ({Initiator}): {Reason}", callId, initiator, reason);
            try
            {
                await WithLifecycleAsync(async lifecycle =>
                {
                    await lifecycle.EndAsync(callId, DateTimeOffset.UtcNow, initiator, reason);
                    return 0;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist end of call {CallId}", callId);
            }
        }

        userAgent.OnCallHungup += dialogue => _ = RecordEndOnceAsync(HangupInitiator.Remote, "remote hangup");
        userAgent.OnDtmfTone += (tone, duration) =>
            _logger.LogInformation("Call {CallId}: DTMF tone {Tone} ({Duration}ms)", callId, tone, duration);

        try
        {
            var serverUserAgent = userAgent.AcceptCall(request);
            var mediaSession = CreateSilenceMediaSession();

            var answered = await userAgent.Answer(serverUserAgent, mediaSession, publicIpAddress: _publicAddress);
            if (!answered)
            {
                await RecordEndOnceAsync(HangupInitiator.Remote, "not answered (cancelled or answer failed)");
                return;
            }

            _logger.LogInformation("Call {CallId} answered; holding {Seconds}s of silence (phase 1)", callId, Phase1HoldDuration.TotalSeconds);
            await WithLifecycleAsync(async lifecycle =>
            {
                await lifecycle.AnswerAsync(callId, DateTimeOffset.UtcNow);
                return 0;
            });

            try
            {
                await Task.Delay(Phase1HoldDuration, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }

            if (userAgent.IsCallActive)
            {
                await RecordEndOnceAsync(HangupInitiator.Local, "phase 1 auto-hangup");
                userAgent.Hangup();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling call {CallId}", callId);
            await RecordEndOnceAsync(HangupInitiator.Local, $"error: {ex.Message}");
            if (userAgent.IsCallActive)
            {
                userAgent.Hangup();
            }
        }
    }

    private (CallSource Source, SourceClassification Classification, PhoneNumber? CallerNumber) ClassifyCaller(string rawCallerId)
    {
        PhoneNumber.TryParse(rawCallerId, out var callerNumber);

        return callerNumber is not null && callerNumber == _myCellNumber
            ? (CallSource.Outbound, SourceClassification.CallerIdMatch, callerNumber)
            : (CallSource.Inbound, SourceClassification.Default, callerNumber);
    }

    private VoIPMediaSession CreateSilenceMediaSession()
    {
        var audioSource = new AudioExtrasSource(
            new AudioEncoder(),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });
        var mediaSession = new NatAwareVoIPMediaSession(
            new VoIPMediaSessionConfig
            {
                MediaEndPoint = new MediaEndPoints { AudioSource = audioSource },
                RtpPortRange = _rtpPortRange,
            },
            _publicAddress);
        mediaSession.AcceptRtpFromAny = true;
        return mediaSession;
    }

    /// <summary>Runs an operation against a scoped <see cref="CallLifecycleService"/> (one DI scope per telephony event).</summary>
    private async Task<T> WithLifecycleAsync<T>(Func<CallLifecycleService, Task<T>> operation)
    {
        using var scope = scopeFactory.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<CallLifecycleService>());
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
