namespace XREngine.Rendering;

/// <summary>
/// Diagnostic-only reconstruction output selected before resource generation.
/// </summary>
public enum EAdvancedReconstructionDebugView : uint
{
    Disabled = 0u,
    WorldPosition,
    GeometricNormal,
    ShadingNormal,
    Tangent,
    Bitangent,
    TexCoord0,
    TexCoord1,
    VertexColor,
    Motion,
    Validity,
    Material,
    View,
    DerivativeDx,
    DerivativeDy,
    DerivativeError,
    SelectedMip,
}
