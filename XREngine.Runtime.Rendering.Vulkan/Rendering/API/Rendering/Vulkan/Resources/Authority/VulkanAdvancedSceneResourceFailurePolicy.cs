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

    /// <summary>
    /// Returns whether replacing the active pipeline or output state can remove
    /// the failed advanced-scene requirement without repairing native authority.
    /// Fixed capacities and correctness/integrity failures intentionally fall
    /// through to the terminal classification.
    /// </summary>
    internal static bool AllowsRecoveryAfterStateChange(
        EVulkanAdvancedSceneResourceFailure failure)
        => failure is
            EVulkanAdvancedSceneResourceFailure.RuntimeUnavailable or
            EVulkanAdvancedSceneResourceFailure.DescriptorIndexingUnavailable or
            EVulkanAdvancedSceneResourceFailure.DescriptorHeapUnsupported or
            EVulkanAdvancedSceneResourceFailure.UnsupportedTextureShape or
            EVulkanAdvancedSceneResourceFailure.UnsupportedSamplerState;
}
