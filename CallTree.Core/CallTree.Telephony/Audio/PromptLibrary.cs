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
}

/// <summary>
/// Loads IVR prompts from disk once at startup and keeps them as raw PCM ready to stream.
/// </summary>
/// <remarks>
/// Prompts live in a configured directory rather than being embedded in the assembly, because the
/// wording is expected to change without a rebuild — the recording disclosure in particular is an open
/// legal decision (Florida is all-party consent). Decoding at startup means a malformed or missing file
/// is a loud problem at boot instead of silence in the middle of a real call.
/// </remarks>
public sealed class PromptLibrary
{
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

        foreach (var required in new[] { PromptNames.Greeting, PromptNames.Accepted, PromptNames.Rejected })
        {
            if (!_prompts.ContainsKey(required))
            {
                _logger.LogWarning("Prompt '{Name}' is missing from {Root}; that step will be silent.", required, Root);
            }
        }
    }
}
