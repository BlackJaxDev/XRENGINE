using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// World-space geometric and MikkTSpace shading frame for one surface.
/// </summary>
public readonly record struct AdvancedReconstructionTangentFrame(
    Vector3 GeometricNormal,
    Vector3 ShadingNormal,
    Vector3 Tangent,
    Vector3 Bitangent,
    float Handedness,
    bool MirroredTransform);
