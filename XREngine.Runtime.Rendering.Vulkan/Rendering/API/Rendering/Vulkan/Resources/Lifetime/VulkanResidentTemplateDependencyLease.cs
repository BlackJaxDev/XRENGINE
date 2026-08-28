namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Generation-checked structural ownership of every native object referenced by
/// one resident command-template artifact. This is deliberately not a per-frame
/// allocation mechanism.
/// </summary>
internal sealed class VulkanResidentTemplateDependencyLease : IDisposable
{
    private VulkanResourceRuntime? _runtime;
    private readonly VulkanResourceSlotHandle[] _dependencies;

    internal VulkanResidentTemplateDependencyLease(
        VulkanResourceRuntime runtime,
        VulkanResourceSlotHandle[] dependencies)
    {
        _runtime = runtime;
        _dependencies = dependencies;
    }

    internal ReadOnlySpan<VulkanResourceSlotHandle> Dependencies => _dependencies;

    internal bool IsActive => Volatile.Read(ref _runtime) is not null;

    public void Dispose()
    {
        VulkanResourceRuntime? runtime = Interlocked.Exchange(ref _runtime, null);
        runtime?.ReleaseResidentTemplateDependencies(_dependencies);
    }
}
