using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command-runtime-local per-thread state. Renderer scopes explicitly install and
/// restore values; this workspace never owns a renderer or native device.
/// </summary>
internal sealed class VulkanCommandThreadWorkspace
{
    private readonly ThreadLocal<VulkanCommandThreadContext> _current;

    internal VulkanCommandThreadWorkspace(VulkanCommandRuntime owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _current = new ThreadLocal<VulkanCommandThreadContext>(
            () => new VulkanCommandThreadContext(owner),
            trackAllValues: false);
    }

    public VulkanCommandThreadContext Current
        => _current.Value ?? throw new InvalidOperationException("The Vulkan command thread workspace has been disposed.");

    /// <summary>
    /// Reads an existing per-thread context without allocating one for a wrapper
    /// operation that has no command-local scope.
    /// </summary>
    public bool TryGetCurrent(out VulkanCommandThreadContext context)
    {
        if (_current.IsValueCreated)
        {
            context = _current.Value ?? throw new InvalidOperationException(
                "The Vulkan command thread workspace has been disposed.");
            return true;
        }

        context = null!;
        return false;
    }

    public void ReleaseCurrentThread()
    {
        if (_current.IsValueCreated)
            _current.Value!.Reset();
    }
}
