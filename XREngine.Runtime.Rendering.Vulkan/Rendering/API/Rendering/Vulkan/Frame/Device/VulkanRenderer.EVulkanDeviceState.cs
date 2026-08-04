namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal enum EVulkanDeviceState : byte
    {
        /// <summary>
        /// The device is functioning normally.
        /// </summary>
        Healthy,
        /// <summary>
        /// The device has encountered a loss.
        /// </summary>
        LossDetected,
        /// <summary>
        /// The device is currently collecting fault data after a loss has been detected.
        /// </summary>
        CollectingFaultData,
        /// <summary>
        /// The device has been quiesced after a loss has been detected and fault data has been collected.
        /// </summary>
        Quiesced,
        /// <summary>
        /// The device has been disposed.
        /// </summary>
        Disposed,
    }
}
