using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a lease for performing operations on a Vulkan queue, ensuring that the queue is accessed in a thread-safe manner and that the Vulkan device is operational.
/// </summary>
internal readonly struct VulkanQueueOperationLease : IDisposable
{
    private readonly object? _gate;

    private VulkanQueueOperationLease(object gate)
        => _gate = gate;

    /// <summary>
    /// Gets a value indicating whether the lease has been successfully acquired.
    /// </summary>
    public bool Acquired => _gate is not null;

    /// <summary>
    /// Attempts to acquire a lease for performing operations on a Vulkan queue. The lease ensures that the queue is accessed in a thread-safe manner and that the Vulkan device is operational.
    /// </summary>
    /// <param name="gate">The synchronization object used to control access to the Vulkan queue.</param>
    /// <param name="deviceState">The state machine representing the Vulkan device's operational state.</param>
    /// <returns>A VulkanQueueOperationLease representing the acquired lease if successful; otherwise, a default lease indicating failure.</returns>
    public static VulkanQueueOperationLease TryEnter(object gate, VulkanDeviceStateMachine deviceState)
    {
        Monitor.Enter(gate);
        if (deviceState.IsOperational)
            return new VulkanQueueOperationLease(gate);

        Monitor.Exit(gate);
        return default;
    }

    /// <summary>
    /// Releases the lease, allowing other threads to acquire the Vulkan queue.
    /// </summary>
    public void Dispose()
    {
        if (_gate is not null)
            Monitor.Exit(_gate);
    }
}
