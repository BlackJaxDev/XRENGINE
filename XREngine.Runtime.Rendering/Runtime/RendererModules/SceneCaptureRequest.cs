namespace XREngine.Rendering;

/// <summary>An independently owned scene output and its immutable layer selection.</summary>
public sealed record SceneCaptureRequest(
    XRViewport Viewport,
    XRFrameBuffer Target,
    XRTexture2DArray Color,
    int ArrayLayer,
    IRuntimeRenderWorld World,
    XRCamera Camera,
    Func<bool> IsCurrent,
    Func<bool>? IsPrepared = null,
    float? FrozenElapsedTime = null,
    SceneCaptureLightingSnapshot? Lighting = null,
    bool NormalizeAtlasOrigin = false);
