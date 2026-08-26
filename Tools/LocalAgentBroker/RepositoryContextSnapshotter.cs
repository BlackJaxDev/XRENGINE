using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Admits an all-or-nothing immutable set of repository context files.
/// </summary>
internal sealed class RepositoryContextSnapshotter
{
    private readonly RepositoryPathPolicy _pathPolicy;
    private readonly RepositoryTextFileReader _reader;

    public RepositoryContextSnapshotter(RepositoryPathPolicy pathPolicy)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _reader = new RepositoryTextFileReader(pathPolicy);
    }

    public IReadOnlyList<AgentContextFileSnapshot> Capture(
        IReadOnlyList<AgentContextFileRequest> contextFiles,
        AgentRunBudget budget)
    {
        ArgumentNullException.ThrowIfNull(contextFiles);
        ArgumentNullException.ThrowIfNull(budget);
        if (contextFiles.Count == 0)
            return [];
        if (contextFiles.Count > budget.MaxContextFiles)
            throw new ArgumentException("context_files exceeds budget.max_context_files.");

        var resolved = new List<(AgentContextFileRequest Request, string FullPath, string RelativePath)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentContextFileRequest request in contextFiles)
        {
            string fullPath = _pathPolicy.ResolveTextFile(request.Path);
            string relativePath = _pathPolicy.ToRelativePath(fullPath);
            if (!seenPaths.Add(relativePath))
                throw new ArgumentException($"context_files contains duplicate path '{relativePath}'.");
            resolved.Add((request, fullPath, relativePath));
        }

        resolved.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        var snapshots = new List<AgentContextFileSnapshot>(resolved.Count);
        long totalRawBytes = 0;
        long totalRenderedBytes = 0;
        foreach ((AgentContextFileRequest request, string fullPath, string relativePath) in resolved)
        {
            AgentContextFileSnapshot snapshot = _reader.Read(
                fullPath,
                relativePath,
                budget.MaxContextFileBytes,
                request.StartLine,
                request.EndLine,
                request.ExpectedSha256);
            totalRawBytes += snapshot.RawByteLength;
            if (totalRawBytes > budget.MaxContextBytes)
                throw new ArgumentException("context_files exceeds budget.max_context_bytes.");
            totalRenderedBytes += AgentContextFileInputBuilder.GetRenderedByteCount(snapshot);
            if (totalRenderedBytes > budget.MaxContextRenderedBytes)
                throw new ArgumentException("context_files exceeds budget.max_context_rendered_bytes.");
            snapshots.Add(snapshot);
        }

        return snapshots;
    }
}
