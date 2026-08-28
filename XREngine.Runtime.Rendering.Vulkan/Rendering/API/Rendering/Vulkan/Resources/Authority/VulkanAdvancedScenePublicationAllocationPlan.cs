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
    internal VulkanAdvancedScenePublicationAllocationPlan()
    {
        _patches = new bool[(int)EVulkanAdvancedSceneResidentOwner.Count];
        _fullBytes = new ulong[(int)EVulkanAdvancedSceneResidentOwner.Count];
    }
    internal ulong RequiredBytes { get; private set; }
    internal void Reset()
    {
        Array.Clear(_patches);
        Array.Clear(_fullBytes);
        RequiredBytes = 0u;
    }
    internal void SetPatch(
        EVulkanAdvancedSceneResidentOwner owner,
        bool patch,
        ulong fullBytes)
    {
        int index = (int)owner;
        _patches[index] = patch;
        _fullBytes[index] = fullBytes;
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
    internal bool IsPatch(EVulkanAdvancedSceneResidentOwner owner)
        => _patches[(int)owner];
    internal void AddTransient(ulong bytes)
        => RequiredBytes = checked(RequiredBytes + bytes);
}
