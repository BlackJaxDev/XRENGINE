using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Signals that command encoding observed a planner context other than the one
/// sealed for the current recording attempt. The partial command buffer must be
/// abandoned and must never be submitted.
/// </summary>
internal sealed class VulkanPlanPreconditionException(string message)
    : InvalidOperationException(message);
