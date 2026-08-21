using System.Text.Json;
using System.Text.Json.Nodes;
using CallTree.Application.Configuration;

namespace CallTree.Api.Settings;

/// <summary>
/// The writable JSON configuration file that sits between the appsettings files and the environment.
/// </summary>
/// <remarks>
/// It lives under the data directory by default so a single bind mount carries the database, the
/// recordings and the trunk configuration together. It holds the trunk password in plaintext, which is
/// the unavoidable cost of letting the UI set one: it is written with owner-only permissions where the
/// platform supports them, and the same care that applies to the recordings applies to this file.
/// </remarks>
public sealed class RuntimeConfigFile(string path)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Absolute path of the file. It need not exist.</summary>
    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    /// <summary>
    /// Resolves the configured path. Read straight out of configuration rather than through
    /// <see cref="StorageOptions"/>, because the source has to be registered before options binding
    /// exists. Relative paths resolve against the content root, as the prompt directory does.
    /// </summary>
    public static string ResolvePath(string? configured, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = new StorageOptions().ConfigFile;
        }

        return System.IO.Path.IsPathRooted(configured)
            ? System.IO.Path.GetFullPath(configured)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRoot, configured));
    }

    /// <summary>The file's current contents, or an empty document when it does not exist yet.</summary>
    public JsonObject Read()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        // A malformed file would already have stopped the host at startup, since the configuration
        // provider parses it eagerly. Failing loudly here rather than silently starting from empty
        // keeps a hand-edited file from being quietly discarded.
        var parsed = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject
            ?? throw new InvalidDataException($"{Path} does not contain a JSON object.");

        return parsed;
    }

    /// <summary>
    /// Replaces the file's contents. Written to a sibling temporary file and moved into place, so a
    /// crash or a full disk cannot leave a half-written file that stops the next boot.
    /// </summary>
    public async Task WriteAsync(JsonObject document, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The configuration provider watches this exact filename, so the temporary file does not
        // trigger a reload of a partial document; the move does.
        var temporary = Path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            document.ToJsonString(WriteOptions) + Environment.NewLine,
            cancellationToken);

        RestrictToOwner(temporary);
        File.Move(temporary, Path, overwrite: true);
    }

    private static void RestrictToOwner(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort: some filesystems (and bind mounts from a Windows host) do not support it.
            // The file still has to be written - failing the save over permissions would be worse.
        }
    }
}
