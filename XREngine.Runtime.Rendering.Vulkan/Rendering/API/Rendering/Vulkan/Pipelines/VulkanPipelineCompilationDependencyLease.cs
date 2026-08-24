namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds the pipeline dependency mutation gate while pointer-bearing shader and
/// layout state is copied into an immutable compilation request.
/// </summary>
internal readonly ref struct VulkanPipelineCompilationDependencyLease(
    VulkanPipelineManager manager,
    long generation)
{
    internal long Generation { get; } = generation;

    public void Dispose()
        => manager.ReleaseCompilationDependencyLease();
}
