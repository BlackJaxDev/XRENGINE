using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Captures whether a desktop present reached the Vulkan queue independently
/// from the result value and auxiliary Streamline/PCL failures.
/// </summary>
internal readonly struct VulkanDesktopPresentDispatchOutcome
{
    public VulkanDesktopPresentDispatchOutcome(
        Result result,
        bool dispatched,
        Exception? auxiliaryFailure)
    {
        Result = result;
        Dispatched = dispatched;
        AuxiliaryFailure = auxiliaryFailure;
    }

    public Result Result { get; }
    public bool Dispatched { get; }
    public Exception? AuxiliaryFailure { get; }
}
