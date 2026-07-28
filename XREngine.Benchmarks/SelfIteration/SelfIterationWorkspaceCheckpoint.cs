namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Recoverable working-tree state captured immediately before an LLM phase.
/// </summary>
public sealed class SelfIterationWorkspaceCheckpoint
{
    public string Directory { get; init; } = string.Empty;
    public string PatchPath { get; init; } = string.Empty;
    public string HeadCommit { get; init; } = string.Empty;
    public string HeadReference { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> WatchedFileHashes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> TrackedDiffPaths { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> NormalStatusEntries { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> UntrackedFiles { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> BackedUpUntrackedFiles { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
