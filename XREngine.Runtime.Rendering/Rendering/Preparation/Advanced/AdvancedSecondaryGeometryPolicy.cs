using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Shared deformation/culling and material-evaluation policy for a secondary
/// geometry consumer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedSecondaryGeometryPolicy(
    EAdvancedPreparationConsumer Consumer,
    bool ReuseAggregateDeformation,
    bool ReusePrimaryRelevance,
    bool RequiresIndependentFrustum,
    bool EvaluateCoverageMaterial,
    bool EvaluateDisplacementMaterial,
    EAdvancedCapturePreviousDataPolicy PreviousDataPolicy);
