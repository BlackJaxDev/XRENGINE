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
}
