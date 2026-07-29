namespace XREngine.Scene.Importers;

/// <summary>
/// Dependency closure reached from one Unity prefab or scene.
/// </summary>
public sealed class UnityDependencyGraph
{
    private readonly Dictionary<string, UnityDependencyNode> _nodes =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, UnityDependencyNode> Nodes => _nodes;
    public List<UnityDependencyEdge> UnresolvedEdges { get; } = [];

    internal void RemovePrefabModificationEdges(
        string sourcePath,
        long referringObjectFileId,
        string entrySourcePath)
    {
        string normalizedSource = Path.GetFullPath(sourcePath);
        if (_nodes.TryGetValue(normalizedSource, out UnityDependencyNode? sourceNode))
        {
            sourceNode.OutgoingEdges.RemoveAll(edge =>
                edge.ReferringObjectFileId == referringObjectFileId);
        }

        UnresolvedEdges.RemoveAll(edge =>
            string.Equals(Path.GetFullPath(edge.SourcePath), normalizedSource, StringComparison.OrdinalIgnoreCase) &&
            edge.ReferringObjectFileId == referringObjectFileId);
        PruneUnreachable(entrySourcePath);
    }

    internal UnityDependencyNode GetOrAdd(
        string path,
        Func<UnityDependencyNode> factory,
        XREngine.Scene.Prefabs.UnityImportDependencyKind kind)
    {
        if (!_nodes.TryGetValue(path, out UnityDependencyNode? node))
        {
            node = factory();
            _nodes.Add(path, node);
        }
        else if (kind < node.Kind)
        {
            // RequiredVisual is deliberately the first enum member and therefore wins.
            node.Kind = kind;
        }

        return node;
    }

    private void PruneUnreachable(string entrySourcePath)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(entrySourcePath));

        while (pending.Count > 0)
        {
            string path = pending.Pop();
            if (!reachable.Add(path) || !_nodes.TryGetValue(path, out UnityDependencyNode? node))
                continue;

            if (!path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                string metaPath = path + ".meta";
                if (_nodes.ContainsKey(metaPath))
                    pending.Push(metaPath);
            }

            foreach (UnityDependencyEdge edge in node.OutgoingEdges)
            {
                if (!string.IsNullOrWhiteSpace(edge.TargetPath))
                    pending.Push(Path.GetFullPath(edge.TargetPath));
            }
        }

        foreach (string unreachable in _nodes.Keys
            .Where(path => !reachable.Contains(path))
            .ToArray())
        {
            _nodes.Remove(unreachable);
        }

        UnresolvedEdges.RemoveAll(edge => !reachable.Contains(Path.GetFullPath(edge.SourcePath)));
    }
}
