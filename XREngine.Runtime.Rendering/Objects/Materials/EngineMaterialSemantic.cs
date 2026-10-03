namespace XREngine.Rendering;

/// <summary>Explicit engine-authored material behavior eligible for cooked shader variants.</summary>
public enum EngineMaterialSemantic
{
    None = 0,
    StandardLitColor = 1,
    OpaqueShadowDepth = 2,
    DebugPoint = 3,
    DebugLine = 4,
    DebugTriangle = 5,
    UIQuadBatched = 6,
    UITextBatchedBitmap = 7,
    OpaquePointShadowDepth = 8,
    SkyboxGradient = 9,
    SkyboxEquirectangular = 10,
    SkyboxOctahedral = 11,
    SkyboxCubemap = 12,
    SkyboxDynamicProcedural = 13,
    OpaqueSpotShadowDepth = 14,
    StandardLitTexture = 15,
    /// <summary>Explicit authored opaque PBR inputs with exact target-cooked shader companions.</summary>
    AuthoredLit = 16,
    /// <summary>Painter-ordered canvas image quads grouped by one sampled texture.</summary>
    UIQuadBatchedTexture = 17,
    /// <summary>Premultiplied linear canvas texture composited onto a scene surface.</summary>
    UICanvasSurface = 18,
    /// <summary>Canonical inverse-hull outline with an explicit modeled Uber surface contract.</summary>
    UberOutline = 19,
}
