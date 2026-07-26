using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents the state machine for managing the lifecycle and fault handling of a Vulkan device.
/// </summary>
internal sealed class VulkanDeviceStateMachine
{
    private int _state = (int)VulkanRenderer.EVulkanDeviceState.Healthy;

    /// <summary>
    /// Gets the current state of the Vulkan device.
    /// </summary>
    public VulkanRenderer.EVulkanDeviceState State =>
        (VulkanRenderer.EVulkanDeviceState)Volatile.Read(ref _state);

    /// <summary>
    /// Gets a value indicating whether the Vulkan device is operational (i.e., in a healthy state).
    /// </summary>
    public bool IsOperational => State == VulkanRenderer.EVulkanDeviceState.Healthy;

    /// <summary>
    /// Attempts to transition the Vulkan device state to begin collecting loss data.
    /// </summary>
    /// <returns>True if the state transition was successful; otherwise, false.</returns>
    public bool TryBeginLossCollection()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)VulkanRenderer.EVulkanDeviceState.LossDetected,
                (int)VulkanRenderer.EVulkanDeviceState.Healthy) != (int)VulkanRenderer.EVulkanDeviceState.Healthy)
            return false;

        Volatile.Write(ref _state, (int)VulkanRenderer.EVulkanDeviceState.CollectingFaultData);
        return true;
    }

    /// <summary>
    /// Completes the collection of loss data and transitions the Vulkan device state to quiesced.
    /// </summary>
    /// <remarks>
    /// If the device has already been disposed, this method will not change the state.
    /// </remarks>
    public void CompleteLossCollection()
    {
        if (State != VulkanRenderer.EVulkanDeviceState.Disposed)
            Volatile.Write(ref _state, (int)VulkanRenderer.EVulkanDeviceState.Quiesced);
    }

    /// <summary>
    /// Disposes the Vulkan device state machine, transitioning its state to disposed.
    /// </summary>
    public void Dispose()
        => Volatile.Write(ref _state, (int)VulkanRenderer.EVulkanDeviceState.Disposed);
}
