namespace XREngine.Rendering;

/// <summary>
/// Versioned identity of engine-authored material behavior. A material without this
/// identity must use its authored shaders, regardless of parameter or shader names.
/// </summary>
public readonly record struct EngineMaterialSemanticIdentity(EngineMaterialSemantic Semantic, int Version)
{
    public static EngineMaterialSemanticIdentity None => default;

    public static EngineMaterialSemanticIdentity StandardLitColorV1 => new(EngineMaterialSemantic.StandardLitColor, 1);

    /// <summary>Opaque deferred PBR textures with UV0, RGB normals and red-channel scalar maps.</summary>
    public static EngineMaterialSemanticIdentity StandardLitTextureV1 => new(EngineMaterialSemantic.StandardLitTexture, 1);

    /// <summary>Lit color with explicit uniform-alpha coverage and sorted blending.</summary>
    public static EngineMaterialSemanticIdentity StandardLitColorV2 => new(EngineMaterialSemantic.StandardLitColor, 2);

    public static EngineMaterialSemanticIdentity OpaqueShadowDepthV1 => new(EngineMaterialSemantic.OpaqueShadowDepth, 1);

    /// <summary>Radial point-light distance with independent per-face raster depth.</summary>
    public static EngineMaterialSemanticIdentity OpaquePointShadowDepthV1 => new(EngineMaterialSemantic.OpaquePointShadowDepth, 1);

    /// <summary>Projected spot-light depth in the light's authored color storage.</summary>
    public static EngineMaterialSemanticIdentity OpaqueSpotShadowDepthV1 => new(EngineMaterialSemantic.OpaqueSpotShadowDepth, 1);

    public static EngineMaterialSemanticIdentity DebugPointV1 => new(EngineMaterialSemantic.DebugPoint, 1);

    public static EngineMaterialSemanticIdentity DebugLineV1 => new(EngineMaterialSemantic.DebugLine, 1);

    public static EngineMaterialSemanticIdentity DebugTriangleV1 => new(EngineMaterialSemantic.DebugTriangle, 1);

    public static EngineMaterialSemanticIdentity UIQuadBatchedV1 => new(EngineMaterialSemantic.UIQuadBatched, 1);

    public static EngineMaterialSemanticIdentity UITextBatchedBitmapV1 => new(EngineMaterialSemantic.UITextBatchedBitmap, 1);

    public static EngineMaterialSemanticIdentity SkyboxGradientV1 => new(EngineMaterialSemantic.SkyboxGradient, 1);
    public static EngineMaterialSemanticIdentity SkyboxEquirectangularV1 => new(EngineMaterialSemantic.SkyboxEquirectangular, 1);
    public static EngineMaterialSemanticIdentity SkyboxOctahedralV1 => new(EngineMaterialSemantic.SkyboxOctahedral, 1);
    public static EngineMaterialSemanticIdentity SkyboxCubemapV1 => new(EngineMaterialSemantic.SkyboxCubemap, 1);
    public static EngineMaterialSemanticIdentity SkyboxDynamicProceduralV1 => new(EngineMaterialSemantic.SkyboxDynamicProcedural, 1);

    /// <summary>Exact built-in sky behavior with authored parameters published by SkyboxComponent.</summary>
    public bool IsSkybox() => Version == 1 && Semantic is EngineMaterialSemantic.SkyboxGradient or
        EngineMaterialSemantic.SkyboxEquirectangular or EngineMaterialSemantic.SkyboxOctahedral or
        EngineMaterialSemantic.SkyboxCubemap or EngineMaterialSemantic.SkyboxDynamicProcedural;

    /// <summary>Rejects unknown semantics and revisions before they can select cooked code.</summary>
    public void Validate()
    {
        if (Semantic == EngineMaterialSemantic.None && Version == 0)
            return;
        if (Semantic == EngineMaterialSemantic.StandardLitColor && Version is 1 or 2)
            return;
        if (Semantic == EngineMaterialSemantic.StandardLitTexture && Version == 1)
            return;
        if (Semantic == EngineMaterialSemantic.OpaqueShadowDepth && Version == 1)
            return;
        if (Semantic == EngineMaterialSemantic.OpaquePointShadowDepth && Version == 1)
            return;
        if (Semantic == EngineMaterialSemantic.OpaqueSpotShadowDepth && Version == 1)
            return;
        if (Semantic is EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle && Version == 1)
            return;
        if (Semantic is EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UITextBatchedBitmap && Version == 1)
            return;
        if (IsSkybox())
            return;
        throw new ArgumentException($"Unsupported engine material semantic '{Semantic}' version {Version}.");
    }
}
