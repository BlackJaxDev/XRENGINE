namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents the canonical Vulkan samplers used in the renderer.
    /// </summary>
    internal enum VulkanCanonicalSampler
    {
        /// <summary>
        /// A linear filtering sampler with clamping at the edges.
        /// </summary>
        LinearClamp,
        /// <summary>
        /// A nearest-neighbor filtering sampler with clamping at the edges.
        /// </summary>
        NearestClamp,
        /// <summary>
        /// A linear filtering sampler with repeating at the edges.
        /// </summary>
        LinearRepeat,
        /// <summary>
        /// An anisotropic filtering sampler.
        /// </summary>
        Anisotropic,
        /// <summary>
        /// A sampler used for shadow comparison.
        /// </summary>
        ShadowComparison,
    }
}
