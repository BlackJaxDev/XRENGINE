namespace XREngine.Rendering.Vulkan;

internal readonly record struct VisibilityPacket(
    RenderViewKey ViewKey,
    ulong SceneRevision,
    ulong CameraRevision,
    ReadOnlyMemory<int> RenderableIds,
    ulong StructuralSignature,
    ulong FrameDataSignature);
