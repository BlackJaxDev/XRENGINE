namespace XREngine.Rendering.Shadows;

/// <summary>
/// Identifies one cumulative counter of <see cref="DirectionalShadowConsumptionTelemetry"/>.
/// </summary>
internal enum EDirectionalShadowConsumptionCounter
{
    /// <summary>The calls of the light collection that plans the shadow atlas and collects the shadow views.</summary>
    ShadowCollectPasses,

    /// <summary>The directional light collections that pass the active and shadow-caster checks.</summary>
    LightCollectCalls,

    /// <summary>The primary shadow view collections.</summary>
    PrimaryCollections,

    /// <summary>The primary shadow view swaps.</summary>
    PrimarySwaps,

    /// <summary>The stopwatch ticks of the primary shadow view swaps.</summary>
    PrimarySwapTicks,

    /// <summary>The mesh commands in the rendering buffer of the primary shadow view after each swap.</summary>
    PrimarySwappedMeshCommands,

    /// <summary>The primary collections that no term made necessary.</summary>
    NeedNone,

    /// <summary>The primary collection tests where cascaded shadows were disabled.</summary>
    NeedCascadesDisabled,

    /// <summary>The primary collection tests where no cascade source had a receiver texture.</summary>
    NeedNoCascadeReceiver,

    /// <summary>The primary collection tests where no cascade source had published cascades.</summary>
    NeedNoPublishedCascades,

    /// <summary>The primary collection tests where the light had no render world.</summary>
    NeedNoRenderWorld,

    /// <summary>The primary collection tests where a world viewport preferred the primary shadow map.</summary>
    NeedPrimaryViewport,

    /// <summary>The primary collection tests where no viewport with a camera targeted the world.</summary>
    NeedNoRelevantViewport,

    /// <summary>The primary submission decisions of the directional shadow atlas planning.</summary>
    AtlasPrimaryDecisions,

    /// <summary>The atlas planning viewport tests that returned true.</summary>
    NeedsPrimaryViewportTrue,

    /// <summary>The atlas planning viewport tests that returned true because no viewport targeted the world.</summary>
    NeedsPrimaryViewportNoRelevantViewport,

    /// <summary>The atlas planning viewport tests that returned false.</summary>
    NeedsPrimaryViewportFalse,

    /// <summary>The primary atlas requests that the shadow atlas accepted.</summary>
    AtlasPrimarySubmitted,

    /// <summary>The primary atlas requests that were dirty or could not reuse the previous frame.</summary>
    AtlasPrimaryDirty,

    /// <summary>The calls of the primary atlas tile render.</summary>
    PrimaryTileRenderAttempts,

    /// <summary>The primary atlas tile renders that collected and swapped the light again before they rendered.</summary>
    PrimaryTileCollectNow,

    /// <summary>The primary atlas tile renders that succeeded.</summary>
    PrimaryTileRenders,

    /// <summary>The renders of the primary shadow view, which read its published backend-ready frame package.</summary>
    PrimaryPackageRenderReads,

    /// <summary>The legacy directional shadow render requests.</summary>
    LegacyRenderCalls,

    /// <summary>The legacy directional shadow renders that collected and swapped the light again before they rendered.</summary>
    LegacyCollectNow,

    /// <summary>The legacy primary shadow renders that started.</summary>
    LegacyPrimaryRenderAttempts,

    /// <summary>The legacy primary shadow renders that succeeded.</summary>
    LegacyPrimaryRenders,

    /// <summary>The legacy cascade renders of one cascade source with at least one cascade.</summary>
    LegacyCascadeRenderCalls,

    /// <summary>The cascade source collections.</summary>
    CascadeSourceCollectCalls,

    /// <summary>The cascade source collections that stopped because the backend cannot render cascades.</summary>
    CascadeSkipDisabled,

    /// <summary>The cascade source collections that stopped because the source had no published cascade.</summary>
    CascadeSkipNoCascades,

    /// <summary>The cascade source collections that stopped because the atlas requested no cascade render.</summary>
    CascadeSkipNoAtlasRenderRequest,

    /// <summary>The cascade source collections that prepared GPU shadow frame packages instead of a CPU collection.</summary>
    CascadeGpuPackages,

    /// <summary>The grouped cascade view collections.</summary>
    CascadeGroupCollections,

    /// <summary>The grouped cascade collections that stopped because the atlas requested no collection.</summary>
    CascadeGroupSkipNoCollectionRequest,

    /// <summary>The single cascade view collections.</summary>
    CascadeViewportCollections,

    /// <summary>The cascade atlas tile renders that succeeded.</summary>
    CascadeTileRenders,

    /// <summary>The grouped cascade atlas renders that succeeded.</summary>
    CascadeGroupRenders,

    /// <summary>The cascade atlas renders that collected and swapped the light again before they rendered.</summary>
    CascadeRenderCollectNow,

    /// <summary>The number of counters. This member is not a counter.</summary>
    Count,
}
