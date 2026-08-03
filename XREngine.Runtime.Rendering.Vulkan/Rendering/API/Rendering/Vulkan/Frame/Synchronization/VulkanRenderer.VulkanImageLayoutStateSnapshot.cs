namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Stores the image-layout signature associated with a cached command-buffer
    /// variant.
    /// </summary>
    private sealed class VulkanImageLayoutStateSnapshot(ulong signature)
    {
        /// <summary>
        /// Gets or sets the signature captured for the command buffer's recorded
        /// end state.
        /// </summary>
        public ulong Signature { get; set; } = signature;
    }
}
