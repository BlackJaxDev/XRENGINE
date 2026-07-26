namespace XREngine.Rendering;

/// <summary>
/// Declares which authored surface rules a companion pass must share with the
/// base color pass.
/// </summary>
[Flags]
public enum EMaterialPassCoverageRules
{
    None = 0,
    Alpha = 1 << 0,
    Dissolve = 1 << 1,
    UvDiscard = 1 << 2,
    VertexDeformation = 1 << 3,
    Culling = 1 << 4,
    All = Alpha | Dissolve | UvDiscard | VertexDeformation | Culling,
}
