namespace XREngine.Rendering.Vulkan;

internal enum RenderPacketVolatility
{
    StaticStructural,
    FrameDataOnly,
    DynamicCommand,
    StructuralDirty,
}
