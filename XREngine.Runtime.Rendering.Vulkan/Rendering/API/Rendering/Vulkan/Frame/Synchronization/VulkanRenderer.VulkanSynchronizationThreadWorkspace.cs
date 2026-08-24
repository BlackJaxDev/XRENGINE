using System;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides allocation-free synchronization scratch storage scoped to the
/// calling thread.
/// </summary>
internal sealed class VulkanSynchronizationThreadWorkspace : IDisposable
{
    private readonly ThreadLocal<VulkanSynchronizationThreadState> _current =
        new(
            static () => new VulkanSynchronizationThreadState(),
            trackAllValues: true);

    /// <summary>
    /// Gets the synchronization scratch state owned by the current thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the thread-local workspace has been disposed.
    /// </exception>
    public VulkanSynchronizationThreadState Current
        => _current.Value
            ?? throw new InvalidOperationException(
                "The Vulkan synchronization workspace has been disposed.");

    /// <summary>
    /// Releases references retained by the current thread's reusable
    /// synchronization buffers.
    /// </summary>
    public void ReleaseCurrentThread()
        => Current.Reset();

    /// <summary>Disposes every per-thread native scratch owner created by this workspace.</summary>
    public void Dispose()
    {
        foreach (VulkanSynchronizationThreadState state in _current.Values)
            state.Dispose();
        _current.Dispose();
    }
}
