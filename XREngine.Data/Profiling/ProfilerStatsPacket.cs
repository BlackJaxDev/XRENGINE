using MemoryPack;

namespace XREngine.Data.Profiling;

/// <summary>
/// Per-frame rendering statistics snapshot.
/// Mirrors Engine.Rendering.Stats.
/// </summary>
[MemoryPackable]
public sealed partial class RenderStatsPacket
{
    // Draw calls
    public int DrawCalls { get; set; }
    public int MultiDrawCalls { get; set; }
    public int TrianglesRendered { get; set; }
    public int GpuCpuFallbackEvents { get; set; }
    public int GpuCpuFallbackRecoveredCommands { get; set; }
    public int ForbiddenGpuFallbackEvents { get; set; }
    public int GpuMappedBuffers { get; set; }
    public long GpuReadbackBytes { get; set; }
    public ShadowAtlasSolveDiagnosticsData ShadowAtlasSolve { get; set; } = new();

    // Rendering profiler schema v2: nested to stay below MemoryPack's per-object member cap.
    public RenderProfilerV2Data RenderProfilerV2 { get; set; } = new();
    public VulkanFrameLoopTelemetryData VulkanFrameLoop { get; set; } = new();
    public FrameLifecycleTelemetryData FrameLifecycle { get; set; } = new();
    public FrameOutputManifestData FrameOutputs { get; set; } = new();

    public int GpuTransparencyOpaqueOrOtherVisible { get; set; }
    public int GpuTransparencyMaskedVisible { get; set; }
    public int GpuTransparencyApproximateVisible { get; set; }
    public int GpuTransparencyExactVisible { get; set; }
    public int GpuMeshletRequestedFrames { get; set; }
    public int GpuMeshletProductionFrames { get; set; }
    public int GpuMeshletFallbackFrames { get; set; }
    public int GpuMeshletDispatchSkipped { get; set; }
    public long GpuMeshletTaskRecordsEmitted { get; set; }
    public long GpuMeshletTaskRecordsFrustumCulled { get; set; }
    public long GpuMeshletTaskRecordsConeCulled { get; set; }
    public long GpuMeshletTaskRecordsHiZCulled { get; set; }
    public long GpuMeshletExpansionOverflowCount { get; set; }
    public long GpuMeshletBufferBytesResident { get; set; }
    public long GpuMeshletLastVisibleMeshletCount { get; set; }
    public long GpuMeshletLastDispatchedMeshletCount { get; set; }
    public long GpuMeshletLastTaskRecordOverflowCount { get; set; }
    public double GpuMeshletLastDispatchMs { get; set; }
    public long GpuMeshletLastReadbackBytes { get; set; }
    public int GpuMeshletCacheHits { get; set; }
    public int GpuMeshletCacheMisses { get; set; }
    public int GpuMeshletCacheStale { get; set; }

