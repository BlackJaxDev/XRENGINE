namespace XREngine.Rendering.Vulkan;

internal enum EVulkanAdvancedSceneResidentOwner
{
    Draws,
    Instances,
    Geometry,
    StaticVertices,
    Indices,
    PreSkinnedCurrent,
    PreSkinnedPrevious,
    MeshletDescriptors,
    MeshletVertexIndices,
    MeshletTriangleWords,
    Transforms,
    Deformations,
    RenderStates,
    EditorIdentities,
    Materials,
    Kernels,
    Layouts,
    MaterialConstants,
    MaterialBindings,
    Textures,
    Samplers,
    Lights,
    Shadows,
    Probes,
    Environments,
    Decals,
    GiResources,
    Lookups,
    Count,
}

/// <summary>
/// One transaction's immutable resident/COW allocation verdict.  Preflight
/// computes it without mutating arena cursors; upload consumes the same bits.
/// </summary>
internal sealed class VulkanAdvancedScenePublicationAllocationPlan
{
    private readonly bool[] _patches;
    private readonly ulong[] _fullBytes;
    private readonly ulong[] _compactBytes;
    private readonly ulong[] _retainedEnds;
    private ulong _transientBytes;
    internal VulkanAdvancedScenePublicationAllocationPlan()
    {
        _patches = new bool[(int)EVulkanAdvancedSceneResidentOwner.Count];
        _fullBytes = new ulong[(int)EVulkanAdvancedSceneResidentOwner.Count];
        _compactBytes = new ulong[(int)EVulkanAdvancedSceneResidentOwner.Count];
        _retainedEnds = new ulong[(int)EVulkanAdvancedSceneResidentOwner.Count];
    }
    internal ulong RequiredBytes { get; private set; }
    internal void Reset()
    {
        Array.Clear(_patches);
        Array.Clear(_fullBytes);
        Array.Clear(_compactBytes);
        Array.Clear(_retainedEnds);
        _transientBytes = 0u;
        RequiredBytes = 0u;
        IsCompactRebuild = false;
    }
    internal void SetPatch(
        EVulkanAdvancedSceneResidentOwner owner,
        bool patch,
        ulong fullBytes,
        ulong compactBytes,
        ulong retainedEnd)
    {
        int index = (int)owner;
        _patches[index] = patch;
        _fullBytes[index] = fullBytes;
        _compactBytes[index] = compactBytes;
        _retainedEnds[index] = retainedEnd;
        if (!patch)
            RequiredBytes = checked(RequiredBytes + fullBytes);
    }

    /// <summary>
    /// Seals a fragmentation-free completed-slot layout. Retaining every
    /// resident preserves its already packed layout; if even one owner needs
    /// COW, rebuilding every owner from cursor zero is the only layout that
    /// cannot strand holes below a retained high-water mark.
    /// </summary>
    internal void SealResidentPacking()
    {
        for (int index = 0; index < _patches.Length; ++index)
        {
            if (!_patches[index])
            {
                RequiredBytes = 0u;
                for (int owner = 0; owner < _patches.Length; ++owner)
                {
                    _patches[owner] = false;
                    RequiredBytes = checked(RequiredBytes + _fullBytes[owner]);
                }
                return;
            }
        }
    }

    /// <summary>
    /// Chooses an exact packed rebuild after the retained-prefix preflight
    /// proves the prior resident layout would exceed this slot's budget.
    /// </summary>
    internal void SelectCompactRebuild()
    {
        RequiredBytes = _transientBytes;
        for (int owner = 0; owner < _patches.Length; ++owner)
        {
            _patches[owner] = false;
            RequiredBytes = checked(RequiredBytes + _compactBytes[owner]);
        }

        IsCompactRebuild = true;
    }

    /// <summary>Returns the cursor required when all selected residents are retained.</summary>
    internal ulong GetRetainedEnd(ulong cursor)
    {
        ulong end = cursor;
        for (int owner = 0; owner < _patches.Length; ++owner)
            if (_patches[owner])
                end = Math.Max(end, _retainedEnds[owner]);
        return end;
    }

    internal bool IsCompactRebuild { get; private set; }
    internal bool IsPatch(EVulkanAdvancedSceneResidentOwner owner)
        => _patches[(int)owner];

    internal ulong CompactRequiredBytes
    {
        get
        {
            ulong required = _transientBytes;
            for (int owner = 0; owner < _compactBytes.Length; ++owner)
                required = checked(required + _compactBytes[owner]);
            return required;
        }
    }

    internal void AddTransient(ulong bytes)
    {
        _transientBytes = checked(_transientBytes + bytes);
        RequiredBytes = checked(RequiredBytes + bytes);
    }
}
