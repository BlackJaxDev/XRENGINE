namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents one coherent observation of the desktop Vulkan frame activity
/// publication.
/// </summary>
internal readonly record struct DesktopFrameActivitySnapshot(
    bool IsActive,
    ulong FrameNumber,
    int FrameSlot);
