namespace XREngine.Rendering.Vulkan;

internal readonly record struct MeshDescriptorPoolSlabKey(
    ulong PoolSizeFingerprint,
    int SetsPerAllocation,
    bool UpdateAfterBind);
