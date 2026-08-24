namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one descriptor owner independently of transient draw occurrence
/// slots. Immutable snapshot, renderer-buffer, and mapped-arena identities keep
/// the generation fast path exact without rebuilding a reflected fingerprint.
/// </summary>
internal readonly record struct DescriptorOwnerLookupKey(
    ulong LayoutFingerprint,
    ulong SchemaFingerprint,
    uint ProgramBindingId,
    int MaterialIdentity,
    ulong MaterialBindingLayoutVersion,
    int ViewFamilyIdentity,
    int DescriptorOwnerSlot,
    ulong SnapshotLayoutSignature,
    ulong SnapshotResourceSignature,
    ulong RendererBufferResourceSignature,
    ulong FrameArenaIdentity,
    ulong FrameArenaGeneration);
