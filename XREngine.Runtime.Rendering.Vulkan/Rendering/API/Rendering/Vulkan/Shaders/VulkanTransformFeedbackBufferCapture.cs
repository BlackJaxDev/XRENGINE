using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed record VulkanTransformFeedbackBufferCapture(
    uint Binding,
    EFeedbackType Type,
    IReadOnlyList<string> Names);
