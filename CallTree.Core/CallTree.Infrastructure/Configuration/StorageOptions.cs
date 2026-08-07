namespace CallTree.Infrastructure.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root directory for recording files; Recording.FilePath values are relative to this.</summary>
    public string RecordingsRoot { get; init; } = "recordings";

    /// <summary>
    /// The writable configuration file the settings UI edits, layered over appsettings and under the
    /// environment. It defaults into the data directory alongside the database and the recordings so
    /// that one bind mount carries the whole instance — move the volume and the trunk moves with it.
    /// </summary>
    /// <remarks>
    /// Read directly out of configuration during startup, before the container exists, because the
    /// configuration source has to be in place before anything binds options. Relative paths resolve
    /// against the content root, matching how the prompt directory is resolved.
    /// </remarks>
    public string ConfigFile { get; init; } = "data/config.json";
}
