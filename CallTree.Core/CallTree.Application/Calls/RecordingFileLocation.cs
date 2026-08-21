namespace CallTree.Application.Calls;

/// <summary>
/// Where a recording's file lives, for the streaming endpoint only — never serialized into a read model
/// like <see cref="RecordingSummary"/>, which deliberately doesn't expose storage details.
/// </summary>
/// <param name="RelativePath">Resolve through <c>RecordingStore.TryResolve</c> before opening; this alone
/// is not safe to combine with the recordings root.</param>
/// <param name="IsFinalized">
/// False means the writer hasn't finished (still recording, or the process died mid-call) — the caller
/// decides whether a growing, not-yet-finalized file is still worth serving.
/// </param>
public readonly record struct RecordingFileLocation(string RelativePath, bool IsFinalized);
