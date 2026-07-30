namespace XREngine.Rendering;

/// <summary>
/// Versioned contract shared by every native material reconstruction kernel.
/// The production surface is shader-local and is never a classic GBuffer row.
/// </summary>
public static class AdvancedSurfaceContract
{
    public const uint ContractVersion = 1u;
    public const uint ComputeGroupSizeX = 8u;
    public const uint ComputeGroupSizeY = 8u;
    public const float DegenerateTriangleAreaPixels = 1.0e-6f;
    public const float MinimumClipW = 1.0e-6f;
    public const float MaximumMotionNdc = 2.0f;
    public const string DerivativeMethod =
        "Analytical derivatives of perspective-correct barycentric weights in pixel space.";
    public const string MotionConvention =
        "Unjittered current-minus-previous NDC per view; vendor bridges multiply by 0.5 for normalized UV.";
    public const string NormalMapConvention =
        "MikkTSpace tangent sign with bitangent = cross(shadingNormal, tangent) * handedness on every backend.";
}
