using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns recorded and timeline-pending texture upload publication queues.
/// </summary>
internal sealed class VulkanTextureUploadPublicationState
{
    private readonly ThreadLocal<List<VulkanImportedTexturePendingUpload>> _recordedForSubmit =
        new(static () => [], trackAllValues: false);

    /// <summary>
    /// Gets the upload batch recorded by the current command-recording thread.
    /// Each persistent Vulkan recording worker owns one reusable list so
    /// concurrent OpenXR eye recording cannot clear or consume its peer's batch.
    /// </summary>
    public List<VulkanImportedTexturePendingUpload> RecordedForSubmit
        => _recordedForSubmit.Value
            ?? throw new InvalidOperationException(
                "The Vulkan texture-upload recording batch is unavailable.");

    public List<PendingRecordedTextureUploadPublication> PendingTimelinePublications { get; } = [];
}
