namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>Explicit analysis and fail-closed policy for transient attachments.</summary>
internal enum EVulkanTransientAttachmentMode
{
    /// <summary>Dedicated device-local images; no aliasing or lazy allocation.</summary>
    Baseline,
    /// <summary>Compile lifetime evidence and report candidates without changing allocation.</summary>
    Analyze,
    /// <summary>Request proven optimization; blocked until native lifetime authority exists.</summary>
    ProofGated,
}
