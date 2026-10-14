namespace XREngine.Rendering;

/// <summary>
/// Identifies one timed stage of the collect phase or of the serial swap.
/// <see cref="CollectVisibleStageTimings"/> keeps a tick sum and a call count for each stage.
/// </summary>
/// <remarks>
/// Each stage nests inside the stage that the summary names as its parent.
/// The parent time minus the sum of its children is the work that no child measures.
/// </remarks>
internal enum ECollectVisibleStage
{
    /// <summary>The full collect dispatch. It has the same boundaries as the collect-phase total of the frame lifecycle.</summary>
    CollectTotal = 0,

    /// <summary>The pre-collect event. Parent: <see cref="CollectTotal"/>.</summary>
    PreCollectEvent,

    /// <summary>The collect event. Parent: <see cref="CollectTotal"/>.</summary>
    CollectEvent,

    /// <summary>The post-collect event. Parent: <see cref="CollectTotal"/>.</summary>
    PostCollectEvent,

    /// <summary>The world render-matrix publication and the pending matrix queue before collection.</summary>
    PreCollectTransforms,

    /// <summary>The transform-store publication, with its pass-gate wait. Parent: <see cref="PreCollectTransforms"/>.</summary>
    PreCollectTransformStorePublish,

    /// <summary>The pending renderable-mesh matrix updates before collection.</summary>
    PreCollectPendingMeshMatrices,

    /// <summary>The visual-scene refresh before collection.</summary>
    PreCollectScene,

    /// <summary>The culling-structure sync and the staged renderable operations. Parent: <see cref="PreCollectScene"/>.</summary>
    SceneRenderableOperations,

    /// <summary>The GPU-bounds eligibility refresh of the skinned meshes. Parent: <see cref="PreCollectScene"/>.</summary>
    SceneGpuBoundsEligibility,

    /// <summary>The covered command-source refresh of the skinned meshes. Parent: <see cref="PreCollectScene"/>.</summary>
    SceneCoveredCommandRefresh,

    /// <summary>The skinned CPU culling-bounds refresh in CPU dispatch. Parent: <see cref="PreCollectScene"/>.</summary>
    SceneSkinnedCpuBounds,

    /// <summary>The CPU culling-tree swap of the scene refresh. Parent: <see cref="PreCollectScene"/>.</summary>
    SceneCpuTreeSwap,

    /// <summary>The shadow planning of the light collection. Parent: <see cref="CollectEvent"/>.</summary>
    LightsPlanning,

    /// <summary>The shadow view collection of each light. Parent: <see cref="CollectEvent"/>.</summary>
    LightsShadowCollect,

    /// <summary>The full serial swap. It has the same boundaries as the swap-phase total of the frame lifecycle.</summary>
    SwapTotal,

    /// <summary>The frame-swap jobs. Parent: <see cref="SwapTotal"/>.</summary>
    SwapJobs,

    /// <summary>The world swap event. Parent: <see cref="SwapTotal"/>.</summary>
    SwapWorld,

    /// <summary>The viewport and window swap event. Parent: <see cref="SwapTotal"/>.</summary>
    SwapViewports,

    /// <summary>The command swap of each automatic viewport whose scene output is due. Parent: <see cref="SwapViewports"/>.</summary>
    MainViewSwap,

    /// <summary>The world render-matrix publication and the pending matrix queue. Parent: <see cref="SwapWorld"/>.</summary>
    SwapTransforms,

    /// <summary>The transform-store publication, with its pass-gate wait. Parent: <see cref="SwapTransforms"/>.</summary>
    SwapTransformStorePublish,

    /// <summary>The pending renderable-mesh matrix updates. Parent: <see cref="SwapWorld"/>.</summary>
    SwapPendingMeshMatrices,

    /// <summary>The light swap. Parent: <see cref="SwapWorld"/>.</summary>
    SwapLights,

    /// <summary>The directional shadow view swaps. Parent: <see cref="SwapLights"/>.</summary>
    SwapLightsDirectional,

    /// <summary>The spot and point shadow view swaps. Parent: <see cref="SwapLights"/>.</summary>
    SwapLightsLocal,

    /// <summary>The shadow atlas frame publication. Parent: <see cref="SwapLights"/>.</summary>
    SwapLightsAtlasPublication,

    /// <summary>The scene-capture swaps. Parent: <see cref="SwapLights"/>.</summary>
    SwapLightsCaptures,

    /// <summary>The Advanced global resource capture. Parent: <see cref="SwapWorld"/>.</summary>
    SwapAdvancedGlobalCapture,

    /// <summary>The visual-scene swap. Parent: <see cref="SwapWorld"/>.</summary>
    SwapScene,

    /// <summary>The covered command-source publication. Parent: <see cref="SwapScene"/>.</summary>
    SwapCoveredSources,

    /// <summary>The CPU culling-tree swap. Parent: <see cref="SwapScene"/>.</summary>
    SwapCpuTree,

    /// <summary>The GPU-scene command buffer swap. Parent: <see cref="SwapScene"/>.</summary>
    SwapSceneCommands,

    /// <summary>The Advanced resident-scene publication. Parent: <see cref="SwapSceneCommands"/>.</summary>
    SwapAdvancedPublication,

    /// <summary>
    /// The unchanged-row gate of the Advanced resident-scene publication. The gate does not run when the
    /// resident tables grow at the frame boundary. Parent: <see cref="SwapAdvancedPublication"/>.
    /// </summary>
    SwapAdvancedRowGate,

    /// <summary>
    /// The Advanced resident-scene publication after the gate decision: the unchanged-row publication or the full
    /// plan with its commit, and the scratch release. When the gate does not run, this stage starts at the same
    /// point. Parent: <see cref="SwapAdvancedPublication"/>.
    /// </summary>
    /// <remarks>
    /// The boundary capacity check and the tombstone reclaim come before the gate decision. Their time stays in
    /// the parent residual.
    /// </remarks>
    SwapAdvancedCommit,

    /// <summary>The number of stages. This value is not a stage.</summary>
    Count,
}
