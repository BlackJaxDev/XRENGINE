using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct IndexedViewportScissorSnapshot(
    Viewport[]? Viewports,
    Rect2D[]? Scissors,
    uint Count);
