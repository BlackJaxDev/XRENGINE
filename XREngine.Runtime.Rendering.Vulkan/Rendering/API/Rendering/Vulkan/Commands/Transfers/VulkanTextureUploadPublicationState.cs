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

    /// <summary>
    /// Drops every recording thread's batch list after generation teardown so a
    /// long-lived thread cannot keep this generation's uploads reachable.
    /// </summary>
    internal void ReleaseThreadBatches()
        => _recordedForSubmit.Dispose();

    internal void QueueRecordedForTimeline(
        ulong timelineValue,
        string uploadSource)
    {
        List<VulkanImportedTexturePendingUpload> recorded = RecordedForSubmit;
        for (int index = 0; index < recorded.Count; index++)
        {
            PendingTimelinePublications.Add(
                new PendingRecordedTextureUploadPublication(
                    recorded[index],
                    timelineValue,
                    uploadSource));
        }

        recorded.Clear();
    }
}
