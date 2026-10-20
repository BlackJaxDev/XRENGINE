namespace XREngine.Rendering;

/// <summary>
/// Cumulative times of the world pre-collect work in the collect phase.
/// </summary>
/// <remarks>
/// An XR runtime can run the world pre-collect work again inside the collect event.
/// Then <see cref="Calls"/> is larger than the collect dispatch count.
/// </remarks>
/// <param name="Calls">The number of world pre-collect calls.</param>
/// <param name="TransformsMilliseconds">The world render-matrix publication and the pending matrix queue.</param>
/// <param name="TransformStorePublishMilliseconds">
/// The transform-store publication, with its pass-gate wait. This time is part of <paramref name="TransformsMilliseconds"/>.
/// </param>
/// <param name="PendingMeshMatricesMilliseconds">The pending renderable-mesh matrix updates.</param>
/// <param name="SceneMilliseconds">The visual-scene refresh.</param>
/// <param name="SceneCalls">The number of visual-scene refreshes in the collect phase, from all callers.</param>
/// <param name="RenderableOperationsMilliseconds">The culling-structure sync and the staged renderable operations.</param>
/// <param name="GpuBoundsEligibilityMilliseconds">The GPU-bounds eligibility refresh of the skinned meshes.</param>
/// <param name="CoveredCommandRefreshMilliseconds">The covered command-source refresh of the skinned meshes.</param>
/// <param name="SkinnedCpuBoundsMilliseconds">The skinned CPU culling-bounds refresh. Only CPU dispatch runs it.</param>
/// <param name="CpuTreeSwapMilliseconds">The CPU culling-tree swap.</param>
/// <param name="SkinnedMeshRows">The skinned meshes that each refresh visits, summed over the scene refreshes.</param>
/// <param name="GpuBoundsEligibleRows">The skinned renderables with full GPU bone coverage, summed over the scene refreshes.</param>
/// <param name="CoveredCommandSourceRows">The covered command sources, summed over the scene refreshes.</param>
public readonly record struct CollectVisiblePreCollectStageTimings(
    long Calls,
    double TransformsMilliseconds,
    double TransformStorePublishMilliseconds,
    double PendingMeshMatricesMilliseconds,
    double SceneMilliseconds,
    long SceneCalls,
    double RenderableOperationsMilliseconds,
    double GpuBoundsEligibilityMilliseconds,
    double CoveredCommandRefreshMilliseconds,
    double SkinnedCpuBoundsMilliseconds,
    double CpuTreeSwapMilliseconds,
    long SkinnedMeshRows,
    long GpuBoundsEligibleRows,
    long CoveredCommandSourceRows);
