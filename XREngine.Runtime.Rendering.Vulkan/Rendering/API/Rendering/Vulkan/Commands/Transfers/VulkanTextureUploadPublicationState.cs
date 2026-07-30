namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns recorded and timeline-pending texture upload publication queues.
/// </summary>
internal sealed class VulkanTextureUploadPublicationState
{
    public List<VulkanImportedTexturePendingUpload> RecordedForSubmit { get; } = [];
    public List<PendingRecordedTextureUploadPublication> PendingTimelinePublications { get; } = [];
}
