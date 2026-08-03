using System;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Provides allocation-free synchronization scratch storage scoped to the
    /// calling thread.
    /// </summary>
    private sealed class VulkanSynchronizationThreadWorkspace
    {
        private readonly ThreadLocal<VulkanSynchronizationThreadState> _current =
            new(
                static () => new VulkanSynchronizationThreadState(),
                trackAllValues: false);

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
    }
}
