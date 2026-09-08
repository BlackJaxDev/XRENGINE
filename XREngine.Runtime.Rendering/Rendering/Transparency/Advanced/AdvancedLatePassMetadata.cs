using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Operational metadata for late and post-visibility draw operations.
/// </summary>
public sealed record AdvancedLatePassMetadata
{
    public EAdvancedLatePassKind Kind { get; init; }
    public bool RequiresSceneColorSnapshot { get; init; }
    public string SceneColorSamplerName { get; init; }
    public int SceneColorTextureUnit { get; init; }
    public bool ParticipatesInMotionVectors { get; init; }
    public XRMaterial? TemporalVelocityMaterial { get; init; }
    public XRMaterial? TemporalReactiveMaskMaterial { get; init; }
    /// <summary>Requires a rigid, single-instance, non-billboard temporal replay.</summary>
    public bool RequiresRigidTemporalGeometry { get; init; }
    /// <summary>Optional source revision for variants whose coverage is tied to an unchanged shader.</summary>
    public long? TemporalSourceShaderRevision { get; init; }
    /// <summary>Shared coverage parameter whose identity must survive source parameter edits.</summary>
    public ShaderVar? TemporalCoverageParameter { get; init; }
    public bool WritesDepth { get; init; }
    public bool IsOrderDependent { get; init; }
    /// <summary>Blocks the material's visible late-pass lane.</summary>
    public string? UnsupportedReason { get; init; }
    /// <summary>
    /// Explains why temporal replay is unavailable without suppressing the
    /// material's visible late-pass draw.
    /// </summary>
    public string? TemporalUnsupportedReason { get; init; }

    public AdvancedLatePassMetadata(
        EAdvancedLatePassKind kind,
        bool requiresSceneColorSnapshot = false,
        string sceneColorSamplerName = AdvancedSceneColorContract.SceneColorSamplerName,
        int sceneColorTextureUnit = 0,
        bool participatesInMotionVectors = false,
        bool writesDepth = false,
        bool isOrderDependent = false,
        string? unsupportedReason = null)
    {
        Kind = kind;
        RequiresSceneColorSnapshot = requiresSceneColorSnapshot;
        SceneColorSamplerName = sceneColorSamplerName;
        SceneColorTextureUnit = sceneColorTextureUnit;
        ParticipatesInMotionVectors = participatesInMotionVectors;
        WritesDepth = writesDepth;
        IsOrderDependent = isOrderDependent;
        UnsupportedReason = unsupportedReason;
    }
}