    // Vulkan phase-7 telemetry
    public int VulkanPipelineBinds { get; set; }
    public int VulkanDescriptorBinds { get; set; }
    public int VulkanPushConstantWrites { get; set; }
    public int VulkanVertexBufferBinds { get; set; }
    public int VulkanIndexBufferBinds { get; set; }
    public int VulkanPipelineBindSkips { get; set; }
    public int VulkanDescriptorBindSkips { get; set; }
    public int VulkanVertexBufferBindSkips { get; set; }
    public int VulkanIndexBufferBindSkips { get; set; }
    public int VulkanPipelineCacheLookupHits { get; set; }
    public int VulkanPipelineCacheLookupMisses { get; set; }
    public double VulkanPipelineCacheLookupHitRate { get; set; }
    public string VulkanPipelineCacheMissSummary { get; set; } = string.Empty;
    public double VulkanFrameWaitFenceMs { get; set; }
    public double VulkanFrameAcquireImageMs { get; set; }
    public double VulkanFrameRecordCommandBufferMs { get; set; }
    public double VulkanFrameSubmitMs { get; set; }
    public double VulkanFrameTrimMs { get; set; }
    public double VulkanFramePresentMs { get; set; }
    public double VulkanFrameTotalMs { get; set; }
    public double VulkanFrameGpuCommandBufferMs { get; set; }
    public int VulkanDeviceLocalAllocationCount { get; set; }
    public long VulkanDeviceLocalAllocatedBytes { get; set; }
    public int VulkanUploadAllocationCount { get; set; }
    public long VulkanUploadAllocatedBytes { get; set; }
    public int VulkanReadbackAllocationCount { get; set; }
    public long VulkanReadbackAllocatedBytes { get; set; }
    public int VulkanDescriptorPoolCreateCount { get; set; }
    public int VulkanDescriptorPoolDestroyCount { get; set; }
    public int VulkanDescriptorPoolResetCount { get; set; }
    public int VulkanLifetimeLiveResourceCount { get; set; }
    public int VulkanTrackedDescriptorSetCount { get; set; }
    public int VulkanQueueSubmitCount { get; set; }
    public int VulkanDroppedFrameOps { get; set; }
    public int VulkanDroppedDrawOps { get; set; }
    public int VulkanDroppedComputeOps { get; set; }
    public int VulkanSceneSwapchainWriters { get; set; }
    public int VulkanOverlaySwapchainWriters { get; set; }
    public int VulkanForcedDiagnosticSwapchainWriters { get; set; }
    public int VulkanFboOnlyDrawOps { get; set; }
    public int VulkanFboOnlyBlitOps { get; set; }
    public int VulkanMissingSceneSwapchainWriteFrames { get; set; }
    public int VulkanFirstFailedFrameOpPassIndex { get; set; }
    public int VulkanFirstFailedFrameOpPipelineIdentity { get; set; }
    public int VulkanFirstFailedFrameOpViewportIdentity { get; set; }
    public string VulkanFirstFailedFrameOpType { get; set; } = string.Empty;
    public string VulkanFirstFailedFrameOpTargetName { get; set; } = string.Empty;
    public string VulkanFirstFailedFrameOpMaterialName { get; set; } = string.Empty;
    public string VulkanFirstFailedFrameOpShaderName { get; set; } = string.Empty;
    public string VulkanFirstFailedFrameOpMessage { get; set; } = string.Empty;
    public string VulkanFrameDiagnosticSummary { get; set; } = string.Empty;
    public int VulkanValidationMessageCount { get; set; }
    public int VulkanValidationErrorCount { get; set; }
    public string VulkanLastValidationMessage { get; set; } = string.Empty;
    public int VulkanDescriptorFallbackSampledImages { get; set; }
    public int VulkanDescriptorFallbackStorageImages { get; set; }
    public int VulkanDescriptorFallbackUniformBuffers { get; set; }
    public int VulkanDescriptorFallbackStorageBuffers { get; set; }
    public int VulkanDescriptorFallbackTexelBuffers { get; set; }
    public int VulkanDescriptorBindingFailures { get; set; }
    public int VulkanDescriptorSkippedDraws { get; set; }
    public int VulkanDescriptorSkippedDispatches { get; set; }
    public string VulkanDescriptorFallbackSummary { get; set; } = string.Empty;
    public string VulkanDescriptorFailureSummary { get; set; } = string.Empty;
    public int VulkanDynamicUniformAllocations { get; set; }
    public long VulkanDynamicUniformAllocatedBytes { get; set; }
    public int VulkanDynamicUniformExhaustions { get; set; }
    public int VulkanRetiredResourcePlanReplacements { get; set; }
    public int VulkanRetiredResourcePlanImages { get; set; }
    public int VulkanRetiredResourcePlanBuffers { get; set; }

    // VRAM
    public long AllocatedVRAMBytes { get; set; }
    public long AllocatedBufferBytes { get; set; }
    public long AllocatedTextureBytes { get; set; }
    public long AllocatedRenderBufferBytes { get; set; }

    // FBO bandwidth
    public long FBOBandwidthBytes { get; set; }
    public int FBOBindCount { get; set; }
    public int RenderResourceCreatedCount { get; set; }
    public int RenderResourceRecreatedCount { get; set; }
    public int RenderResourceResizedCount { get; set; }
    public int RenderResourceDestroyedCount { get; set; }
    public RenderResourceChurnRowData[] RenderResourceChurnRows { get; set; } = [];

    // OpenXR / VR
    public int VrLeftEyeDraws { get; set; }
    public int VrRightEyeDraws { get; set; }
    public int VrLeftEyeVisible { get; set; }
    public int VrRightEyeVisible { get; set; }
    public double VrLeftWorkerBuildTimeMs { get; set; }
    public double VrRightWorkerBuildTimeMs { get; set; }
    public double VrRenderSubmitTimeMs { get; set; }
    public double VrXrWaitFrameBlockTimeMs { get; set; }
    public double VrXrEndFrameSubmitTimeMs { get; set; }
    public double VrXrPredictedToLatePoseDeltaMillimeters { get; set; }
    public double VrXrPredictedToLatePoseDeltaDegrees { get; set; }
    public double VrXrPredictedDisplayLeadTimeMs { get; set; }
    public int VrXrMissedDeadlineFrames { get; set; }
    public int VrXrTrackingLossFrames { get; set; }
    public double VrXrRelocatePredictedTimeMs { get; set; }
    public double VrXrCollectFrustumExpansionDegrees { get; set; }
    public double VrXrPacingThreadIdleTimeMs { get; set; }
    public int VrXrPacingHandoffStalls { get; set; }

    // Physics-chain telemetry
    public long PhysicsChainCpuUploadBytes { get; set; }
    public long PhysicsChainGpuCopyBytes { get; set; }
    public long PhysicsChainCpuReadbackBytes { get; set; }
    public int PhysicsChainDispatchGroupCount { get; set; }
    public int PhysicsChainDispatchIterationCount { get; set; }
    public long PhysicsChainResidentParticleBytes { get; set; }
    public long PhysicsChainStandaloneCpuUploadBytes { get; set; }
    public long PhysicsChainStandaloneCpuReadbackBytes { get; set; }
    public long PhysicsChainBatchedCpuUploadBytes { get; set; }
    public long PhysicsChainBatchedGpuCopyBytes { get; set; }
    public long PhysicsChainBatchedCpuReadbackBytes { get; set; }
    public double PhysicsChainHierarchyRecalcMilliseconds { get; set; }

