namespace XREngine.Rendering;

/// <summary>
/// Cumulative times of the serial swap. <see cref="CollectVisibleSwapLightStageTimings"/> splits the light swap.
/// </summary>
/// <param name="JobsMilliseconds">The frame-swap jobs.</param>
/// <param name="WorldCalls">The number of world swaps in the world swap event.</param>
/// <param name="WorldMilliseconds">The world swap event.</param>
/// <param name="TransformsMilliseconds">The world render-matrix publication and the pending matrix queue.</param>
/// <param name="TransformStorePublishMilliseconds">
/// The transform-store publication, with its pass-gate wait. This time is part of <paramref name="TransformsMilliseconds"/>.
/// </param>
/// <param name="PendingMeshMatricesMilliseconds">The pending renderable-mesh matrix updates.</param>
/// <param name="LightsMilliseconds">The light swap.</param>
/// <param name="AdvancedGlobalCaptureMilliseconds">The Advanced global resource capture.</param>
/// <param name="SceneMilliseconds">The visual-scene swap.</param>
/// <param name="CoveredSourcesMilliseconds">The covered command-source publication in the visual-scene swap.</param>
/// <param name="CpuTreeMilliseconds">The CPU culling-tree swap in the visual-scene swap.</param>
/// <param name="SceneCommandsMilliseconds">The GPU-scene command buffer swap, with its lock wait.</param>
/// <param name="AdvancedPublicationMilliseconds">
/// The Advanced resident-scene publication. This time is part of <paramref name="SceneCommandsMilliseconds"/>.
/// </param>
/// <param name="AdvancedPublication">
/// The split of <paramref name="AdvancedPublicationMilliseconds"/> at the unchanged-row gate decision, with the gate results.
/// </param>
/// <param name="ViewportsMilliseconds">The viewport and window swap event.</param>
/// <param name="MainViewSwapCalls">The number of automatic viewport command swaps whose scene output is due.</param>
/// <param name="MainViewSwapMilliseconds">
/// The automatic viewport command swaps whose scene output is due. This time is part of <paramref name="ViewportsMilliseconds"/>.
/// </param>
public readonly record struct CollectVisibleSwapStageTimings(
    double JobsMilliseconds,
    long WorldCalls,
    double WorldMilliseconds,
    double TransformsMilliseconds,
    double TransformStorePublishMilliseconds,
    double PendingMeshMatricesMilliseconds,
    double LightsMilliseconds,
    double AdvancedGlobalCaptureMilliseconds,
    double SceneMilliseconds,
    double CoveredSourcesMilliseconds,
    double CpuTreeMilliseconds,
    double SceneCommandsMilliseconds,
    double AdvancedPublicationMilliseconds,
    CollectVisibleSwapAdvancedPublicationTimings AdvancedPublication,
    double ViewportsMilliseconds,
    long MainViewSwapCalls,
    double MainViewSwapMilliseconds);
