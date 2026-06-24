namespace XREngine
{
    /// <summary>
    /// Selects how the Vulkan backend represents render targets.
    /// </summary>
    public enum EVulkanRenderTargetMode
    {
        /// <summary>
        /// Prefer dynamic rendering when the device supports it, otherwise use legacy render passes.
        /// </summary>
        Auto,

        /// <summary>
        /// Require VK_KHR_dynamic_rendering or Vulkan 1.3 dynamic rendering support.
        /// </summary>
        DynamicRendering,

        /// <summary>
        /// Force the legacy VkRenderPass/VkFramebuffer path.
        /// </summary>
        LegacyRenderPass,
    }
}
