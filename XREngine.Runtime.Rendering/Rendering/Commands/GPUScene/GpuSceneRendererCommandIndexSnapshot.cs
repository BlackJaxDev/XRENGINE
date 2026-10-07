namespace XREngine.Rendering.Commands;

using XREngine.Rendering.Compute;

/// <summary>
/// Reusable caller-owned command indices grouped by renderer. Each index keeps
/// the material that its published draw uses, including command overrides.
/// Capture and read this storage on one thread; no scene-owned lists remain in
/// the snapshot.
/// </summary>
internal sealed class GpuSceneRendererCommandIndexSnapshot
{
    private readonly Dictionary<XRMeshRenderer, (int First, int Last)> _ranges =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<(uint CommandIndex, PhysicsChainDrawMaterialSnapshot Material, int Next)> _indices = [];
    internal long PublicationGeneration { get; private set; } = -1;

    /// <summary>Releases renderer references and retains storage for the next capture.</summary>
    public void Clear()
    {
        _ranges.Clear();
        _indices.Clear();
        PublicationGeneration = -1;
    }

    internal void SetPublicationGeneration(long generation)
        => PublicationGeneration = generation;

    internal bool ContainsRenderer(XRMeshRenderer renderer)
        => _ranges.ContainsKey(renderer);

    internal void CopyTo(GpuSceneRendererCommandIndexSnapshot destination)
    {
        destination.Clear();
        for (int index = 0; index < _indices.Count; ++index)
            destination._indices.Add(_indices[index]);
        foreach (KeyValuePair<XRMeshRenderer, (int First, int Last)> entry in _ranges)
            destination._ranges.Add(entry.Key, entry.Value);
        destination.PublicationGeneration = PublicationGeneration;
    }

    internal void Append(XRMeshRenderer renderer, List<uint> commandIndices)
        => Append(renderer, commandIndices, null);

    /// <summary>
    /// Appends command indices for one renderer. <paramref name="materials"/>
    /// is null or holds one drawn material for each index.
    /// </summary>
    internal void Append(XRMeshRenderer renderer, List<uint> commandIndices, List<XRMaterial?>? materials)
    {
        if (commandIndices.Count == 0)
            return;
        if (materials is not null && materials.Count != commandIndices.Count)
            throw new ArgumentException("Each command index requires one material entry.", nameof(materials));

        int first = _indices.Count;
        for (int index = 0; index < commandIndices.Count; ++index)
        {
            int next = index + 1 < commandIndices.Count ? _indices.Count + 1 : -1;
            _indices.Add((commandIndices[index],
                PhysicsChainDrawMaterialSnapshot.Capture(materials?[index]), next));
        }

        if (_ranges.TryGetValue(renderer, out var range))
        {
            var tail = _indices[range.Last];
            _indices[range.Last] = (tail.CommandIndex, tail.Material, first);
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

    /// <summary>
    /// Copies the drawn material of each captured command for one renderer
    /// into reusable storage. An entry is null when the capture had no material.
    /// </summary>
    public bool TryGetCommandMaterials(XRMeshRenderer renderer, List<XRMaterial?> output)
    {
        output.Clear();
        if (!_ranges.TryGetValue(renderer, out var range))
            return false;

        for (int index = range.First; index >= 0; index = _indices[index].Next)
            output.Add(_indices[index].Material.Material);
        return output.Count != 0;
    }

    /// <summary>Copies the material state sealed with each published command.</summary>
    public bool TryGetCommandMaterialSnapshots(XRMeshRenderer renderer,
        List<PhysicsChainDrawMaterialSnapshot> output)
    {
        output.Clear();
        if (!_ranges.TryGetValue(renderer, out var range))
            return false;

        for (int index = range.First; index >= 0; index = _indices[index].Next)
            output.Add(_indices[index].Material);
        return output.Count != 0;
    }
}
