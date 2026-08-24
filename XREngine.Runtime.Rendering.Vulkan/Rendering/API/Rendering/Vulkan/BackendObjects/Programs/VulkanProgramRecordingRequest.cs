using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable recording boundary supplied by the command authority for one
/// program operation. Program wrappers never retain command or telemetry state.
/// </summary>
internal readonly record struct VulkanProgramRecordingRequest(
    VulkanCommandRuntime Commands,
    CommandBuffer CommandBuffer);
