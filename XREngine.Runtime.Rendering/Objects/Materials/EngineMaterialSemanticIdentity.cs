namespace XREngine.Rendering;

/// <summary>
/// Versioned identity of engine-authored material behavior. A material without this
/// identity must use its authored shaders, regardless of parameter or shader names.
/// </summary>
public readonly record struct EngineMaterialSemanticIdentity(EngineMaterialSemantic Semantic, int Version)
{
    public static EngineMaterialSemanticIdentity None => default;

    public static EngineMaterialSemanticIdentity StandardLitColorV1 => new(EngineMaterialSemantic.StandardLitColor, 1);

    /// <summary>Lit color with explicit uniform-alpha coverage and sorted blending.</summary>
    public static EngineMaterialSemanticIdentity StandardLitColorV2 => new(EngineMaterialSemantic.StandardLitColor, 2);

    public static EngineMaterialSemanticIdentity OpaqueShadowDepthV1 => new(EngineMaterialSemantic.OpaqueShadowDepth, 1);

    public static EngineMaterialSemanticIdentity DebugPointV1 => new(EngineMaterialSemantic.DebugPoint, 1);

    public static EngineMaterialSemanticIdentity DebugLineV1 => new(EngineMaterialSemantic.DebugLine, 1);

    public static EngineMaterialSemanticIdentity DebugTriangleV1 => new(EngineMaterialSemantic.DebugTriangle, 1);

    public static EngineMaterialSemanticIdentity UIQuadBatchedV1 => new(EngineMaterialSemantic.UIQuadBatched, 1);

    public static EngineMaterialSemanticIdentity UITextBatchedBitmapV1 => new(EngineMaterialSemantic.UITextBatchedBitmap, 1);

    /// <summary>Rejects unknown semantics and revisions before they can select cooked code.</summary>
    public void Validate()
    {
        if (Semantic == EngineMaterialSemantic.None && Version == 0)
            return;
        if (Semantic == EngineMaterialSemantic.StandardLitColor && Version is 1 or 2)
            return;
        if (Semantic == EngineMaterialSemantic.OpaqueShadowDepth && Version == 1)
            return;
        if (Semantic is EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle && Version == 1)
            return;
        if (Semantic is EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UITextBatchedBitmap && Version == 1)
            return;
        throw new ArgumentException($"Unsupported engine material semantic '{Semantic}' version {Version}.");
    }
}
