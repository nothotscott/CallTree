namespace CallTree.Infrastructure.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Root directory for recording files; Recording.FilePath values are relative to this.</summary>
    public string RecordingsRoot { get; init; } = "recordings";
}
