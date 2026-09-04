namespace CallTree.SipHarness;

/// <summary>What the harness should pretend to be, and to whom.</summary>
internal enum Scenario
{
    /// <summary>
    /// A stranger calls the DID: press the screening digit, get bridged to the mobile the harness is
    /// also answering as, and exchange tones both ways. The scenario that exercises the most code.
    /// </summary>
    Inbound,

    /// <summary>
    /// The operator's own mobile calls the DID: auto-answered, PIN if one is configured, recorded. No
    /// second leg unless <see cref="HarnessOptions.ProxyNumber"/> asks for one.
    /// </summary>
    Outbound,

    /// <summary>A stranger presses the wrong digit and is hung up on. Should never reach the mobile.</summary>
    Screened,

    /// <summary>A stranger passes screening but the mobile never answers. Should land in Missed.</summary>
    Missed,
}

/// <summary>
/// Everything the harness was told on the command line, already validated.
/// </summary>
/// <remarks>
/// Hand-parsed rather than taken from a CLI package: the harness is a diagnostic tool that has to build
/// and run when the rest of the repo is in pieces, and one more dependency is one more thing that can be
/// the reason it does not.
/// </remarks>
internal sealed record HarnessOptions
{
    /// <summary>Host CallTree's SIP stack is listening on.</summary>
    public required string Host { get; init; }

    /// <summary>CallTree's Telephony:SipListenPort. 5061 under the Development profile.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// CallTree's Telephony:DidNumber. The request URI has to match it or the INVITE is refused with a
    /// 404 before a Call row exists - the harness is subject to the same DID filter that keeps dial-plan
    /// probes out, on purpose.
    /// </summary>
    public required string Did { get; init; }

    /// <summary>
    /// CallTree's Telephony:MyCellNumber. Used as the caller ID for <see cref="Scenario.Outbound"/>,
    /// which is the entire mechanism by which CallTree decides a call is from the operator - hence
    /// "spoofing".
    /// </summary>
    public required string Cell { get; init; }

    /// <summary>Caller ID for the scenarios that are meant to look like a stranger.</summary>
    public required string Stranger { get; init; }

    /// <summary>
    /// SIP port the harness itself listens on. Must match CallTree's Spoof:LoopbackHost, because that is
    /// where its outbound legs are dialled.
    /// </summary>
    public required int ListenPort { get; init; }

    public required int RtpPortStart { get; init; }

    public required int RtpPortEnd { get; init; }

    public required Scenario Scenario { get; init; }

    /// <summary>How many callers to run at once. The whole point of the tool above 1.</summary>
    public required int Calls { get; init; }

    /// <summary>Seconds to hold each call once media is flowing, before hanging up.</summary>
    public required int DurationSeconds { get; init; }

    /// <summary>DTMF to send after answer - the screening digit, or a PIN, or both concatenated.</summary>
    public required string Digits { get; init; }

    /// <summary>
    /// How long to wait after the 200 OK before keying anything in.
    /// </summary>
    /// <remarks>
    /// Not politeness - a race. The answer goes out before the gate that listens for DTMF is attached
    /// (CallTree persists the answer first, which is a database round trip), so a digit sent the
    /// microsecond the call connects can arrive at a call that is not yet listening and be lost with no
    /// trace on either side. A real caller is reacting to a prompt they are hearing and cannot come
    /// close to that window; the harness can, and does, unless told to wait.
    /// </remarks>
    public required double DtmfDelaySeconds { get; init; }

    /// <summary>When set, an Outbound-source call dials *{number}# partway through.</summary>
    public string? ProxyNumber { get; init; }

    /// <summary>
    /// How long into the call to key in the proxy dial.
    /// </summary>
    /// <remarks>
    /// Has to clear the recording reminder, which is about six seconds long and plays <em>before</em>
    /// RecordOutboundSourceAsync opens the recorder and constructs the ProxyDialCollector. Digits sent
    /// while that prompt is still playing reach a call with nothing listening for them and vanish - and
    /// because the collector needs the leading * to start a sequence, losing just that one digit
    /// silently discards the whole dial. Found by this harness at a four-second delay, where it worked
    /// or did not depending on how the two timers happened to line up.
    /// </remarks>
    public required double ProxyDelaySeconds { get; init; }

    /// <summary>Seconds the simulated mobile waits before answering. Exercises ringback.</summary>
    public required int AnswerDelaySeconds { get; init; }

    /// <summary>
    /// Optional path to CallTree's recordings root. When given, every WAV written during the run is
    /// decoded afterwards and checked for the tones that should be in it, per channel.
    /// </summary>
    public string? RecordingsRoot { get; init; }

