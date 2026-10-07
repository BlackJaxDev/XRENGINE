namespace XREngine.Rendering.Commands;

/// <summary>
/// Reusable caller-owned command indices grouped by renderer. Capture and read
/// this storage on one thread; no scene-owned lists remain in the snapshot.
/// </summary>
internal sealed class GpuSceneRendererCommandIndexSnapshot
{
    private readonly Dictionary<XRMeshRenderer, (int First, int Last)> _ranges =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<(uint CommandIndex, int Next)> _indices = [];

    /// <summary>Releases renderer references and retains storage for the next capture.</summary>
    public void Clear()
    {
        _ranges.Clear();
        _indices.Clear();
    }

    internal void Append(XRMeshRenderer renderer, List<uint> commandIndices)
    {
        if (commandIndices.Count == 0)
            return;

        int first = _indices.Count;
        for (int index = 0; index < commandIndices.Count; ++index)
        {
            int next = index + 1 < commandIndices.Count ? _indices.Count + 1 : -1;
            _indices.Add((commandIndices[index], next));
        }

        if (_ranges.TryGetValue(renderer, out var range))
        {
            var tail = _indices[range.Last];
            _indices[range.Last] = (tail.CommandIndex, first);
            first = range.First;
        }
        _ranges[renderer] = (first, _indices.Count - 1);
    }

    /// <summary>Copies the captured indices for one renderer into reusable storage.</summary>
    public bool TryGetCommandIndices(XRMeshRenderer renderer, List<uint> output)
    {
        output.Clear();
        if (!_ranges.TryGetValue(renderer, out var range))
            return false;

        for (int index = range.First; index >= 0; index = _indices[index].Next)
            output.Add(_indices[index].CommandIndex);
        return output.Count != 0;
    }
}
