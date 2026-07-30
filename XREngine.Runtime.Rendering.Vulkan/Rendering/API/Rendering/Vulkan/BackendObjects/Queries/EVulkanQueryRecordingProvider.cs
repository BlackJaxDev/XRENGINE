namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Selects the Vulkan command family used to record a query descriptor.
/// </summary>
internal enum EVulkanQueryRecordingProvider
{
    Unsupported,
    BeginEnd,
    Timestamp,
    TransformFeedbackIndexed,
    PrimitivesGeneratedIndexed,
    AccelerationStructureProperties,
    MicromapProperties,
    Performance,
    Video,
}