    // Render matrix
    public bool RenderMatrixStatsReady { get; set; }
    public int RenderMatrixApplied { get; set; }
    public int RenderMatrixBatchCount { get; set; }
    public int RenderMatrixMaxBatchSize { get; set; }
    public int RenderMatrixSetCalls { get; set; }
    public int RenderMatrixListenerInvocations { get; set; }
    public RenderMatrixListenerEntry[] RenderMatrixListenerCounts { get; set; } = [];

    // Skinned bounds refresh
    public bool SkinnedBoundsStatsReady { get; set; }
    public int SkinnedBoundsDeferredScheduledCount { get; set; }
    public int SkinnedBoundsDeferredCompletedCount { get; set; }
    public int SkinnedBoundsDeferredFailedCount { get; set; }
    public int SkinnedBoundsDeferredInFlightCount { get; set; }
    public int SkinnedBoundsDeferredMaxInFlightCount { get; set; }
    public double SkinnedBoundsDeferredQueueWaitMs { get; set; }
    public double SkinnedBoundsDeferredCpuJobMs { get; set; }
    public double SkinnedBoundsDeferredApplyMs { get; set; }
    public double SkinnedBoundsDeferredMaxQueueWaitMs { get; set; }
    public double SkinnedBoundsDeferredMaxCpuJobMs { get; set; }
    public double SkinnedBoundsDeferredMaxApplyMs { get; set; }
    public int SkinnedBoundsGpuCompletedCount { get; set; }
    public double SkinnedBoundsGpuComputeMs { get; set; }
    public double SkinnedBoundsGpuApplyMs { get; set; }
    public double SkinnedBoundsGpuMaxComputeMs { get; set; }
    public double SkinnedBoundsGpuMaxApplyMs { get; set; }

    // Octree
    public bool OctreeStatsReady { get; set; }
    public int OctreeCollectCallCount { get; set; }
    public int OctreeVisibleRenderableCount { get; set; }
    public int OctreeEmittedCommandCount { get; set; }
    public int OctreeMaxVisibleRenderablesPerCollect { get; set; }
    public int OctreeMaxEmittedCommandsPerCollect { get; set; }
    public int OctreeAddCount { get; set; }
    public int OctreeMoveCount { get; set; }
    public int OctreeRemoveCount { get; set; }
    public int OctreeSkippedMoveCount { get; set; }
    public int OctreeSwapDrainedCommandCount { get; set; }
    public int OctreeSwapBufferedCommandCount { get; set; }
    public int OctreeSwapExecutedCommandCount { get; set; }
    public double OctreeSwapDrainMs { get; set; }
    public double OctreeSwapExecuteMs { get; set; }
    public double OctreeSwapMaxCommandMs { get; set; }
    public string OctreeSwapMaxCommandKind { get; set; } = string.Empty;
    public int OctreeRaycastProcessedCommandCount { get; set; }
    public int OctreeRaycastDroppedCommandCount { get; set; }
    public double OctreeRaycastTraversalMs { get; set; }
    public double OctreeRaycastCallbackMs { get; set; }
    public double OctreeRaycastMaxTraversalMs { get; set; }
    public double OctreeRaycastMaxCallbackMs { get; set; }
    public double OctreeRaycastMaxCommandMs { get; set; }
    public string CpuSpatialTreeMode { get; set; } = string.Empty;
    public int CpuSpatialTreeNodeCount { get; set; }
    public int CpuSpatialTreeItemCount { get; set; }
    public int CpuSpatialTreeRootItemCount { get; set; }
    public int CpuSpatialTreeMaxNodeItemCount { get; set; }
    public int CpuSpatialTreeMaxDepth { get; set; }
    public int CpuSpatialTreeUnboundedItemCount { get; set; }
    public double CpuSpatialTreeCollectMs { get; set; }
    public double CpuSpatialTreeMaxCollectMs { get; set; }

    // GPU render-pipeline command timings
    public bool GpuRenderPipelineProfilingEnabled { get; set; }
    public bool GpuRenderPipelineProfilingSupported { get; set; }
    public bool GpuRenderPipelineTimingsReady { get; set; }
    public string GpuRenderPipelineBackend { get; set; } = string.Empty;
    public string GpuRenderPipelineStatusMessage { get; set; } = string.Empty;
    public double GpuRenderPipelineFrameMs { get; set; }
    public GpuPipelineTimingNodeData[] GpuRenderPipelineTimingRoots { get; set; } = [];
}

