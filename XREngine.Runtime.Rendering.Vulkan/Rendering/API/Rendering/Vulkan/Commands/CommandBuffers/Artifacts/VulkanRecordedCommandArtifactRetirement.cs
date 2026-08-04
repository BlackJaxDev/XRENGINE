using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable ownership snapshot for one native command buffer after its
/// reusable artifact slot has moved on to another generation.
/// </summary>
internal readonly record struct VulkanRecordedCommandArtifactRetirement(
    CommandBuffer NativeBuffer,
    CommandBufferLevel Level,
    CommandPool OwnerPool,
    bool OwnsPool,
    int FrameSlot,
    ulong ArenaOwnerIdentity,
    ulong ArtifactGeneration,
    ulong RecordingGeneration,
    CommandRecordingDependencySignature DependencyIdentity,
    ulong ReferencedResourceIdentity,
    int QueuedSubmissionCount,
    int RecordedPrimaryReferenceCount)
{
    internal bool IsValid =>
        NativeBuffer.Handle != 0 &&
        OwnerPool.Handle != 0;

    internal bool WasPendingAtCapture =>
        QueuedSubmissionCount != 0 ||
        RecordedPrimaryReferenceCount != 0;
}
