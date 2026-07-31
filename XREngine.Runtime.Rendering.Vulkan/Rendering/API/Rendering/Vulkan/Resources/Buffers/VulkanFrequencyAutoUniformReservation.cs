namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable arena range and per-frame publication ledger shared by every draw
/// that references the same linked auto-uniform owner.
/// </summary>
internal sealed class VulkanFrequencyAutoUniformReservation(
    VulkanFrequencyAutoUniformReservationKey key,
    ulong offset,
    uint size,
    ulong recordingVisibleGeneration,
    int frameCount)
{
    internal VulkanFrequencyAutoUniformReservationKey Key { get; } = key;
    internal ulong Offset { get; } = offset;
    internal uint Size { get; } = size;
    internal ulong RecordingVisibleGeneration { get; } =
        recordingVisibleGeneration;
    internal VulkanAutoUniformPublicationState[] PublicationStates { get; } =
        new VulkanAutoUniformPublicationState[Math.Max(frameCount, 1)];

    internal VulkanAutoUniformPublicationIdentity CapturePublicationIdentity(
        ulong contentGeneration)
        => new(
            Key.PublicationLayoutSignature,
            Key.Frequency,
            Key.OwnerIdentity,
            contentGeneration,
            RecordingVisibleGeneration);
}
