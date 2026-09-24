using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Records the authorities that can change a mesh's local descriptor buffer writes.
/// Snapshot resource signatures invalidate the memo without becoming allocation identity.
/// </summary>
internal readonly record struct DescriptorLocalFingerprintMemoKey(
    VkRenderProgram Program,
    XRMaterial Material,
    IReadOnlyList<DescriptorBindingInfo> Bindings,
    uint ProgramBindingId,
    ulong ProgramLinkGeneration,
    ulong LayoutFingerprint,
    ulong SchemaFingerprint,
    ulong MaterialBindingLayoutVersion,
    ulong MaterialBindingResourceVersion,
    int FrameCount,
    int SetCount,
    uint ActiveSetMask,
    bool UsesSharedMaterialTier,
    int DrawUniformSlot,
    int ViewFamilyIdentity,
    ulong SnapshotLayoutSignature,
    ulong SnapshotResourceSignature,
    ulong SnapshotStableResourceSignature,
    ulong PreparedMaterialTableSignature,
    ulong RendererBufferSignature,
    ulong FrameArenaIdentity,
    ulong FrameArenaGeneration,
    ulong NativeBufferBindingRevision)
{
    /// <summary>Compares object owners by identity; mutable owners must never alias.</summary>
    internal bool Matches(in DescriptorLocalFingerprintMemoKey other)
        => ReferenceEquals(Program, other.Program) &&
           ReferenceEquals(Material, other.Material) &&
           ReferenceEquals(Bindings, other.Bindings) &&
           ProgramBindingId == other.ProgramBindingId &&
           ProgramLinkGeneration == other.ProgramLinkGeneration &&
           LayoutFingerprint == other.LayoutFingerprint &&
           SchemaFingerprint == other.SchemaFingerprint &&
           MaterialBindingLayoutVersion == other.MaterialBindingLayoutVersion &&
           MaterialBindingResourceVersion == other.MaterialBindingResourceVersion &&
           FrameCount == other.FrameCount &&
           SetCount == other.SetCount &&
           ActiveSetMask == other.ActiveSetMask &&
           UsesSharedMaterialTier == other.UsesSharedMaterialTier &&
           DrawUniformSlot == other.DrawUniformSlot &&
           ViewFamilyIdentity == other.ViewFamilyIdentity &&
           SnapshotLayoutSignature == other.SnapshotLayoutSignature &&
           SnapshotResourceSignature == other.SnapshotResourceSignature &&
           SnapshotStableResourceSignature == other.SnapshotStableResourceSignature &&
           PreparedMaterialTableSignature == other.PreparedMaterialTableSignature &&
           RendererBufferSignature == other.RendererBufferSignature &&
           FrameArenaIdentity == other.FrameArenaIdentity &&
           FrameArenaGeneration == other.FrameArenaGeneration &&
           NativeBufferBindingRevision == other.NativeBufferBindingRevision;
}
