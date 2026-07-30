using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Dense motion result in the engine's unjittered current-minus-previous NDC convention.
/// </summary>
public readonly record struct AdvancedReconstructionMotion(
    Vector2 NdcMotion,
    bool IsValid,
    bool IsReactive);
