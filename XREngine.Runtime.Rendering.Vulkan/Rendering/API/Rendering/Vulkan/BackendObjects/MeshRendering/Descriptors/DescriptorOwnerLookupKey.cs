namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one descriptor owner independently of transient draw occurrence
/// slots. Local dynamic uniform sets use their physical buffer identity; other
/// layouts retain the published snapshot and renderer-buffer identities.
/// </summary>
internal readonly record struct DescriptorOwnerLookupKey(
    ulong LayoutFingerprint,
    ulong SchemaFingerprint,
    uint ProgramBindingId,
    int MaterialIdentity,
    ulong MaterialBindingLayoutVersion,
    int ViewFamilyIdentity,
    int DescriptorOwnerSlot,
    bool UsesLocalPhysicalResourceIdentity,
    ulong SnapshotLayoutSignature,
    ulong SnapshotResourceSignature,
    ulong RendererBufferResourceSignature,
    ulong FrameArenaIdentity,
    ulong FrameArenaGeneration);
