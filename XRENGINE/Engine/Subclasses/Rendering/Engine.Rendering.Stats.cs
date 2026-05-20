using System.Collections.Generic;
using System;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Timers;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Contains rendering statistics tracked per frame.
            /// </summary>
            public static partial class Stats
            {
                private static int _drawCalls;
                private static int _trianglesRendered;
                private static int _multiDrawCalls;
                private static int _lastFrameDrawCalls;
                private static int _lastFrameTrianglesRendered;
                private static int _lastFrameMultiDrawCalls;

                /// The number of draw calls in the last completed frame.
                /// </summary>
                public static int DrawCalls => _lastFrameDrawCalls;

                /// <summary>
                /// The number of triangles rendered in the last completed frame.
                /// </summary>
                public static int TrianglesRendered => _lastFrameTrianglesRendered;

                /// <summary>
                /// The number of multi-draw indirect calls in the last completed frame.
                /// </summary>
                public static int MultiDrawCalls => _lastFrameMultiDrawCalls;
                /// <summary>
                /// When false, disables all per-frame statistics tracking to reduce overhead.
                /// VRAM tracking remains enabled as it's not per-frame.
                /// </summary>
                public static bool EnableTracking { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif


                /// <summary>
                /// Call this at the start of each frame to reset the counters.
                /// </summary>
                public static void BeginFrame()
                {
                    bool gpuPipelineProfilingEnabled = EnableTracking && Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;
                    RenderPipelineGpuProfiler.Instance.BeginFrame(State.RenderFrameId, gpuPipelineProfilingEnabled);

                    // Notify GPU dispatch logger of new frame for logging context
                    GpuDispatchLogger.BeginFrame();

                    // Snapshot & reset per-frame occlusion observability counters.
                    XREngine.Rendering.Occlusion.OcclusionTelemetry.BeginFrame();
                    
                    _lastFrameDrawCalls = _drawCalls;
                    _lastFrameTrianglesRendered = _trianglesRendered;
                    _lastFrameMultiDrawCalls = _multiDrawCalls;
                    _lastFrameGpuCpuFallbackEvents = _gpuCpuFallbackEvents;
                    _lastFrameGpuCpuFallbackRecoveredCommands = _gpuCpuFallbackRecoveredCommands;
                    _lastFrameForbiddenGpuFallbackEvents = _forbiddenGpuFallbackEvents;
                    _lastFrameGpuTransparencyOpaqueOrOtherVisible = _gpuTransparencyOpaqueOrOtherVisible;
                    _lastFrameGpuTransparencyMaskedVisible = _gpuTransparencyMaskedVisible;
                    _lastFrameGpuTransparencyApproximateVisible = _gpuTransparencyApproximateVisible;
                    _lastFrameGpuTransparencyExactVisible = _gpuTransparencyExactVisible;
                    _lastFrameGpuMeshletRequestedFrames = _gpuMeshletRequestedFrames;
                    _lastFrameGpuMeshletProductionFrames = _gpuMeshletProductionFrames;
                    _lastFrameGpuMeshletFallbackFrames = _gpuMeshletFallbackFrames;
                    _lastFrameGpuMeshletDispatchSkipped = _gpuMeshletDispatchSkipped;
                    _lastFrameGpuMeshletTaskRecordsEmitted = _gpuMeshletTaskRecordsEmitted;
                    _lastFrameGpuMeshletTaskRecordsFrustumCulled = _gpuMeshletTaskRecordsFrustumCulled;
                    _lastFrameGpuMeshletTaskRecordsConeCulled = _gpuMeshletTaskRecordsConeCulled;
                    _lastFrameGpuMeshletTaskRecordsHiZCulled = _gpuMeshletTaskRecordsHiZCulled;
                    _lastFrameGpuMeshletExpansionOverflowCount = _gpuMeshletExpansionOverflowCount;
                    _lastFrameGpuMeshletBufferBytesResident = _gpuMeshletBufferBytesResident;
                    _lastFrameGpuMeshletCacheHits = _gpuMeshletCacheHits;
                    _lastFrameGpuMeshletCacheMisses = _gpuMeshletCacheMisses;
                    _lastFrameGpuMeshletCacheStale = _gpuMeshletCacheStale;
                    _lastFrameGpuMappedBuffers = _gpuMappedBuffers;
                    _lastFrameGpuReadbackBytes = _gpuReadbackBytes;
                    _lastFrameRtxIoDecompressCalls = _rtxIoDecompressCalls;
                    _lastFrameRtxIoCopyIndirectCalls = _rtxIoCopyIndirectCalls;
                    _lastFrameRtxIoCompressedBytes = _rtxIoCompressedBytes;
                    _lastFrameRtxIoDecompressedBytes = _rtxIoDecompressedBytes;
                    _lastFrameRtxIoCopyBytes = _rtxIoCopyBytes;
                    _lastFrameRtxIoSubmissionTimeTicks = _rtxIoSubmissionTimeTicks;
                    _lastFrameVulkanIndirectCountPathCalls = _vulkanIndirectCountPathCalls;
                    _lastFrameVulkanIndirectNonCountPathCalls = _vulkanIndirectNonCountPathCalls;
                    _lastFrameVulkanIndirectLoopFallbackCalls = _vulkanIndirectLoopFallbackCalls;
                    _lastFrameVulkanIndirectApiCalls = _vulkanIndirectApiCalls;
                    _lastFrameVulkanIndirectSubmittedDraws = _vulkanIndirectSubmittedDraws;
                    _lastFrameVulkanIndirectRequestedBatches = _vulkanIndirectRequestedBatches;
                    _lastFrameVulkanIndirectMergedBatches = _vulkanIndirectMergedBatches;
                    _lastFrameVulkanIndirectPrimaryRecordOps = _vulkanIndirectPrimaryRecordOps;
                    _lastFrameVulkanIndirectSecondaryRecordOps = _vulkanIndirectSecondaryRecordOps;
                    _lastFrameVulkanIndirectParallelSecondaryRecordOps = _vulkanIndirectParallelSecondaryRecordOps;
                    _lastFrameVulkanPlannedImageBarriers = _vulkanPlannedImageBarriers;
                    _lastFrameVulkanPlannedBufferBarriers = _vulkanPlannedBufferBarriers;
                    _lastFrameVulkanQueueOwnershipTransfers = _vulkanQueueOwnershipTransfers;
                    _lastFrameVulkanBarrierStageFlushes = _vulkanBarrierStageFlushes;
                    _lastFrameVulkanOverlapCandidatePasses = _vulkanOverlapCandidatePasses;
                    _lastFrameVulkanOverlapTransferCosts = _vulkanOverlapTransferCosts;
                    _lastFrameVulkanOverlapFrameDeltaMicros = _vulkanOverlapFrameDeltaMicros;
                    _lastFrameVulkanOverlapModePromotions = _vulkanOverlapModePromotions;
                    _lastFrameVulkanOverlapModeDemotions = _vulkanOverlapModeDemotions;
                    _lastFrameVulkanAdhocBarrierEmits = _vulkanAdhocBarrierEmits;
                    _lastFrameVulkanAdhocBarrierRedundant = _vulkanAdhocBarrierRedundant;
                    _lastFrameVulkanPipelineBinds = _vulkanPipelineBinds;
                    _lastFrameVulkanDescriptorBinds = _vulkanDescriptorBinds;
                    _lastFrameVulkanPushConstantWrites = _vulkanPushConstantWrites;
                    _lastFrameVulkanVertexBufferBinds = _vulkanVertexBufferBinds;
                    _lastFrameVulkanIndexBufferBinds = _vulkanIndexBufferBinds;
                    _lastFrameVulkanPipelineBindSkips = _vulkanPipelineBindSkips;
                    _lastFrameVulkanDescriptorBindSkips = _vulkanDescriptorBindSkips;
                    _lastFrameVulkanVertexBufferBindSkips = _vulkanVertexBufferBindSkips;
                    _lastFrameVulkanIndexBufferBindSkips = _vulkanIndexBufferBindSkips;
                    _lastFrameVulkanPipelineCacheLookupHits = _vulkanPipelineCacheLookupHits;
                    _lastFrameVulkanPipelineCacheLookupMisses = _vulkanPipelineCacheLookupMisses;
                    _lastFrameVulkanRequestedDraws = _vulkanRequestedDraws;
                    _lastFrameVulkanCulledDraws = _vulkanCulledDraws;
                    _lastFrameVulkanEmittedIndirectDraws = _vulkanEmittedIndirectDraws;
                    _lastFrameVulkanConsumedDraws = _vulkanConsumedDraws;
                    _lastFrameVulkanOverflowCount = _vulkanOverflowCount;
                    _lastFrameVulkanStageResetTicks = _vulkanStageResetTicks;
                    _lastFrameVulkanStageCullTicks = _vulkanStageCullTicks;
                    _lastFrameVulkanStageOcclusionTicks = _vulkanStageOcclusionTicks;
                    _lastFrameVulkanStageIndirectTicks = _vulkanStageIndirectTicks;
                    _lastFrameVulkanStageDrawTicks = _vulkanStageDrawTicks;
                    _lastFrameVulkanFrameWaitFenceTicks = _vulkanFrameWaitFenceTicks;
                    _lastFrameVulkanFrameAcquireImageTicks = _vulkanFrameAcquireImageTicks;
                    _lastFrameVulkanFrameRecordCommandBufferTicks = _vulkanFrameRecordCommandBufferTicks;
                    _lastFrameVulkanFrameSubmitTicks = _vulkanFrameSubmitTicks;
                    _lastFrameVulkanFrameTrimTicks = _vulkanFrameTrimTicks;
                    _lastFrameVulkanFramePresentTicks = _vulkanFramePresentTicks;
                    _lastFrameVulkanFrameTotalTicks = _vulkanFrameTotalTicks;
                    _lastFrameVulkanFrameGpuCommandBufferTicks = _vulkanFrameGpuCommandBufferTicks;
                    _lastFrameVulkanDeviceLocalAllocationCount = _vulkanDeviceLocalAllocationCount;
                    _lastFrameVulkanDeviceLocalAllocatedBytes = _vulkanDeviceLocalAllocatedBytes;
                    _lastFrameVulkanUploadAllocationCount = _vulkanUploadAllocationCount;
                    _lastFrameVulkanUploadAllocatedBytes = _vulkanUploadAllocatedBytes;
                    _lastFrameVulkanReadbackAllocationCount = _vulkanReadbackAllocationCount;
                    _lastFrameVulkanReadbackAllocatedBytes = _vulkanReadbackAllocatedBytes;
                    _lastFrameVulkanDescriptorPoolCreateCount = _vulkanDescriptorPoolCreateCount;
                    _lastFrameVulkanDescriptorPoolDestroyCount = _vulkanDescriptorPoolDestroyCount;
                    _lastFrameVulkanDescriptorPoolResetCount = _vulkanDescriptorPoolResetCount;
                    _lastFrameVulkanQueueSubmitCount = _vulkanQueueSubmitCount;
                    _lastFrameVulkanOomFallbackCount = _vulkanOomFallbackCount;
                    _lastFrameVulkanDroppedFrameOps = _vulkanDroppedFrameOps;
                    _lastFrameVulkanDroppedDrawOps = _vulkanDroppedDrawOps;
                    _lastFrameVulkanDroppedComputeOps = _vulkanDroppedComputeOps;
                    _lastFrameVulkanSceneSwapchainWriters = _vulkanSceneSwapchainWriters;
                    _lastFrameVulkanOverlaySwapchainWriters = _vulkanOverlaySwapchainWriters;
                    _lastFrameVulkanForcedDiagnosticSwapchainWriters = _vulkanForcedDiagnosticSwapchainWriters;
                    _lastFrameVulkanFboOnlyDrawOps = _vulkanFboOnlyDrawOps;
                    _lastFrameVulkanFboOnlyBlitOps = _vulkanFboOnlyBlitOps;
                    _lastFrameVulkanMissingSceneSwapchainWriteFrames = _vulkanMissingSceneSwapchainWriteFrames;
                    _lastFrameVulkanFirstFailedFrameOpPassIndex = _vulkanFirstFailedFrameOpPassIndex;
                    _lastFrameVulkanFirstFailedFrameOpPipelineIdentity = _vulkanFirstFailedFrameOpPipelineIdentity;
                    _lastFrameVulkanFirstFailedFrameOpViewportIdentity = _vulkanFirstFailedFrameOpViewportIdentity;
                    _lastFrameVulkanValidationMessageCount = _vulkanValidationMessageCount;
                    _lastFrameVulkanValidationErrorCount = _vulkanValidationErrorCount;
                    _lastFrameVulkanDescriptorFallbackSampledImages = _vulkanDescriptorFallbackSampledImages;
                    _lastFrameVulkanDescriptorFallbackStorageImages = _vulkanDescriptorFallbackStorageImages;
                    _lastFrameVulkanDescriptorFallbackUniformBuffers = _vulkanDescriptorFallbackUniformBuffers;
                    _lastFrameVulkanDescriptorFallbackStorageBuffers = _vulkanDescriptorFallbackStorageBuffers;
                    _lastFrameVulkanDescriptorFallbackTexelBuffers = _vulkanDescriptorFallbackTexelBuffers;
                    _lastFrameVulkanDescriptorBindingFailures = _vulkanDescriptorBindingFailures;
                    _lastFrameVulkanDescriptorSkippedDraws = _vulkanDescriptorSkippedDraws;
                    _lastFrameVulkanDescriptorSkippedDispatches = _vulkanDescriptorSkippedDispatches;
                    _lastFrameVulkanDynamicUniformAllocations = _vulkanDynamicUniformAllocations;
                    _lastFrameVulkanDynamicUniformAllocatedBytes = _vulkanDynamicUniformAllocatedBytes;
                    _lastFrameVulkanDynamicUniformExhaustions = _vulkanDynamicUniformExhaustions;
                    _lastFrameVulkanRetiredResourcePlanReplacements = _vulkanRetiredResourcePlanReplacements;
                    _lastFrameVulkanRetiredResourcePlanImages = _vulkanRetiredResourcePlanImages;
                    _lastFrameVulkanRetiredResourcePlanBuffers = _vulkanRetiredResourcePlanBuffers;
                    lock (_vulkanDiagnosticLock)
                    {
                        _lastFrameVulkanFirstFailedFrameOpType = _vulkanFirstFailedFrameOpType;
                        _lastFrameVulkanFirstFailedFrameOpTargetName = _vulkanFirstFailedFrameOpTargetName;
                        _lastFrameVulkanFirstFailedFrameOpMaterialName = _vulkanFirstFailedFrameOpMaterialName;
                        _lastFrameVulkanFirstFailedFrameOpShaderName = _vulkanFirstFailedFrameOpShaderName;
                        _lastFrameVulkanFirstFailedFrameOpMessage = _vulkanFirstFailedFrameOpMessage;
                        _lastFrameVulkanFrameDiagnosticSummary = _vulkanFrameDiagnosticSummary;
                        _lastFrameVulkanLastValidationMessage = _vulkanLastValidationMessage;
                        _lastFrameVulkanPipelineCacheMissSummary = _vulkanPipelineCacheMissSummary;
                        _lastFrameVulkanDescriptorFallbackSummary = _vulkanDescriptorFallbackSummary;
                        _lastFrameVulkanDescriptorFailureSummary = _vulkanDescriptorFailureSummary;
                    }
                    _lastFrameVrLeftEyeDraws = _vrLeftEyeDraws;
                    _lastFrameVrRightEyeDraws = _vrRightEyeDraws;
                    _lastFrameVrLeftEyeVisible = _vrLeftEyeVisible;
                    _lastFrameVrRightEyeVisible = _vrRightEyeVisible;
                    _lastFrameVrLeftWorkerBuildTimeTicks = _vrLeftWorkerBuildTimeTicks;
                    _lastFrameVrRightWorkerBuildTimeTicks = _vrRightWorkerBuildTimeTicks;
                    _lastFrameVrRenderSubmitTimeTicks = _vrRenderSubmitTimeTicks;
                    _lastFrameVrXrWaitFrameBlockTimeTicks = _vrXrWaitFrameBlockTimeTicks;
                    _lastFrameVrXrEndFrameSubmitTimeTicks = _vrXrEndFrameSubmitTimeTicks;
                    _lastFrameVrXrPredictedToLatePoseDeltaMillimetersBits = _vrXrPredictedToLatePoseDeltaMillimetersBits;
                    _lastFrameVrXrPredictedToLatePoseDeltaDegreesBits = _vrXrPredictedToLatePoseDeltaDegreesBits;
                    _lastFrameVrXrPredictedDisplayLeadTimeMsBits = _vrXrPredictedDisplayLeadTimeMsBits;
                    _lastFrameVrXrMissedDeadlineFrames = _vrXrMissedDeadlineFrames;
                    _lastFrameVrXrTrackingLossFrames = _vrXrTrackingLossFrames;
                    _lastFrameVrXrRelocatePredictedTimeTicks = _vrXrRelocatePredictedTimeTicks;
                    _lastFrameVrXrCollectFrustumExpansionDegreesBits = _vrXrCollectFrustumExpansionDegreesBits;
                    _lastFrameVrXrPacingThreadIdleTimeTicks = _vrXrPacingThreadIdleTimeTicks;
                    _lastFrameVrXrPacingHandoffStalls = _vrXrPacingHandoffStalls;
                    _lastFrameFBOBandwidthBytes = _fboBandwidthBytes;
                    _lastFrameFBOBindCount = _fboBindCount;

#if !XRE_PUBLISHED
                    Engine.ProfileCapture.RecordRenderStatsSnapshot();
#endif

                    _drawCalls = 0;
                    _trianglesRendered = 0;
                    _multiDrawCalls = 0;
                    _gpuCpuFallbackEvents = 0;
                    _gpuCpuFallbackRecoveredCommands = 0;
                    _forbiddenGpuFallbackEvents = 0;
                    _gpuTransparencyOpaqueOrOtherVisible = 0;
                    _gpuTransparencyMaskedVisible = 0;
                    _gpuTransparencyApproximateVisible = 0;
                    _gpuTransparencyExactVisible = 0;
                    _gpuMeshletRequestedFrames = 0;
                    _gpuMeshletProductionFrames = 0;
                    _gpuMeshletFallbackFrames = 0;
                    _gpuMeshletDispatchSkipped = 0;
                    _gpuMeshletTaskRecordsEmitted = 0;
                    _gpuMeshletTaskRecordsFrustumCulled = 0;
                    _gpuMeshletTaskRecordsConeCulled = 0;
                    _gpuMeshletTaskRecordsHiZCulled = 0;
                    _gpuMeshletExpansionOverflowCount = 0;
                    _gpuMeshletBufferBytesResident = 0;
                    _gpuMeshletCacheHits = 0;
                    _gpuMeshletCacheMisses = 0;
                    _gpuMeshletCacheStale = 0;
                    _gpuMappedBuffers = 0;
                    _gpuReadbackBytes = 0;
                    _rtxIoDecompressCalls = 0;
                    _rtxIoCopyIndirectCalls = 0;
                    _rtxIoCompressedBytes = 0;
                    _rtxIoDecompressedBytes = 0;
                    _rtxIoCopyBytes = 0;
                    _rtxIoSubmissionTimeTicks = 0;
                    _vulkanIndirectCountPathCalls = 0;
                    _vulkanIndirectNonCountPathCalls = 0;
                    _vulkanIndirectLoopFallbackCalls = 0;
                    _vulkanIndirectApiCalls = 0;
                    _vulkanIndirectSubmittedDraws = 0;
                    _vulkanIndirectRequestedBatches = 0;
                    _vulkanIndirectMergedBatches = 0;
                    _vulkanIndirectPrimaryRecordOps = 0;
                    _vulkanIndirectSecondaryRecordOps = 0;
                    _vulkanIndirectParallelSecondaryRecordOps = 0;
                    _vulkanPlannedImageBarriers = 0;
                    _vulkanPlannedBufferBarriers = 0;
                    _vulkanQueueOwnershipTransfers = 0;
                    _vulkanBarrierStageFlushes = 0;
                    _vulkanOverlapCandidatePasses = 0;
                    _vulkanOverlapTransferCosts = 0;
                    _vulkanOverlapFrameDeltaMicros = 0;
                    _vulkanOverlapModePromotions = 0;
                    _vulkanOverlapModeDemotions = 0;
                    _vulkanAdhocBarrierEmits = 0;
                    _vulkanAdhocBarrierRedundant = 0;
                    _vulkanPipelineBinds = 0;
                    _vulkanDescriptorBinds = 0;
                    _vulkanPushConstantWrites = 0;
                    _vulkanVertexBufferBinds = 0;
                    _vulkanIndexBufferBinds = 0;
                    _vulkanPipelineBindSkips = 0;
                    _vulkanDescriptorBindSkips = 0;
                    _vulkanVertexBufferBindSkips = 0;
                    _vulkanIndexBufferBindSkips = 0;
                    _vulkanPipelineCacheLookupHits = 0;
                    _vulkanPipelineCacheLookupMisses = 0;
                    _vulkanRequestedDraws = 0;
                    _vulkanCulledDraws = 0;
                    _vulkanEmittedIndirectDraws = 0;
                    _vulkanConsumedDraws = 0;
                    _vulkanOverflowCount = 0;
                    _vulkanStageResetTicks = 0;
                    _vulkanStageCullTicks = 0;
                    _vulkanStageOcclusionTicks = 0;
                    _vulkanStageIndirectTicks = 0;
                    _vulkanStageDrawTicks = 0;
                    _vulkanFrameWaitFenceTicks = 0;
                    _vulkanFrameAcquireImageTicks = 0;
                    _vulkanFrameRecordCommandBufferTicks = 0;
                    _vulkanFrameSubmitTicks = 0;
                    _vulkanFrameTrimTicks = 0;
                    _vulkanFramePresentTicks = 0;
                    _vulkanFrameTotalTicks = 0;
                    _vulkanFrameGpuCommandBufferTicks = 0;
                    _vulkanDeviceLocalAllocationCount = 0;
                    _vulkanDeviceLocalAllocatedBytes = 0;
                    _vulkanUploadAllocationCount = 0;
                    _vulkanUploadAllocatedBytes = 0;
                    _vulkanReadbackAllocationCount = 0;
                    _vulkanReadbackAllocatedBytes = 0;
                    _vulkanDescriptorPoolCreateCount = 0;
                    _vulkanDescriptorPoolDestroyCount = 0;
                    _vulkanDescriptorPoolResetCount = 0;
                    _vulkanQueueSubmitCount = 0;
                    _vulkanOomFallbackCount = 0;
                    _vulkanDroppedFrameOps = 0;
                    _vulkanDroppedDrawOps = 0;
                    _vulkanDroppedComputeOps = 0;
                    _vulkanSceneSwapchainWriters = 0;
                    _vulkanOverlaySwapchainWriters = 0;
                    _vulkanForcedDiagnosticSwapchainWriters = 0;
                    _vulkanFboOnlyDrawOps = 0;
                    _vulkanFboOnlyBlitOps = 0;
                    _vulkanMissingSceneSwapchainWriteFrames = 0;
                    _vulkanFirstFailedFrameOpPassIndex = int.MinValue;
                    _vulkanFirstFailedFrameOpPipelineIdentity = 0;
                    _vulkanFirstFailedFrameOpViewportIdentity = 0;
                    _vulkanValidationMessageCount = 0;
                    _vulkanValidationErrorCount = 0;
                    _vulkanDescriptorFallbackSampledImages = 0;
                    _vulkanDescriptorFallbackStorageImages = 0;
                    _vulkanDescriptorFallbackUniformBuffers = 0;
                    _vulkanDescriptorFallbackStorageBuffers = 0;
                    _vulkanDescriptorFallbackTexelBuffers = 0;
                    _vulkanDescriptorBindingFailures = 0;
                    _vulkanDescriptorSkippedDraws = 0;
                    _vulkanDescriptorSkippedDispatches = 0;
                    _vulkanDynamicUniformAllocations = 0;
                    _vulkanDynamicUniformAllocatedBytes = 0;
                    _vulkanDynamicUniformExhaustions = 0;
                    _vulkanRetiredResourcePlanReplacements = 0;
                    _vulkanRetiredResourcePlanImages = 0;
                    _vulkanRetiredResourcePlanBuffers = 0;
                    lock (_vulkanDiagnosticLock)
                    {
                        _vulkanFirstFailedFrameOpType = string.Empty;
                        _vulkanFirstFailedFrameOpTargetName = string.Empty;
                        _vulkanFirstFailedFrameOpMaterialName = string.Empty;
                        _vulkanFirstFailedFrameOpShaderName = string.Empty;
                        _vulkanFirstFailedFrameOpMessage = string.Empty;
                        _vulkanFrameDiagnosticSummary = string.Empty;
                        _vulkanLastValidationMessage = string.Empty;
                        _vulkanPipelineCacheMissSummary = string.Empty;
                        _vulkanDescriptorFallbackSummary = string.Empty;
                        _vulkanDescriptorFailureSummary = string.Empty;
                    }
                    _vrLeftEyeDraws = 0;
                    _vrRightEyeDraws = 0;
                    _vrLeftEyeVisible = 0;
                    _vrRightEyeVisible = 0;
                    _vrLeftWorkerBuildTimeTicks = 0;
                    _vrRightWorkerBuildTimeTicks = 0;
                    _vrRenderSubmitTimeTicks = 0;
                    _vrXrWaitFrameBlockTimeTicks = 0;
                    _vrXrEndFrameSubmitTimeTicks = 0;
                    _vrXrPredictedToLatePoseDeltaMillimetersBits = 0;
                    _vrXrPredictedToLatePoseDeltaDegreesBits = 0;
                    _vrXrPredictedDisplayLeadTimeMsBits = BitConverter.DoubleToInt64Bits(double.NaN);
                    _vrXrMissedDeadlineFrames = 0;
                    _vrXrTrackingLossFrames = 0;
                    _vrXrRelocatePredictedTimeTicks = 0;
                    _vrXrCollectFrustumExpansionDegreesBits = 0;
                    _vrXrPacingThreadIdleTimeTicks = 0;
                    _vrXrPacingHandoffStalls = 0;
                    _fboBandwidthBytes = 0;
                    _fboBindCount = 0;
                    // Note: render-matrix and skinned-bounds stats are swapped separately during swap-buffers.
                }

                /// <summary>
                /// Increment the draw call counter.
                /// </summary>
                public static void IncrementDrawCalls()
                {
                    if (!EnableTracking) return;
                    Interlocked.Increment(ref _drawCalls);
                }

                /// <summary>
                /// Increment the draw call counter by a specific amount.
                /// </summary>
                public static void IncrementDrawCalls(int count)
                {
                    if (!EnableTracking) return;
                    Interlocked.Add(ref _drawCalls, count);
                }

                /// <summary>
                /// Add to the triangles rendered counter.
                /// </summary>
                public static void AddTrianglesRendered(int count)
                {
                    if (!EnableTracking) return;
                    Interlocked.Add(ref _trianglesRendered, count);
                }

                /// <summary>
                /// Increment the multi-draw indirect call counter.
                /// </summary>
                public static void IncrementMultiDrawCalls()
                {
                    if (!EnableTracking) return;
                    Interlocked.Increment(ref _multiDrawCalls);
                }

                /// <summary>
                /// Increment the multi-draw indirect call counter by a specific amount.
                /// </summary>
                public static void IncrementMultiDrawCalls(int count)
                {
                    if (!EnableTracking) return;
                    Interlocked.Add(ref _multiDrawCalls, count);
                }

            }
        }
    }
}
