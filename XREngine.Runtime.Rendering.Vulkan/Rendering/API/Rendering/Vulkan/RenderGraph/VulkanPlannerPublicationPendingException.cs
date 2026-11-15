namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Signals that a frame operation names a planner key whose output already has a published
/// planner state, but not yet the state of this key. This happens for one frame after the
/// output changes its passes or its resource generation (for example a debug view change):
/// the operations already carry the new key, and the generation transaction publishes its
/// planner state at the next render. Desktop readiness retries the frame for a bounded number
/// of attempts instead of failing it. A key whose output has no published state at all still
/// fails with the base exception.
/// </summary>
internal sealed class VulkanPlannerPublicationPendingException(string message)
    : VulkanPlanPreconditionException(message);
