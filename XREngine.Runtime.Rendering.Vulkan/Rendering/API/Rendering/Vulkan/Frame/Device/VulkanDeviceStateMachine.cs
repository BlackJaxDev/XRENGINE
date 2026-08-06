using System.Threading;

using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents the state machine for managing the lifecycle and fault handling of a Vulkan device.
/// </summary>
internal sealed class VulkanDeviceStateMachine
{
    private int _state = (int)EVulkanDeviceState.Healthy;

    /// <summary>
    /// Gets the current state of the Vulkan device.
    /// </summary>
    public EVulkanDeviceState State =>
        (EVulkanDeviceState)Volatile.Read(ref _state);

    /// <summary>
    /// Gets a value indicating whether the Vulkan device is operational (i.e., in a healthy state).
    /// </summary>
    public bool IsOperational => State == EVulkanDeviceState.Healthy;

    /// <summary>
    /// Attempts to transition the Vulkan device state to begin collecting loss data.
    /// </summary>
    /// <returns>True if the state transition was successful; otherwise, false.</returns>
    public bool TryBeginLossCollection()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)EVulkanDeviceState.LossDetected,
                (int)EVulkanDeviceState.Healthy) != (int)EVulkanDeviceState.Healthy)
            return false;

        return Interlocked.CompareExchange(
                   ref _state,
                   (int)EVulkanDeviceState.CollectingFaultData,
                   (int)EVulkanDeviceState.LossDetected) == (int)EVulkanDeviceState.LossDetected;
    }

    /// <summary>
    /// Completes the collection of loss data and transitions the Vulkan device state to quiesced.
    /// </summary>
    /// <remarks>
    /// If the device has already been disposed, this method will not change the state.
    /// </remarks>
    public void CompleteLossCollection()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state == (int)EVulkanDeviceState.Disposed)
                return;

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)EVulkanDeviceState.Quiesced,
                    state) == state)
                return;
        }
    }

    /// <summary>
    /// Disposes the Vulkan device state machine, transitioning its state to disposed.
    /// </summary>
    public void Dispose()
        => Volatile.Write(ref _state, (int)EVulkanDeviceState.Disposed);
}
