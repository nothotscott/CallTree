using CallTree.Telephony.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTree.Telephony.Audio;

/// <summary>Named prompts the IVR can play.</summary>
public static class PromptNames
{
    /// <summary>Greeting plus the press-1 instruction. Carries the recording disclosure.</summary>
    public const string Greeting = "greeting";

    /// <summary>Played once the caller passes the gate.</summary>
    public const string Accepted = "accepted";

    /// <summary>Played when the caller presses nothing, or the wrong key.</summary>
    public const string Rejected = "rejected";

    /// <summary>
    /// Played to the Outbound-source operator before recording starts - a reminder, not a notice: they
    /// already know they are being recorded, this is telling them to disclose it to whoever they merge in
    /// afterwards, since the merge happens on the handset and CallTree is not told about it. See
    /// <c>Telephony:RecordingToneIntervalSeconds</c>.
    /// </summary>
    public const string RecordingReminder = "recording-reminder";

    /// <summary>
    /// "This call is being recorded" - played to the party reached by an outbound proxy dial
    /// (<c>*{NUMBER}#</c> on the Outbound-source path) once they answer, before their audio is mixed into
    /// the recording. Unlike <see cref="RecordingReminder"/>'s party, CallTree placed this leg itself, so
    /// it can actually disclose to them directly rather than relying on the operator to say it out loud.
    /// </summary>
    public const string RecordingNotice = "recording-notice";

    /// <summary>Asks the Outbound-source caller for the PIN. Only used when one is configured.</summary>
    public const string PinRequest = "pin-request";

    /// <summary>
    /// Short tone repeated during recording, when an interval is configured. Unlike the spoken notice
    /// this is audible to anyone merged into the call later.
    /// </summary>
    public const string RecordingTone = "recording-tone";

    /// <summary>Played to an Inbound caller whose bridge to the mobile went unanswered, before hanging up.</summary>
    public const string Apology = "apology";

    /// <summary>
    /// A ringback tone looped to an Inbound caller while the bridge's outbound leg is ringing, so they
    /// don't sit in dead silence waiting for the mobile to answer.
    /// </summary>
    public const string Ringing = "ringing";
}

/// <summary>
/// Loads IVR prompts from disk once at startup and keeps them as raw PCM ready to stream.
/// </summary>
/// <remarks>
/// Prompts live in a configured directory rather than being embedded in the assembly, because the
/// wording is expected to change without a rebuild — the recording disclosure in particular is an open
/// legal decision that varies by jurisdiction, and several require every party to consent. Decoding at
/// startup means a malformed or missing file is a loud problem at boot instead of silence mid-call.
/// </remarks>
public sealed class PromptLibrary
{
    /// <summary>
    /// Prompts every deployment needs. <see cref="PromptNames.PinRequest"/> and
    /// <see cref="PromptNames.RecordingTone"/> are left out because they belong to features that are off
    /// unless configured; warning about them unconditionally would train the operator to ignore the
    /// warning that matters. <see cref="PromptNames.RecordingReminder"/> and
    /// <see cref="PromptNames.RecordingNotice"/> are both in the list precisely because a disclosure prompt
    /// must never go missing quietly - the latter is the one mechanism that can actually inform a
    /// proxy-dialed third party directly.
    /// </summary>
    private static readonly string[] RequiredPrompts =
    [
        PromptNames.Greeting,
        PromptNames.Accepted,
        PromptNames.Rejected,
        PromptNames.RecordingReminder,
        PromptNames.RecordingNotice,
    ];

    private readonly Dictionary<string, PcmAudio> _prompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PromptLibrary> _logger;

    public PromptLibrary(IOptions<TelephonyOptions> options, IHostEnvironment environment, ILogger<PromptLibrary> logger)
    {
        _logger = logger;

        // Relative to the content root, not the working directory — otherwise it resolves differently
        // under `dotnet run`, a published build, and a container.
        var configured = options.Value.PromptsRoot;
        Root = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));

        Load();
    }

    public string Root { get; }

    /// <summary>Prompts that loaded, in name order.</summary>
    public IReadOnlyList<string> Loaded => [.. _prompts.Keys.Order()];

    /// <summary>
    /// Required prompts that did not load. Non-empty means a step of the IVR will play silence, which
    /// looks like success from every other angle: signalling works and the call connects.
    /// </summary>
    public IReadOnlyList<string> Missing =>
        [.. RequiredPrompts.Where(name => !_prompts.ContainsKey(name))];

    public bool TryGet(string name, out PcmAudio prompt) => _prompts.TryGetValue(name, out prompt!);

    private void Load()
    {
        if (!Directory.Exists(Root))
        {
            _logger.LogWarning("Prompt directory {Root} does not exist - the IVR will run without audio.", Root);
            return;
        }

        foreach (var path in Directory.EnumerateFiles(Root, "*.wav"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try
            {
                var pcm = WavAudio.ReadPcm(File.ReadAllBytes(path));
                _prompts[name] = pcm;
                _logger.LogInformation(
                    "Loaded prompt '{Name}' ({Duration:0.0}s, {SampleRate} Hz) from {Path}",
                    name, pcm.Duration.TotalSeconds, pcm.SampleRate, path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not load prompt '{Name}' from {Path}", name, path);
            }
        }

        foreach (var required in RequiredPrompts)
        {
            if (!_prompts.ContainsKey(required))
            {
                _logger.LogWarning("Prompt '{Name}' is missing from {Root}; that step will be silent.", required, Root);
            }
        }
    }
}
