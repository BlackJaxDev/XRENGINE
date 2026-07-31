using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact secondary-artifact generation embedded into a primary recording
/// identity. Native lifetime tracking remains responsible for the matching
/// primary-to-secondary command-buffer pin.
/// </summary>
internal readonly record struct VulkanRecordedCommandArtifactReference(
    CommandBuffer NativeBuffer,
    CommandBufferLevel Level,
    int FrameSlot,
    ulong ArtifactGeneration,
    ulong RecordingGeneration,
    ulong ReferencedResourceIdentity,
    bool IsExecutable,
    VulkanCommandIdentityComponents IdentityComponents)
{
    internal void AddTo(ref FrameOpSignatureHasher identity)
    {
        identity.Add(NativeBuffer.Handle);
        identity.Add((int)Level);
        identity.Add(FrameSlot);
        identity.Add(ArtifactGeneration);
        identity.Add(RecordingGeneration);
        identity.Add(ReferencedResourceIdentity);
        identity.Add(IsExecutable);
        IdentityComponents.AddTo(ref identity);
    }
}
