namespace XREngine.Rendering.Vulkan;

internal readonly record struct ProgramImageBinding(
    XRTexture Texture,
    int Level,
    bool Layered,
    int Layer,
    XRRenderProgram.EImageAccess Access,
    XRRenderProgram.EImageFormat Format);