[MemoryPackable]
public sealed partial class RenderProfilerV2Data
{
    public int ProfileCaptureSchemaVersion { get; set; } = 4;
    public RenderProfilerRendererStateData RendererState { get; set; } = new();
    public RenderProfilerSceneAssetData SceneAssets { get; set; } = new();
    public RenderProfilerGpuDrivenData GpuDriven { get; set; } = new();
    public RenderProfilerOcclusionData Occlusion { get; set; } = new();
}

[MemoryPackable]
public sealed partial class VulkanFrameLoopTelemetryData
{
    public double FrameSampleTimingQueriesMs { get; set; }
    public double FrameDrainRetiredResourcesMs { get; set; }
    public double FrameAcquireBridgeSubmitMs { get; set; }
    public double FrameWaitSwapchainImageMs { get; set; }
    public double FrameResetDynamicUniformRingMs { get; set; }
    public long RecordCommandBufferAllocatedBytes { get; set; }
    public int FrameOpTotalCount { get; set; }
    public int FrameOpClearCount { get; set; }
    public int FrameOpMeshDrawCount { get; set; }
    public int FrameOpIndirectDrawCount { get; set; }
    public int FrameOpMeshTaskDispatchCount { get; set; }
    public int FrameOpBlitCount { get; set; }
    public int FrameOpComputeCount { get; set; }
    public int FrameOpSwapchainWriteCount { get; set; }
    public int FrameOpFboWriteCount { get; set; }
    public int FrameOpUniquePassCount { get; set; }
    public int FrameOpUniqueContextCount { get; set; }
    public int FrameOpUniqueTargetCount { get; set; }

    public int CommandBufferCleanReuseCount { get; set; }
    public int CommandBufferRecordCount { get; set; }
    public int CommandBufferForcedDirtyCount { get; set; }
    public int CommandBufferFrameOpSignatureDirtyCount { get; set; }
    public int CommandBufferPlannerDirtyCount { get; set; }
    public int CommandBufferProfilerDirtyCount { get; set; }
    public string CommandBufferDirtySummary { get; set; } = string.Empty;

    public int CommandChainsScheduled { get; set; }
    public int CommandChainsRecorded { get; set; }
    public int CommandChainsReused { get; set; }
    public int CommandChainsFrameDataRefreshed { get; set; }
    public int VolatileCommandChainsRecorded { get; set; }
    public int PrimaryCommandBuffersReused { get; set; }
    public int PrimaryCommandBuffersRecorded { get; set; }
    public int VisibilityPacketCount { get; set; }
    public int RenderPacketCount { get; set; }
    public int SecondaryCommandBufferCount { get; set; }
    public double CommandChainWorkerRecordMs { get; set; }
    public double RenderThreadWaitForChainWorkersMs { get; set; }
    public string FirstCommandChainStructuralDirtyReason { get; set; } = string.Empty;
    public string FirstCommandChainDescriptorGenerationMismatch { get; set; } = string.Empty;
    public string FirstCommandChainResourcePlanRevisionMismatch { get; set; } = string.Empty;

    public int RetiredDescriptorPoolCount { get; set; }
    public int RetiredPipelineCount { get; set; }
    public int RetiredFramebufferCount { get; set; }
    public int RetiredBufferCount { get; set; }
    public int RetiredBufferMemoryCount { get; set; }
    public int RetiredImageCount { get; set; }
    public int RetiredImageViewCount { get; set; }
    public int RetiredSamplerCount { get; set; }
    public int RetiredImageMemoryCount { get; set; }
    public long RetiredImageBytes { get; set; }
}

[MemoryPackable]
public sealed partial class FrameLifecycleTelemetryData
{
    public ulong UpdateFrameId { get; set; }
    public ulong CollectFrameId { get; set; }
    public ulong SwapFrameId { get; set; }
    public ulong RenderFrameId { get; set; }
    public ulong PresentFrameId { get; set; }
    public string CollectVisibleLatePolicy { get; set; } = string.Empty;
    public double CollectWaitForRenderMs { get; set; }
    public string CollectWaitReason { get; set; } = string.Empty;
    public double RenderWaitForCollectMs { get; set; }
    public string RenderWaitReason { get; set; } = string.Empty;
    public int SkippedCollectFrames { get; set; }
    public int StaleCollectReuseFrames { get; set; }
}

