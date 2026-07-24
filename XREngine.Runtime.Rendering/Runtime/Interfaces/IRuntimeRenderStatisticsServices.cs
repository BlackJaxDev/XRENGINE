using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Optional allocation-free render statistics and telemetry sink.
/// </summary>
public interface IRuntimeRenderStatisticsServices
{
    /// <summary>
    /// Begins per-frame render statistics tracking for the host.
    /// </summary>
    void BeginRenderStatsFrame();

    /// <summary>
    /// Adds submitted draw calls to the host render-stat counters.
    /// </summary>
    void IncrementRenderDrawCalls(int count);

    /// <summary>
    /// Adds submitted multi-draw calls to the host render-stat counters.
    /// </summary>
    void IncrementRenderMultiDrawCalls(int count);

    /// <summary>
    /// Adds rendered triangles to the host render-stat counters.
    /// </summary>
    void AddRenderTrianglesRendered(int count);

    void AddRenderGpuBufferAllocation(long bytes);
    void RemoveRenderGpuBufferAllocation(long bytes);
    void AddRenderGpuTextureAllocation(long bytes);
    void RemoveRenderGpuTextureAllocation(long bytes);
    void AddRenderGpuRenderBufferAllocation(long bytes);
    void RemoveRenderGpuRenderBufferAllocation(long bytes);
    bool CanAllocateRenderVram(long requestedBytes, long existingAllocationBytes, out long projectedBytes, out long budgetBytes);
    void RecordRenderGpuBufferMapped(int count = 1);
    void RecordRenderGpuReadbackBytes(long bytes);
    void RecordRenderRendererStateCounter(ERendererProfilerCounter counter, long count = 1);
    void RecordRenderMemoryBarrier(EMemoryBarrierMask mask);
    void RecordRenderSceneAssetVisible(
        string? sourceAssetIdentity,
        string? cookedVariantIdentity,
        string? meshName,
        string? materialName,
        int materialSlots,
        int textureCount,
        long triangleCount,
        bool skinned,
        string? representation);
    void RecordRenderTextureUpload(long bytes, TimeSpan elapsed);
    void RecordRenderSkinningUpload(
        long boneMatrixBytes,
        long blendshapeWeightBytes,
        int skinningDispatches = 0,
        int blendshapeDispatches = 0,
        long coreInfluenceBytes = 0,
        long spillHeaderBytes = 0,
        long spillEntryBytes = 0,
        long skinPaletteBytes = 0,
        int skippedSkinningDispatches = 0,
        int reusedSkinnedOutputBuffers = 0,
        int liveSkinningShaderPermutations = 0,
        long blendshapeActiveListUploadBytes = 0,
        long blendshapeDeltaBytes = 0,
        int blendshapeAuthoredShapeCount = 0,
        int blendshapeActiveShapeCount = 0,
        int blendshapeAffectedVertexCount = 0,
        int skippedBlendshapeDispatches = 0,
        int compactedActiveBlendshapeCount = 0,
        int liveBlendshapeShaderPermutations = 0);
    void RecordRenderShaderVariant(bool requested, bool warming, bool linked, bool failed, bool loadedFromDiskCache, bool generatedThisRun);
    void RecordRenderGpuDrivenBucketWork(int activeBuckets, int emptyBucketSkips, int fullBucketScans, int materialScatterDispatches);
    void RecordRenderGpuDrivenCommandCompaction(long culledCommands, long delayedDrawCountValue, long gpuCompactionOverflow, long activeListOverflow, long bucketOverflow, long meshletOverflow);
    void RecordRenderGpuDrivenStageTiming(TimeSpan indirectGeneration, TimeSpan gpuCull, TimeSpan sortCompact);
    void RecordRenderGpuDrivenDelayedDiagnosticReadback(long bytes);
    void RecordRenderGpuDrivenHiZMode(string? mode);
    void RecordRenderGpuDrivenHiZPhase(bool twoPhase, long phaseOneDraws, long phaseTwoDraws);
    void RecordRenderVisibilityBuffer(int passDraws, long classifiedPixels, int activeMaterialTiles, int classificationOverflow, TimeSpan reconstruction, TimeSpan materialShading);
    void RecordRenderRvcFrameCounters(RvcFrameCounters counters)
    {
    }
    void RecordRenderRvcFrameProfile(RvcFrameProfileSnapshot profile)
    {
    }
    void RecordRenderGpuCpuFallback(int eventCount, int recoveredCommands);
    void RecordRenderForbiddenGpuFallback(int eventCount = 1);
    void RecordRenderResourceChurn(string resourceKind, string resourceName, string eventName, string? reason = null);
    void RecordRenderShadowAtlasSolveDiagnostics(ShadowAtlasSolveDiagnostics diagnostics);
    void RecordRenderGpuTransparencyDomainCounts(uint opaqueOrOtherVisible, uint maskedVisible, uint approximateVisible, uint exactVisible);
    void RecordRenderGpuMeshletStrategyRequested(int eventCount = 1);
    void RecordRenderGpuMeshletProductionFrame(int eventCount = 1);
    void RecordRenderGpuMeshletFallback(int eventCount = 1);
    void RecordRenderGpuMeshletDispatchSkipped(int eventCount = 1);
    void RecordRenderGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled);
    void RecordRenderGpuMeshletExpansionOverflow(uint overflowCount);
    void RecordRenderGpuMeshletBufferBytesResident(long bytes);
    void RecordRenderGpuMeshletInstrumentation(uint visibleMeshletCount, uint dispatchedMeshletCount, uint taskRecordOverflowCount, TimeSpan dispatchTime, uint readbackBytes);
    void RecordRenderGpuMeshletCacheHit(int eventCount = 1);
    void RecordRenderGpuMeshletCacheMiss(int eventCount = 1);
    void RecordRenderGpuMeshletCacheStale(int eventCount = 1);
    void RecordRenderOctreeCollect(int visibleRenderables, int emittedCommands);
    void RecordRenderCpuSpatialTreeStats(string mode, SpatialTreeOccupancyStats occupancy, long collectTicks);
    void RecordRenderRtxIoCopyIndirect(long copiedBytes, TimeSpan submissionTime);
    void RecordRenderRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan submissionTime);
    void RecordRenderSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded);
    void RecordRenderSkinnedBoundsRefreshDeferredScheduled();
    void RecordRenderSkinnedBoundsRefreshGpuCompleted(long computeTicks, long applyTicks);
    void RecordRenderVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime);
    void RecordRenderVrPerViewVisibleCounts(uint leftVisible, uint rightVisible);
    void RecordRenderVrRenderSubmitTime(TimeSpan submitTime);
    void RecordRenderVrXrWaitFrameBlockTime(TimeSpan waitTime);
    void RecordRenderVrXrEndFrameSubmitTime(TimeSpan submitTime, ulong renderFrameId = 0UL);
    void RecordRenderVrXrPredictedToLatePoseDelta(double millimeters, double degrees);
    void RecordRenderVrXrPredictedDisplayLeadTime(double leadTimeMs);
    void RecordRenderVrXrMissedDeadlineFrame();
    void RecordRenderVrXrTrackingLossFrame();
    void RecordRenderVrXrRelocatePredictedTime(TimeSpan elapsed);
    void RecordRenderVrXrCollectFrustumExpansionDegrees(double degrees);
    void RecordRenderVrXrPacingThreadIdleTime(TimeSpan elapsed);
    void RecordRenderVrXrPacingHandoffStall();
    void RecordRenderVulkanAdhocBarrier(int emittedCount, int redundantCount);
    void RecordRenderVulkanAllocation(int allocationClass, long bytes);
    void RecordRenderVulkanBarrierPlannerPass(int imageBarrierCount, int bufferBarrierCount, int queueOwnershipTransfers, int stageFlushes);
    void RecordRenderVulkanBindChurn(
        int pipelineBinds = 0,
        int descriptorBinds = 0,
        int pushConstantWrites = 0,
        int vertexBufferBinds = 0,
        int indexBufferBinds = 0,
        int pipelineBindSkips = 0,
        int descriptorBindSkips = 0,
        int vertexBufferBindSkips = 0,
        int indexBufferBindSkips = 0);
    void RecordRenderVulkanDescriptorBindingFailure(
        string? programName,
        string? bindingClass,
        string? bindingName,
        uint set,
        uint binding,
        bool skippedDraw,
        bool skippedDispatch,
        string? message);
    void RecordRenderVulkanDescriptorFallback(
        string? programName,
        string? bindingClass,
        string? bindingName,
        uint set,
        uint binding,
        int count = 1);
    void RecordRenderVulkanDescriptorPoolCreate();
    void RecordRenderVulkanDescriptorPoolDestroy();
    void RecordRenderVulkanDescriptorPoolReset();
    void RecordRenderVulkanResourceLifetimeGauges(int liveResourceCount, int trackedDescriptorSetCount, int pendingRetirementCount, long oldestPendingRetirementAgeMilliseconds);
    void RecordRenderVulkanMeshFrameDataGauges(int arenaChunkCount, long mappedBytes, long reservedBytes, int reservationCount, ulong generation, int recordingLeases, int cachedLeases, int submittedLeases, int activeGenerationCount, int leaseRetainedGenerationCount);
    void RecordRenderVulkanFrameWideMeshFrameDataManifestGauges(ulong generation, long publicationCount, long lateRegistrationCount, int rendererCount, int familyCount, bool isSealed);
    void AdjustRenderVulkanMeshDescriptorOwnership(int allocationVariants, int pools, int allocatedSets, int reservedSets);
    void RecordRenderVulkanDynamicUniformAllocation(long bytes);
    void RecordRenderVulkanDynamicUniformExhaustion();
    void RecordRenderVulkanRecordCommandBufferAllocation(long bytes);
    void RecordRenderVulkanFrameDiagnostics(
        int droppedFrameOps,
        int droppedDrawOps,
        int droppedComputeOps,
        int sceneSwapchainWriters,
        int overlaySwapchainWriters,
        int forcedDiagnosticSwapchainWriters,
        int fboOnlyDrawOps,
        int fboOnlyBlitOps,
        bool missingSceneSwapchainWriters,
        string? firstFailedOpType,
        int firstFailedPassIndex,
        int firstFailedPipelineIdentity,
        int firstFailedViewportIdentity,
        string? firstFailedTargetName,
        string? firstFailedMaterialName,
        string? firstFailedShaderName,
        string? firstFailedMessage,
        string? diagnosticSummary);
    void RecordRenderVulkanFrameGpuCommandBufferTime(TimeSpan elapsed);
    void RecordRenderVulkanFrameLifecycleTiming(
        TimeSpan waitFence,
        TimeSpan acquireImage,
        TimeSpan recordCommandBuffer,
        TimeSpan submit,
        TimeSpan trim,
        TimeSpan present,
        TimeSpan total);
    void RecordRenderVulkanFrameLifecycleDetailTiming(
        TimeSpan sampleTimingQueries,
        TimeSpan drainRetiredResources,
        TimeSpan acquireBridgeSubmit,
        TimeSpan waitSwapchainImage,
        TimeSpan resetDynamicUniformRing,
        TimeSpan snapshotImGuiOverlay,
        TimeSpan recordSceneCommandBuffer,
        TimeSpan recordImGuiOverlay,
        TimeSpan recordDynamicUiTextOverlay);
    void RecordRenderVulkanFrameOpCensus(
        int totalCount,
        int clearCount,
        int meshDrawCount,
        int indirectDrawCount,
        int meshTaskDispatchCount,
        int blitCount,
        int computeCount,
        int swapchainWriteCount,
        int fboWriteCount,
        int uniquePassCount,
        int uniqueContextCount,
        int uniqueTargetCount);
    void RecordRenderVulkanCommandBufferCacheOutcome(
        bool reusedClean,
        bool recorded,
        bool forcedDirty,
        bool frameOpSignatureDirty,
        bool plannerDirty,
        bool profilerDirty,
        string? dirtyReason,
        EVulkanCommandBufferDecisionReason detailReasons,
        ulong structuralSignature,
        ulong descriptorGeneration,
        int swapchainSlot);
    void RecordRenderVulkanCpuStage(EVulkanCpuStage stage, TimeSpan elapsed, long allocatedBytes);
    void RecordRenderVulkanCommandBuffersDirty(string? reason);
    void RecordRenderVulkanExactResourceInvalidation(
        int exactVariantsDirtied,
        int exactCommandChainsDirtied,
        int unrelatedVariantsPreserved,
        int globalFallbackInvalidations)
    {
    }
    void RecordRenderVulkanTrackingBatch(
        int dependencyBinds,
        int uniqueDependencies,
        int imageAccessWrites,
        int compactImageRanges)
    {
    }
    void RecordRenderVulkanDescriptorExpansion(int cacheHits, int cacheMisses)
    {
    }
    void RecordRenderVulkanTrackingContention(int lifetimeLockContentions, int layoutLockContentions)
    {
    }
    void RecordRenderVulkanCommandChainMetrics(
        int chainsScheduled,
        int chainsRecorded,
        int chainsReused,
        int chainsFrameDataRefreshed,
        int volatileChainsRecorded,
        int primaryCommandBuffersReused,
        int primaryCommandBuffersRecorded,
        int visibilityPackets,
        int renderPackets,
        int secondaryCommandBuffers,
        TimeSpan chainWorkerRecordTime,
        TimeSpan renderThreadWaitForWorkersTime,
        string? firstStructuralDirtyReason,
        string? firstDescriptorGenerationMismatch,
        string? firstResourcePlanRevisionMismatch);
    void RecordRenderVulkanGpuDrivenStageTiming(int stage, TimeSpan elapsed);
    void RecordRenderVulkanIndirectBatchMerge(int requestedBatchCount, int mergedBatchCount);
    void RecordRenderVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0u);
    void RecordRenderVulkanIndirectRecordingMode(bool usedSecondary, bool usedParallel, int opCount);
    void RecordRenderVulkanIndirectSubmission(bool usedCountPath, bool usedLoopFallback, int apiCalls, uint submittedDraws);
    void RecordRenderVulkanOomFallback();
    void RecordRenderVulkanPipelineCacheLookup(bool cacheHit);
    void RecordRenderVulkanPipelineCacheMiss(string? summary);
    void RecordRenderVulkanPipelineTelemetry(
        EVulkanPipelineTelemetryEvent eventKind,
        EVulkanDriverPipelineCacheOutcome cacheOutcome,
        bool backgroundCompile,
        double compileMilliseconds,
        int queueDepth,
        int queueCapacity);
    void RecordRenderVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode);
    void RecordRenderVulkanQueueSubmit();
    void RecordRenderVulkanPresentResult(int result, bool accepted);
    void RecordRenderVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount);
    void RecordRenderVulkanSwapchainRetirement(int queued, int drained, int pending, int deferred);
    void RecordRenderVulkanRetiredResourceDrain(
        int descriptorPools,
        int descriptorSets,
        int commandBuffers,
        int queryPools,
        int bufferViews,
        int pipelines,
        int framebuffers,
        int buffers,
        int bufferMemories,
        int images,
        int imageViews,
        int samplers,
        int imageMemories,
        long imageBytes);
    void RecordRenderVulkanValidationMessage(bool isError, string? message);
}
