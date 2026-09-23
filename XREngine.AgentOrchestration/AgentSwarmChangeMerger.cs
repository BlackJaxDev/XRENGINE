namespace XREngine.AgentOrchestration;

/// <summary>
/// Validates approved leaf edits against immutable snapshots and materializes one result per file.
/// </summary>
public static class AgentSwarmChangeMerger
{
    public static IReadOnlyList<AgentSwarmCodeChange> Merge(
        IReadOnlyList<AgentSwarmCodeChange> changes,
        IReadOnlyList<AgentContextFileSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(snapshots);
        var files = snapshots.ToDictionary(static snapshot => snapshot.Path, StringComparer.Ordinal);
        var merged = new List<AgentSwarmCodeChange>();
        foreach (IGrouping<string, AgentSwarmCodeChange> group in changes.GroupBy(static change => change.Path, StringComparer.Ordinal))
        {
            if (!files.TryGetValue(group.Key, out AgentContextFileSnapshot? file))
                throw new ArgumentException($"Change path '{group.Key}' has no immutable snapshot.", nameof(changes));
            AgentSwarmCodeChange[] edits = group.ToArray();
            if (edits.Any(edit => !string.Equals(edit.BaseSha256, file.Sha256, StringComparison.Ordinal)))
                throw new ArgumentException($"Change path '{group.Key}' does not use its immutable base hash.", nameof(changes));
            if (file.Sha256 == "missing")
            {
                if (edits.Length != 1 || edits[0].OldText.Length != 0)
                    throw new ArgumentException($"A new file '{group.Key}' may have exactly one whole-file edit.", nameof(changes));
                merged.Add(edits[0]);
                continue;
            }

            var ranges = new List<(AgentSwarmCodeChange Change, int Start)>();
            if (file.Content.Length == 0)
            {
                if (edits.Length != 1 || edits[0].OldText.Length != 0)
                    throw new ArgumentException($"An empty existing file '{group.Key}' may have exactly one creation-style edit.", nameof(changes));
                merged.Add(new AgentSwarmCodeChange
                {
                    Path = file.Path,
                    BaseSha256 = file.Sha256,
                    OldText = file.Content,
                    NewText = edits[0].NewText,
                    NodeId = "merged",
                });
                continue;
            }
            foreach (AgentSwarmCodeChange edit in edits)
            {
                if (edit.OldText.Length == 0)
                    throw new ArgumentException($"Existing file '{group.Key}' requires non-empty old text.", nameof(changes));
                int start = file.Content.IndexOf(edit.OldText, StringComparison.Ordinal);
                if (start < 0 || file.Content.IndexOf(edit.OldText, start + edit.OldText.Length, StringComparison.Ordinal) >= 0)
                    throw new ArgumentException($"Edit from node '{edit.NodeId}' is not uniquely anchored in '{group.Key}'.", nameof(changes));
                ranges.Add((edit, start));
            }
            ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            for (int index = 1; index < ranges.Count; index++)
                if (ranges[index].Start < ranges[index - 1].Start + ranges[index - 1].Change.OldText.Length)
                    throw new ArgumentException($"Edits for '{group.Key}' overlap in the immutable base text.", nameof(changes));

            string materialized = file.Content;
            foreach ((AgentSwarmCodeChange edit, int start) in ranges.OrderByDescending(static range => range.Start))
                materialized = materialized.Remove(start, edit.OldText.Length).Insert(start, edit.NewText);
            merged.Add(new AgentSwarmCodeChange
            {
                Path = file.Path,
                BaseSha256 = file.Sha256,
                OldText = file.Content,
                NewText = materialized,
                NodeId = "merged",
            });
        }
        return merged;
    }
}
