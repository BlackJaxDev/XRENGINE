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
}