    public required bool Verbose { get; init; }

    /// <summary>Legs the run will create at most, which is the size of the tone series to score against.</summary>
    public int ToneCount => (Calls * 2) + 2;

    public static string Usage => """
        CallTree SIP harness - drives a spoofing-mode instance with real SIP and real RTP.

        The instance under test must be started with Spoof:Enabled=true and a blank Trunk:Host, and its
        Spoof:LoopbackHost must point at this harness's --listen port.

          --host <host>          CallTree's SIP host                    (default 127.0.0.1)
          --port <n>             CallTree's SIP port                    (default 5061, the dev profile)
          --did <number>         must equal Telephony:DidNumber         (required)
          --cell <number>        must equal Telephony:MyCellNumber      (required)
          --stranger <number>    caller ID for non-operator scenarios   (default +15550001111)
          --listen <n>           this harness's SIP port                (default 5070)
          --rtp <start>-<end>    this harness's RTP range               (default 13000-13200)
          --scenario <name>      inbound | outbound | screened | missed (default inbound)
          --calls <n>            concurrent callers                     (default 1)
          --duration <sec>       hold time once media is up             (default 12)
          --digits <string>      DTMF to send after answer              (default: per scenario)
          --dtmf-delay <sec>     wait before keying digits in           (default 2)
          --proxy <number>       outbound scenario: dial *<number># mid-call
          --proxy-delay <sec>    when to key the proxy dial in          (default 10)
          --answer-delay <sec>   simulated mobile's ring time           (default 1)
          --recordings <path>    CallTree's recordings root, to verify the WAVs it wrote
          --verbose              full SIP/SIPSorcery logging
        """;

    public static bool TryParse(string[] args, out HarnessOptions options, out string error)
    {
        options = null!;
        error = "";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument {args[i]}.";
                return false;
            }

            var name = args[i][2..];

            if (name is "verbose" or "help")
            {
                flags.Add(name);
                continue;
            }

            if (i + 1 >= args.Length)
            {
                error = $"--{name} needs a value.";
                return false;
            }

            values[name] = args[++i];
        }

        if (flags.Contains("help"))
        {
            return false;
        }

        string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;

        var did = Get("did");
        var cell = Get("cell");
        if (did is null || cell is null)
        {
            error = "--did and --cell are required: they have to match Telephony:DidNumber and Telephony:MyCellNumber.";
            return false;
        }

        var scenarioName = Get("scenario") ?? "inbound";
        if (!Enum.TryParse<Scenario>(scenarioName, ignoreCase: true, out var scenario))
        {
            error = $"Unknown scenario {scenarioName}.";
            return false;
        }

        var rtp = (Get("rtp") ?? "13000-13200").Split('-');
        if (rtp.Length != 2 || !int.TryParse(rtp[0], out var rtpStart) || !int.TryParse(rtp[1], out var rtpEnd))
        {
            error = "--rtp wants a range like 13000-13200.";
            return false;
        }

        int Number(string name, int fallback) =>
            Get(name) is { } raw && int.TryParse(raw, out var parsed) ? parsed : fallback;

        var calls = Number("calls", 1);

        options = new HarnessOptions
        {
            Host = Get("host") ?? "127.0.0.1",
            Port = Number("port", 5061),
            Did = did,
            Cell = cell,
            Stranger = Get("stranger") ?? "+15550001111",
            ListenPort = Number("listen", 5070),
            RtpPortStart = rtpStart,
            RtpPortEnd = rtpEnd,
            Scenario = scenario,
            Calls = calls,
            DurationSeconds = Number("duration", 12),
            // The screening gate wants one digit and the PIN gate wants several; defaulting to the
            // screening digit is right for three of the four scenarios and harmless in the fourth,
            // where the point is precisely that the wrong digit was pressed.
            Digits = Get("digits") ?? (scenario == Scenario.Screened ? "9" : "1"),
            DtmfDelaySeconds = Get("dtmf-delay") is { } delay && double.TryParse(delay, out var parsedDelay) ? parsedDelay : 2,
            ProxyNumber = Get("proxy"),
            ProxyDelaySeconds = Get("proxy-delay") is { } proxyDelay && double.TryParse(proxyDelay, out var parsedProxy)
                ? parsedProxy
                : 10,
            AnswerDelaySeconds = Number("answer-delay", 1),
            RecordingsRoot = Get("recordings"),
            Verbose = flags.Contains("verbose"),
        };

        if (rtpEnd - rtpStart < calls * 8)
        {
            error = $"--rtp range is too narrow for {calls} concurrent calls; allow about 8 ports per leg.";
            return false;
        }

        return true;
    }
}
