namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable diagnostic view of the selected Vulkan output runtime.</summary>
internal readonly record struct VulkanOutputRuntimeSnapshot(
    RenderExecutionMode ExecutionMode,
    string TargetDriverName,
    bool HasExplicitFrameTarget,
    long LastExplicitTargetFrameNumber);
