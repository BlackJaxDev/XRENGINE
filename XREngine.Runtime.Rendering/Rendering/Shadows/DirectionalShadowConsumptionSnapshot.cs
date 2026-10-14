namespace XREngine.Rendering.Shadows;

/// <summary>
/// Cumulative counts of how the directional lights collect, swap, submit and render their primary shadow view and
/// their cascades. <see cref="DirectionalShadowConsumptionTelemetry.Capture"/> returns it.
/// </summary>
/// <remarks>
/// Capture twice around a timed window and use the differences. Divide by the difference of
/// <paramref name="ShadowCollectPasses"/> to get values for each frame. The primary collection time is in the primary
/// shadow view of <see cref="CollectVisibleStageTimings"/>.
/// </remarks>
/// <param name="ShadowCollectPasses">The calls of the light collection that plans the shadow atlas and collects the shadow views.</param>
/// <param name="LightCollectCalls">
/// The directional light collections that pass the active and shadow-caster checks. This count includes the
/// render-thread collections of the render paths that collect again.
/// </param>
/// <param name="PrimaryCollections">The primary shadow view collections.</param>
/// <param name="PrimarySwaps">The primary shadow view swaps.</param>
/// <param name="PrimarySwapMilliseconds">The time of the primary shadow view swaps.</param>
/// <param name="PrimarySwappedMeshCommands">The mesh commands in the rendering buffer of the primary shadow view after each swap.</param>
/// <param name="AtlasPrimaryDecisions">The primary submission decisions of the directional shadow atlas planning.</param>
/// <param name="NeedsPrimaryViewportTrue">
/// The atlas planning viewport tests that returned true. The planning runs the test only when cascaded shadows are
/// enabled and published.
/// </param>
/// <param name="NeedsPrimaryViewportNoRelevantViewport">
/// The part of <paramref name="NeedsPrimaryViewportTrue"/> where no window or VR eye viewport with a camera targeted the world.
/// </param>
/// <param name="NeedsPrimaryViewportFalse">The atlas planning viewport tests that returned false.</param>
/// <param name="AtlasPrimarySubmitted">The primary atlas requests that the shadow atlas accepted.</param>
/// <param name="AtlasPrimaryDirty">The primary atlas requests that were dirty or could not reuse the previous frame.</param>
/// <param name="PrimaryTileRenderAttempts">The calls of the primary atlas tile render from the shadow atlas tile render.</param>
/// <param name="PrimaryTileCollectNow">The primary atlas tile renders that collected and swapped the light again on the render thread.</param>
/// <param name="PrimaryTileRenders">The primary atlas tile renders that succeeded.</param>
/// <param name="PrimaryPackageRenderReads">
/// The renders of the primary shadow view from the atlas tile and the legacy primary paths. Only these renders read the
/// published backend-ready frame package of the primary shadow view on the render thread.
/// </param>
/// <param name="Need">The first term that made the primary shadow view necessary at each light collection.</param>
/// <param name="Legacy">The legacy dedicated shadow map and cascade texture array renders.</param>
/// <param name="Cascades">The cascade collection outcomes and the cascade atlas renders.</param>
public readonly record struct DirectionalShadowConsumptionSnapshot(
    long ShadowCollectPasses,
    long LightCollectCalls,
    long PrimaryCollections,
    long PrimarySwaps,
    double PrimarySwapMilliseconds,
    long PrimarySwappedMeshCommands,
    long AtlasPrimaryDecisions,
    long NeedsPrimaryViewportTrue,
    long NeedsPrimaryViewportNoRelevantViewport,
    long NeedsPrimaryViewportFalse,
    long AtlasPrimarySubmitted,
    long AtlasPrimaryDirty,
    long PrimaryTileRenderAttempts,
    long PrimaryTileCollectNow,
    long PrimaryTileRenders,
    long PrimaryPackageRenderReads,
    DirectionalPrimaryShadowNeedCounts Need,
    DirectionalLegacyShadowRenderCounts Legacy,
    DirectionalCascadeConsumptionCounts Cascades);
