namespace XREngine.Rendering;

/// <summary>Explicit engine-authored material behavior eligible for cooked shader variants.</summary>
public enum EngineMaterialSemantic
{
    None = 0,
    StandardLitColor = 1,
    OpaqueShadowDepth = 2,
}