[MemoryPackable]
public sealed partial class FrameOutputManifestData
{
    public ulong FrameId { get; set; }
    public bool VrActive { get; set; }
    public string MirrorMode { get; set; } = string.Empty;
    public string VisibilityPolicy { get; set; } = string.Empty;
    public string BudgetBand { get; set; } = string.Empty;
    public double BudgetMs { get; set; }
    public double WholeFrameMs { get; set; }
    public double WholeFrameP50Ms { get; set; }
    public double WholeFrameP90Ms { get; set; }
    public double WholeFrameP95Ms { get; set; }
    public double WholeFrameP99Ms { get; set; }
    public double WholeFrameWorstMs { get; set; }
    public ulong WorkloadIdentityHash { get; set; }
    public int OutputRequestCount { get; set; }
    public int UniqueViewFamilyCount { get; set; }
    public int TargetVariantCount { get; set; }
    public int SceneSnapshotCount { get; set; }
    public int VisibilityBuildCount { get; set; }
    public int CompiledPlanCacheHits { get; set; }
    public int CompiledPlanCacheMisses { get; set; }
    public int SharedPassReuseCount { get; set; }
    public int RecordedWorkItemCount { get; set; }
    public int ReusedWorkItemCount { get; set; }
    public int DuplicatedWorkItemCount { get; set; }
    public int CpuBudgetDeferralCount { get; set; }
    public int GpuBudgetDeferralCount { get; set; }
    public int StaleResultReuseCount { get; set; }
    public int MissedDeadlineCount { get; set; }
    public int UnapprovedPolicyEventCount { get; set; }
    public int SubmissionRejectionCount { get; set; }
    public int PlannerPruneCount { get; set; }
    public int GlobalInFlightWaitCount { get; set; }
    public int ForceFlushCount { get; set; }
    public FrameOutputEntryData[] Outputs { get; set; } = [];
}

