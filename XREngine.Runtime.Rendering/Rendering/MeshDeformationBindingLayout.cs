namespace XREngine.Rendering;

/// <summary>
/// Stable graphics-program SSBO bindings for renderer-local deformed vertex streams.
/// </summary>
/// <remarks>
/// OpenGL shares SSBO binding points across every stage in a linked graphics program.
/// Keep these slots above the engine-global material, batching, lighting, and shadow
/// bindings (currently 0-38) so a fragment-stage resource cannot replace a vertex
/// deformation stream between binding and draw submission.
/// </remarks>
public static class MeshDeformationBindingLayout
{
    public const uint ComputeInterleaved = 39u;
    public const uint ComputePosition = 40u;
    public const uint ComputeNormal = 41u;
    public const uint ComputeTangent = 42u;
    public const uint PrecombinedBlendshapePosition = 43u;
    public const uint PrecombinedBlendshapeNormal = 44u;
    public const uint PrecombinedBlendshapeTangent = 45u;
}
