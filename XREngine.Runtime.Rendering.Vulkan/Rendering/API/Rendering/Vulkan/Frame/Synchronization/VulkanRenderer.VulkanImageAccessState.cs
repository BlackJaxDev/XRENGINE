using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

    /// <summary>
    /// Captures the layout, visibility, queue ownership, and resource-generation
    /// contract for one tracked Vulkan image subresource.
    /// </summary>
    /// <param name="Layout">The image's Vulkan layout.</param>
    /// <param name="StageMask">Stages that can access the recorded contents.</param>
    /// <param name="AccessMask">Access types permitted for the recorded contents.</param>
    /// <param name="QueueFamilyIndex">The owning queue family, or ignored.</param>
    /// <param name="ExpectedDescriptorLayout">
    /// The descriptor layout implied by the access contract.
    /// </param>
    /// <param name="Serial">The monotonic image-transition serial.</param>
    /// <param name="ResourceGeneration">
    /// The generation that distinguishes recycled native handles.
    /// </param>
    /// <param name="ExternalOwnership">The current external ownership state.</param>
internal readonly record struct VulkanImageAccessState(
        ImageLayout Layout,
        PipelineStageFlags2 StageMask,
        AccessFlags2 AccessMask,
        uint QueueFamilyIndex,
        ImageLayout ExpectedDescriptorLayout,
        ulong Serial,
        ulong ResourceGeneration,
        EVulkanExternalImageOwnership ExternalOwnership =
            EVulkanExternalImageOwnership.EngineOwned)
    {
        /// <summary>
        /// Gets the initial state for an image whose contents and ownership have
        /// not yet been established.
        /// </summary>
        public static VulkanImageAccessState Undefined => new(
            ImageLayout.Undefined,
            PipelineStageFlags2.TopOfPipeBit,
            AccessFlags2.None,
            Vk.QueueFamilyIgnored,
            ImageLayout.Undefined,
            0,
            0,
            EVulkanExternalImageOwnership.EngineOwned);
}
