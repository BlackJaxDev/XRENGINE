namespace XREngine.Rendering;

/// <summary>Frozen authored behavior identity carried in the final word of a canonical material header.</summary>
public enum EAdvancedMaterialSourceContract : uint
{
    Unclassified,
    StandardSurface,
    AuthoredStandardSurface,
    ProjectiveMirror,
    CustomVertexProgram,
    CustomSurfaceProgram,
    UnsupportedTextureSemantics,
    EngineGeneratedSurface,
    TessellatedVertexProgram,
    TopologyChangingProgram,
    /// <summary>Exact canonical Uber base shader with a separately versioned browser surface companion.</summary>
    UberBaseSurface,
}
