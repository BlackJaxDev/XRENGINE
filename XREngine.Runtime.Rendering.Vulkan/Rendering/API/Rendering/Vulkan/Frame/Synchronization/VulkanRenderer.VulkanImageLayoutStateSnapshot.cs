namespace XREngine.Rendering.Vulkan;

    /// <summary>
    /// Stores the image-layout signature associated with a cached command-buffer
    /// variant.
    /// </summary>
internal sealed class VulkanImageLayoutStateSnapshot(ulong signature)
    {
        /// <summary>
        /// Gets or sets the signature captured for the command buffer's recorded
        /// end state.
        /// </summary>
        public ulong Signature { get; set; } = signature;
}
