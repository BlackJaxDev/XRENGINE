namespace XREngine.Rendering.Vulkan;

/// <summary>Snapshot returned by <see cref="VulkanShadowAtlasDiagnostics"/>.</summary>
public sealed record VulkanShadowAtlasDiagnosticSnapshot(
    bool Enabled,
    VulkanShadowAtlasWriterReceipt[] Writers,
    VulkanShadowAtlasConsumerReceipt[] Consumers,
    VulkanShadowAtlasFrameOperationReceipt[] EnqueuedOperations,
    VulkanShadowAtlasFrameOperationReceipt[] PrimaryOperations);
