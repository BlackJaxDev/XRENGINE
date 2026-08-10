namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds the dependency mutation gate after all in-flight compilation has been
/// drained. Disposing publishes the post-mutation dependency generation.
/// </summary>
internal readonly ref struct VulkanPipelineCompilationMutationLease(
    VulkanPipelineManager manager,
    bool outermost)
{
    public void Dispose()
        => manager.ReleaseCompilationMutationLease(outermost);
}
