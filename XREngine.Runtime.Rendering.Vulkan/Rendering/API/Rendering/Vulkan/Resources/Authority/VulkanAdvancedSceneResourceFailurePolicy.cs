namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Classifies advanced-scene realization failures by whether a newly authored
/// frame can resolve them without changing renderer-global native state.
/// </summary>
internal static class VulkanAdvancedSceneResourceFailurePolicy
{
    internal static bool RequiresFrameRetry(
        EVulkanAdvancedSceneResourceFailure failure)
        => failure is
            EVulkanAdvancedSceneResourceFailure.PublicationSnapshotUnavailable or
            EVulkanAdvancedSceneResourceFailure.SourceMismatch or
            EVulkanAdvancedSceneResourceFailure.TextureWrapperUnavailable or
            EVulkanAdvancedSceneResourceFailure.TextureDescriptorNotReady;
}
