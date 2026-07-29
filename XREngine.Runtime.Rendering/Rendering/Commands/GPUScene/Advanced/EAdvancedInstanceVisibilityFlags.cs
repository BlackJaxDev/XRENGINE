namespace XREngine.Rendering.Commands;

[Flags]
public enum EAdvancedInstanceVisibilityFlags : uint
{
    None = 0u,
    Enabled = 1u << 0,
    CastsShadows = 1u << 1,
    ReceivesShadows = 1u << 2,
    FrustumVisible = 1u << 3,
    OcclusionVisible = 1u << 4,
    EditorVisible = 1u << 5,
}
