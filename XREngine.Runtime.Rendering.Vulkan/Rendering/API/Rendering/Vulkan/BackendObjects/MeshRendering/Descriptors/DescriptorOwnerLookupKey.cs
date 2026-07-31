namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies one descriptor owner independently of transient draw occurrence
/// slots and mutable resource fingerprints.
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
    ulong SnapshotSamplerResourceSignature);