[MemoryPackable]
public sealed partial class FrameOutputEntryData
{
    public ulong FrameId { get; set; }
    public string OutputKind { get; set; } = string.Empty;
    public string ViewKind { get; set; } = string.Empty;
    public ulong OutputId { get; set; }
    public ulong ViewFamilyId { get; set; }
    public string OutputClass { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string TargetClass { get; set; } = string.Empty;
    public ulong StableTargetId { get; set; }
    public ulong TargetGeneration { get; set; }
    public uint DisplayWidth { get; set; }
    public uint DisplayHeight { get; set; }
    public uint InternalWidth { get; set; }
    public uint InternalHeight { get; set; }
    public ulong TargetCompatibilityKey { get; set; }
    public uint SampleCount { get; set; }
    public uint ViewMask { get; set; }
    public int ExternalImageSlot { get; set; }
    public float DesiredRateHz { get; set; }
    public double DeadlineMs { get; set; }
    public double MaxCpuBudgetMs { get; set; }
    public double MaxGpuBudgetMs { get; set; }
    public uint MaxContentAgeFrames { get; set; }
    public bool HardDeadline { get; set; }
    public string QualityRequirements { get; set; } = string.Empty;
    public string FallbackPolicy { get; set; } = string.Empty;
    public string CompletionRequirement { get; set; } = string.Empty;
    public ulong ProducerDependencySetId { get; set; }
    public ulong ConsumerDependencySetId { get; set; }
    public string WorkDisposition { get; set; } = string.Empty;
    public uint ContentAgeFrames { get; set; }
    public bool DeadlineMissed { get; set; }
    public bool PolicyAuthorized { get; set; }
    public string PolicyReason { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public bool Rendered { get; set; }
    public bool SceneRendered { get; set; }
    public bool Mirror { get; set; }
    public bool SeparateSceneRender { get; set; }
    public bool SharedVisibility { get; set; }
    public bool Due { get; set; }
    public bool Skipped { get; set; }
    public bool CadenceSkipped { get; set; }
    public bool AutoSkipped { get; set; }
    public string SkipReason { get; set; } = string.Empty;
    public float ConfiguredTargetRateHz { get; set; }
    public float SourceRateHz { get; set; }
    public double AchievedRateHz { get; set; }
    public int TotalRenderCount { get; set; }
    public int TotalSkipCount { get; set; }
    public int CommandCount { get; set; }
    public int DrawCalls { get; set; }
    public int MultiDrawCalls { get; set; }
    public int Triangles { get; set; }
    public double CollectCpuMs { get; set; }
    public double SwapCpuMs { get; set; }
    public double RenderCpuMs { get; set; }
    public double SubmitCpuMs { get; set; }
    public double OverlayCpuMs { get; set; }
    public double PresentCpuMs { get; set; }
    public double GpuMs { get; set; }
}

[MemoryPackable]
public sealed partial class ShadowAtlasSolveDiagnosticsData
{
    public double ElapsedMilliseconds { get; set; }
    public int ClassifiedRequestCount { get; set; }
    public int DirectionalRequestCount { get; set; }
    public int SpotRequestCount { get; set; }
    public int PointRequestCount { get; set; }
    public int DepthRequestCount { get; set; }
    public int Variance2RequestCount { get; set; }
    public int ExponentialVariance2RequestCount { get; set; }
    public int ExponentialVariance4RequestCount { get; set; }
    public int BalancedSolveAttemptCount { get; set; }
    public int FailedCandidateCount { get; set; }
    public int DemotionCount { get; set; }
    public int StickyDemotionCount { get; set; }
    public int DirectionalGroupDemotionCount { get; set; }
    public int DeterministicFallbackDemotionCount { get; set; }
    public int PriorReserveHitCount { get; set; }
    public int PriorReserveMissCount { get; set; }
    public int PriorSubBlockHitCount { get; set; }
    public int PriorSubBlockMissCount { get; set; }
    public int PageAllocationAttemptCount { get; set; }
    public int PageAllocationSuccessCount { get; set; }
    public int PageCreateAttemptCount { get; set; }
    public int PageCreateSuccessCount { get; set; }
    public int PageClearCount { get; set; }
    public int DirectionalGroupSeedCount { get; set; }
    public int DirectionalGroupMemberCount { get; set; }
    public int DirectionalGroupCoLocationFailureCount { get; set; }
    public int PointGroupSeedCount { get; set; }
    public int PointGroupMemberCount { get; set; }
    public int PointGroupCoLocationFailureCount { get; set; }
    public int IncrementalReuseCount { get; set; }
    public int WaterlineDemotionCount { get; set; }
    public string LastFailureReason { get; set; } = string.Empty;
}

[MemoryPackable]
public sealed partial class RenderResourceChurnRowData
{
    public string ResourceKind { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int Count { get; set; }
}

[MemoryPackable]
public sealed partial class RenderProfilerRendererStateData
{
    public int IndirectCountCalls { get; set; }
    public int ShaderProgramSwitches { get; set; }
    public int ProgramPipelineSwitches { get; set; }
    public int VaoBinds { get; set; }
    public int VaoBindSkips { get; set; }
    public int ArrayBufferBinds { get; set; }
    public int ElementArrayBufferBinds { get; set; }
    public int DrawIndirectBufferBinds { get; set; }
    public int ParameterBufferBinds { get; set; }
    public int SsboBinds { get; set; }
    public int UboBinds { get; set; }
    public int TextureBinds { get; set; }
    public int TextureBindSkips { get; set; }
    public int TextureUnitSwitches { get; set; }
    public int UniformCalls { get; set; }
    public int SamplerUniformCalls { get; set; }
    public long BufferUploadBytes { get; set; }
    public int BarrierCalls { get; set; }
    public int BarrierAll { get; set; }
    public int BarrierCommand { get; set; }
    public int BarrierBufferUpdate { get; set; }
    public int BarrierShaderStorage { get; set; }
    public int BarrierTextureFetch { get; set; }
    public int BarrierTextureUpdate { get; set; }
    public int BarrierFramebuffer { get; set; }
    public int TimestampQueryCount { get; set; }
    public long TimestampQueryReadbackBytes { get; set; }
    public int TimestampDenseModeFrames { get; set; }
    public int RedundantStateSkips { get; set; }
    public int CpuDirectDrawCalls { get; set; }
    public int GpuIndirectDrawCalls { get; set; }
    public int GpuMeshletDrawCalls { get; set; }
    public int UnknownStrategyDrawCalls { get; set; }
    public int DirectionalCascadeStaleSampled { get; set; }
    public int DirectionalCascadeMixedGenerationPrevented { get; set; }
    public int DirectionalCascadePhysicalReprojected { get; set; }
    public int DirectionalCascadeForcedFreshRender { get; set; }
    public string ActiveTextureBindingRung { get; set; } = string.Empty;
    public string ActiveStereoMode { get; set; } = string.Empty;
    public string ActiveSubmissionStrategy { get; set; } = string.Empty;
    public string ActiveRenderBackend { get; set; } = string.Empty;
    public bool ValidationLayersEnabled { get; set; }
    public bool DebugOutputEnabled { get; set; }
    public bool GpuTimestampsDenseMode { get; set; }
}

[MemoryPackable]
public sealed partial class RenderProfilerOcclusionData
{
    public string EffectiveMode { get; set; } = string.Empty;
    public string SubmissionStrategy { get; set; } = string.Empty;
    public int CpuPassesActive { get; set; }
    public int CpuPassesSkippedNoCamera { get; set; }
    public int CpuPassesSkippedShadow { get; set; }
    public int CpuPassesSkippedDepthNormalPrePass { get; set; }
    public int CpuPassesSkippedModeOff { get; set; }
    public int CpuTested { get; set; }
    public int CpuCulled { get; set; }
    public int CpuRendered { get; set; }
    public int CpuDecisionSeed { get; set; }
    public int CpuDecisionCached { get; set; }
    public int CpuDecisionVisibleQuery { get; set; }
    public int CpuDecisionVisibleHysteresis { get; set; }
    public int CpuDecisionProbe { get; set; }
    public int CpuDecisionSkip { get; set; }
    public int CpuDecisionForcedVisible { get; set; }
    public string CpuMotionTier { get; set; } = string.Empty;
    public string CpuActiveViewScope { get; set; } = string.Empty;
    public int CpuGlobalConservativeFrames { get; set; }
    public int CpuPendingQueries { get; set; }
    public long CpuQuerySubmittedTotal { get; set; }
    public long CpuQueryResolvedTotal { get; set; }
    public int CpuQueryLatencySamples { get; set; }
    public double CpuQueryLatencyAverageFrames { get; set; }
    public int CpuQueryLatencyMaxFrames { get; set; }
    public long CpuBudgetSkippedTotal { get; set; }
    public long CpuForcedVisibleTotal { get; set; }
    public int CpuUnsupportedStereoQueryMode { get; set; }
    public int CpuQueryAsyncSubmitted { get; set; }
    public int CpuQueryAsyncResolved { get; set; }
    public int CpuQueryAsyncOccluded { get; set; }
    public int CpuSocTested { get; set; }
    public int CpuSocCulled { get; set; }
}

[MemoryPackable]
public sealed partial class RenderProfilerSceneAssetData
{
    public int VisibleRendererCount { get; set; }
    public int VisibleSubmeshCount { get; set; }
    public long VisibleTriangleCount { get; set; }
    public int MaterialSlotCount { get; set; }
    public int ActiveMaterialCount { get; set; }
    public int TextureCount { get; set; }
    public long ResidentTextureMemoryBytes { get; set; }
    public int TextureUploadJobs { get; set; }
    public long TextureUploadBytes { get; set; }
    public double TextureUploadMs { get; set; }
    public int ShaderVariantsRequested { get; set; }
    public int ShaderVariantsWarming { get; set; }
    public int ShaderVariantsLinked { get; set; }
    public int ShaderVariantsFailed { get; set; }
    public int ShaderVariantsLoadedFromDiskCache { get; set; }
    public int ShaderVariantsGeneratedThisRun { get; set; }
    public int SkinnedRendererCount { get; set; }
    public long BoneMatrixUploadBytes { get; set; }
    public long BlendshapeWeightUploadBytes { get; set; }
    public long BlendshapeActiveListUploadBytes { get; set; }
    public long BlendshapeDeltaBytes { get; set; }
    public long SkinningCoreInfluenceBytes { get; set; }
    public long SkinningSpillHeaderBytes { get; set; }
    public long SkinningSpillEntryBytes { get; set; }
    public long SkinPaletteUploadBytes { get; set; }
    public int SkinningComputeDispatchCount { get; set; }
    public int BlendshapeComputeDispatchCount { get; set; }
    public int SkippedSkinningComputeDispatchCount { get; set; }
    public int SkippedBlendshapeComputeDispatchCount { get; set; }
    public int ReusedSkinnedOutputBufferCount { get; set; }
    public int LiveSkinningShaderPermutationCount { get; set; }
    public int BlendshapeAuthoredShapeCount { get; set; }
    public int BlendshapeActiveShapeCount { get; set; }
    public int BlendshapeAffectedVertexCount { get; set; }
    public int CompactedActiveBlendshapeCount { get; set; }
    public int LiveBlendshapeShaderPermutationCount { get; set; }
    public int AvatarSourceMeshCount { get; set; }
    public int AvatarOptimizedLodCount { get; set; }
    public int AvatarMeshletCount { get; set; }
    public int AvatarVisibilityBufferCount { get; set; }
    public int AvatarClusterVirtualizedCount { get; set; }
    public int AvatarOctahedralImpostorCount { get; set; }
    public int AvatarGaussianSplatCount { get; set; }
    public RenderAssetCostRowData[] RenderAssetCostRows { get; set; } = [];
}

[MemoryPackable]
public sealed partial class RenderProfilerGpuDrivenData
{
    public long GpuDrivenCulledCommandCount { get; set; }
    public int GpuDrivenActiveBucketCount { get; set; }
    public int GpuDrivenEmptyBucketSkips { get; set; }
    public int GpuDrivenFullBucketScans { get; set; }
    public int GpuDrivenMaterialScatterDispatches { get; set; }
    public double GpuDrivenIndirectCommandGenerationMs { get; set; }
    public double GpuDrivenGpuCullMs { get; set; }
    public double GpuDrivenGpuSortCompactMs { get; set; }
    public long GpuDrivenDelayedDrawCountBufferValue { get; set; }
    public long GpuDrivenDelayedDiagnosticReadbackBytes { get; set; }
    public int GpuDrivenDelayedDiagnosticReadbackCount { get; set; }
    public long GpuCompactionOverflow { get; set; }
    public long GpuActiveListOverflow { get; set; }
    public long GpuBucketOverflow { get; set; }
    public long GpuMeshletOverflow { get; set; }
    public string GpuHiZMode { get; set; } = string.Empty;
    public int GpuHiZOnePhaseFrames { get; set; }
    public int GpuHiZTwoPhaseFrames { get; set; }
    public long GpuHiZPhaseOneDraws { get; set; }
    public long GpuHiZPhaseTwoDraws { get; set; }
    public int VisibilityPassDraws { get; set; }
    public long VisibilityClassifiedPixels { get; set; }
    public int VisibilityActiveMaterialTiles { get; set; }
    public int VisibilityClassificationOverflow { get; set; }
    public double VisibilityReconstructionMs { get; set; }
    public double VisibilityMaterialShadingMs { get; set; }
}

[MemoryPackable]
public sealed partial class GpuPipelineTimingNodeData
{
    public string Name { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }
    public int SampleCount { get; set; }
    public GpuPipelineTimingNodeData[] Children { get; set; } = [];
}

[MemoryPackable]
public sealed partial class RenderAssetCostRowData
{
    public string SourceAssetIdentity { get; set; } = string.Empty;
    public string CookedVariantIdentity { get; set; } = string.Empty;
    public string MeshName { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string Representation { get; set; } = string.Empty;
    public int DrawCalls { get; set; }
    public long Triangles { get; set; }
    public int MaterialSlots { get; set; }
    public int TextureCount { get; set; }
    public int SkinnedDraws { get; set; }
}

/// <summary>
/// A single render-matrix listener type and its invocation count.
/// Replaces KeyValuePair&lt;string, int&gt; for MemoryPack compatibility.
/// </summary>
[MemoryPackable]
public sealed partial class RenderMatrixListenerEntry
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// Per-thread GC allocation tracking snapshot.
/// Mirrors Engine.ThreadAllocationSnapshot.
/// </summary>
[MemoryPackable]
public sealed partial class ThreadAllocationsPacket
{
    public AllocationSlice Render { get; set; } = new();
    public AllocationSlice CollectSwap { get; set; } = new();
    public AllocationSlice Update { get; set; } = new();
    public AllocationSlice FixedUpdate { get; set; } = new();
    public AllocationScopeSlice[] Scopes { get; set; } = [];
}

/// <summary>
/// Snapshot of one allocation tracking ring buffer.
/// Mirrors Engine.AllocationRingSnapshot.
/// </summary>
[MemoryPackable]
public sealed partial class AllocationSlice
{
    public long LastBytes { get; set; }
    public double AverageBytes { get; set; }
    public long MaxBytes { get; set; }
    public int Samples { get; set; }
    public int Capacity { get; set; }

    public double LastKB => LastBytes / 1024.0;
    public double AverageKB => AverageBytes / 1024.0;
    public double MaxKB => MaxBytes / 1024.0;
}

/// <summary>
/// Snapshot of a named allocation tracking scope.
/// </summary>
[MemoryPackable]
public sealed partial class AllocationScopeSlice
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public long BudgetBytes { get; set; }
    public long LastBytes { get; set; }
    public double AverageBytes { get; set; }
    public long MaxBytes { get; set; }
    public int Samples { get; set; }
    public int Capacity { get; set; }
    public long OverBudgetCount { get; set; }

    public double LastKB => LastBytes / 1024.0;
    public double AverageKB => AverageBytes / 1024.0;
    public double MaxKB => MaxBytes / 1024.0;
    public double BudgetKB => BudgetBytes / 1024.0;
}

/// <summary>
/// GPU BVH profiling metrics snapshot.
/// Mirrors BvhGpuProfiler.Metrics.
/// </summary>
[MemoryPackable]
public sealed partial class BvhMetricsPacket
{
    public uint BuildCount { get; set; }
    public double BuildMilliseconds { get; set; }
    public uint RefitCount { get; set; }
    public double RefitMilliseconds { get; set; }
    public uint CullCount { get; set; }
    public double CullMilliseconds { get; set; }
    public uint RaycastCount { get; set; }
    public double RaycastMilliseconds { get; set; }
}

/// <summary>
/// Job system stats snapshot.
/// Mirrors Engine.Jobs (JobManager) state.
/// </summary>
[MemoryPackable]
public sealed partial class JobSystemStatsPacket
{
    public int WorkerCount { get; set; }
    public bool IsQueueBounded { get; set; }
    public int QueueCapacity { get; set; }
    public int QueueSlotsInUse { get; set; }
    public int QueueSlotsAvailable { get; set; }
    public JobPriorityStatsEntry[] Priorities { get; set; } = [];
}

/// <summary>
/// Per-priority job queue stats.
/// </summary>
[MemoryPackable]
public sealed partial class JobPriorityStatsEntry
{
    public int Priority { get; set; }
    public string PriorityName { get; set; } = string.Empty;
    public int QueuedAny { get; set; }
    public int QueuedMain { get; set; }
    public int QueuedCollect { get; set; }
    public double AvgWaitMs { get; set; }
}

/// <summary>
/// Main thread invoke log entries (sent as a delta or full snapshot).
/// </summary>
[MemoryPackable]
public sealed partial class MainThreadInvokesPacket
{
    public MainThreadInvokeEntryData[] Entries { get; set; } = [];
}

/// <summary>
/// A single main-thread invoke log entry.
/// Mirrors Engine.MainThreadInvokeEntry.
/// </summary>
[MemoryPackable]
public sealed partial class MainThreadInvokeEntryData
{
    public long Sequence { get; set; }
    public long TimestampTicks { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int CallerThreadId { get; set; }
}

/// <summary>
/// Heartbeat sent at ~1 Hz to indicate the engine process is alive.
/// </summary>
[MemoryPackable]
public sealed partial class HeartbeatPacket
{
    public string ProcessName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public long UptimeMs { get; set; }
}
