namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Separates the three generations involved in a frequency-owned payload.
/// Layout and owner identity select the stable reservation, content controls
/// byte publication, and recording-visible generation controls whether a
/// prepared command artifact may retain that reservation.
/// </summary>
internal readonly record struct VulkanAutoUniformPublicationIdentity(
    ulong PublicationLayoutSignature,
    EVulkanBindingFrequency Frequency,
    ulong OwnerIdentity,
    ulong ContentGeneration,
    ulong RecordingVisibleGeneration)
{
    internal bool IsComplete =>
        PublicationLayoutSignature != 0 &&
        Frequency is > EVulkanBindingFrequency.Unknown and
            < EVulkanBindingFrequency.Count &&
        OwnerIdentity != 0 &&
        RecordingVisibleGeneration != 0;

    internal bool HasStableRecordingLocation(
        in VulkanAutoUniformPublicationIdentity other)
        => PublicationLayoutSignature == other.PublicationLayoutSignature &&
            Frequency == other.Frequency &&
            OwnerIdentity == other.OwnerIdentity &&
            RecordingVisibleGeneration == other.RecordingVisibleGeneration;

    internal bool RequiresContentPublication(
        in VulkanAutoUniformPublicationIdentity published)
        => !HasStableRecordingLocation(published) ||
            ContentGeneration != published.ContentGeneration;
}
