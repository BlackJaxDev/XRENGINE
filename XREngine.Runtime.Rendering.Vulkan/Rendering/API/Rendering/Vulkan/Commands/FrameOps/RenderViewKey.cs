namespace XREngine.Rendering.Vulkan;

internal readonly record struct RenderViewKey(
    int PipelineIdentity,
    int ViewportIdentity,
    int ViewIndex,
    RenderViewKind Kind,
    int LightIdentity,
    int CascadeIndex);
