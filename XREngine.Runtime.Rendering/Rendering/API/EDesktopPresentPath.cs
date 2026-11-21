namespace XREngine.Rendering
{
    /// <summary>
    /// Selects how a renderer shows the final image of a desktop window.
    /// </summary>
    /// <remarks>
    /// Only the Vulkan renderer on Windows reads this value. Other renderers and platforms
    /// always use their native presentation path.
    /// </remarks>
    public enum EDesktopPresentPath
    {
        /// <summary>
        /// Presents through the native Vulkan swapchain of the window. This is the default.
        /// </summary>
        VulkanSwapchain,

        /// <summary>
        /// Renders into images shared with Direct3D 11 and presents them through a DXGI flip-model
        /// swapchain. On some drivers the native windowed Vulkan present makes the desktop
        /// compositor use a full CPU core; the flip-model path avoids that cost. If the path
        /// cannot start, the renderer logs the reason and uses the Vulkan swapchain.
        /// </summary>
        DxgiFlipModel,
    }
}
