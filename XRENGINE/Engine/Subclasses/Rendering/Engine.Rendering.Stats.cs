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
            public static class Stats
            {
                private static int _drawCalls;
                private static int _trianglesRendered;
                private static int _multiDrawCalls;
                private static int _lastFrameDrawCalls;
                private static int _lastFrameTrianglesRendered;
                private static int _lastFrameMultiDrawCalls;
                private static int _gpuCpuFallbackEvents;
                private static int _gpuCpuFallbackRecoveredCommands;
                private static int _forbiddenGpuFallbackEvents;
                private static int _lastFrameGpuCpuFallbackEvents;
                private static int _lastFrameGpuCpuFallbackRecoveredCommands;
                private static int _lastFrameForbiddenGpuFallbackEvents;
                private static int _gpuTransparencyOpaqueOrOtherVisible;
                private static int _gpuTransparencyMaskedVisible;
                private static int _gpuTransparencyApproximateVisible;
                private static int _gpuTransparencyExactVisible;
                private static int _lastFrameGpuTransparencyOpaqueOrOtherVisible;
                private static int _lastFrameGpuTransparencyMaskedVisible;
                private static int _lastFrameGpuTransparencyApproximateVisible;
                private static int _lastFrameGpuTransparencyExactVisible;
                private static int _vulkanIndirectCountPathCalls;
                private static int _vulkanIndirectNonCountPathCalls;
                private static int _vulkanIndirectLoopFallbackCalls;
                private static int _vulkanIndirectApiCalls;
                private static long _vulkanIndirectSubmittedDraws;
                private static int _vulkanIndirectRequestedBatches;
                private static int _vulkanIndirectMergedBatches;
                private static int _vulkanIndirectPrimaryRecordOps;
                private static int _vulkanIndirectSecondaryRecordOps;
                private static int _vulkanIndirectParallelSecondaryRecordOps;
                private static int _vulkanPlannedImageBarriers;
                private static int _vulkanPlannedBufferBarriers;
                private static int _vulkanQueueOwnershipTransfers;
                private static int _vulkanBarrierStageFlushes;
                private static int _vulkanOverlapCandidatePasses;
                private static int _vulkanOverlapTransferCosts;
                private static long _vulkanOverlapFrameDeltaMicros;
                private static int _vulkanOverlapModePromotions;
                private static int _vulkanOverlapModeDemotions;
                private static int _vulkanAdhocBarrierEmits;
                private static int _vulkanAdhocBarrierRedundant;
                private static int _vulkanPipelineBinds;
                private static int _vulkanDescriptorBinds;
                private static int _vulkanPushConstantWrites;
                private static int _vulkanVertexBufferBinds;
                private static int _vulkanIndexBufferBinds;
                private static int _vulkanPipelineBindSkips;
                private static int _vulkanDescriptorBindSkips;
                private static int _vulkanVertexBufferBindSkips;
                private static int _vulkanIndexBufferBindSkips;
                private static int _vulkanPipelineCacheLookupHits;
                private static int _vulkanPipelineCacheLookupMisses;
                private static long _vulkanRequestedDraws;
                private static long _vulkanCulledDraws;
                private static long _vulkanEmittedIndirectDraws;
                private static long _vulkanConsumedDraws;
                private static long _vulkanOverflowCount;
                private static long _vulkanStageResetTicks;
                private static long _vulkanStageCullTicks;
                private static long _vulkanStageOcclusionTicks;
                private static long _vulkanStageIndirectTicks;
                private static long _vulkanStageDrawTicks;
                private static long _vulkanFrameWaitFenceTicks;
                private static long _vulkanFrameAcquireImageTicks;
                private static long _vulkanFrameRecordCommandBufferTicks;
                private static long _vulkanFrameSubmitTicks;
                private static long _vulkanFrameTrimTicks;
                private static long _vulkanFramePresentTicks;
                private static long _vulkanFrameTotalTicks;
                private static long _vulkanFrameGpuCommandBufferTicks;
                private static int _lastFrameVulkanIndirectCountPathCalls;
                private static int _lastFrameVulkanIndirectNonCountPathCalls;
                private static int _lastFrameVulkanIndirectLoopFallbackCalls;
                private static int _lastFrameVulkanIndirectApiCalls;
                private static long _lastFrameVulkanIndirectSubmittedDraws;
                private static int _lastFrameVulkanIndirectRequestedBatches;
                private static int _lastFrameVulkanIndirectMergedBatches;
                private static int _lastFrameVulkanIndirectPrimaryRecordOps;
                private static int _lastFrameVulkanIndirectSecondaryRecordOps;
                private static int _lastFrameVulkanIndirectParallelSecondaryRecordOps;
                private static int _lastFrameVulkanPlannedImageBarriers;
                private static int _lastFrameVulkanPlannedBufferBarriers;
                private static int _lastFrameVulkanQueueOwnershipTransfers;
                private static int _lastFrameVulkanBarrierStageFlushes;
                private static int _lastFrameVulkanOverlapCandidatePasses;
                private static int _lastFrameVulkanOverlapTransferCosts;
                private static long _lastFrameVulkanOverlapFrameDeltaMicros;
                private static int _lastFrameVulkanOverlapModePromotions;
                private static int _lastFrameVulkanOverlapModeDemotions;
                private static int _lastFrameVulkanAdhocBarrierEmits;
                private static int _lastFrameVulkanAdhocBarrierRedundant;
                private static int _lastFrameVulkanPipelineBinds;
                private static int _lastFrameVulkanDescriptorBinds;
                private static int _lastFrameVulkanPushConstantWrites;
                private static int _lastFrameVulkanVertexBufferBinds;
                private static int _lastFrameVulkanIndexBufferBinds;
                private static int _lastFrameVulkanPipelineBindSkips;
                private static int _lastFrameVulkanDescriptorBindSkips;
                private static int _lastFrameVulkanVertexBufferBindSkips;
                private static int _lastFrameVulkanIndexBufferBindSkips;
                private static int _lastFrameVulkanPipelineCacheLookupHits;
                private static int _lastFrameVulkanPipelineCacheLookupMisses;
                private static long _lastFrameVulkanRequestedDraws;
                private static long _lastFrameVulkanCulledDraws;
                private static long _lastFrameVulkanEmittedIndirectDraws;
                private static long _lastFrameVulkanConsumedDraws;
                private static long _lastFrameVulkanOverflowCount;
                private static long _lastFrameVulkanStageResetTicks;
                private static long _lastFrameVulkanStageCullTicks;
                private static long _lastFrameVulkanStageOcclusionTicks;
                private static long _lastFrameVulkanStageIndirectTicks;
                private static long _lastFrameVulkanStageDrawTicks;
                private static long _lastFrameVulkanFrameWaitFenceTicks;
                private static long _lastFrameVulkanFrameAcquireImageTicks;
                private static long _lastFrameVulkanFrameRecordCommandBufferTicks;
                private static long _lastFrameVulkanFrameSubmitTicks;
                private static long _lastFrameVulkanFrameTrimTicks;
                private static long _lastFrameVulkanFramePresentTicks;
                private static long _lastFrameVulkanFrameTotalTicks;
                private static long _lastFrameVulkanFrameGpuCommandBufferTicks;
                private static int _vulkanDeviceLocalAllocationCount;
                private static long _vulkanDeviceLocalAllocatedBytes;
                private static int _vulkanUploadAllocationCount;
                private static long _vulkanUploadAllocatedBytes;
                private static int _vulkanReadbackAllocationCount;
                private static long _vulkanReadbackAllocatedBytes;
                private static int _vulkanDescriptorPoolCreateCount;
                private static int _vulkanDescriptorPoolDestroyCount;
                private static int _vulkanDescriptorPoolResetCount;
                private static int _vulkanQueueSubmitCount;
                private static int _lastFrameVulkanDeviceLocalAllocationCount;
                private static long _lastFrameVulkanDeviceLocalAllocatedBytes;
                private static int _lastFrameVulkanUploadAllocationCount;
                private static long _lastFrameVulkanUploadAllocatedBytes;
                private static int _lastFrameVulkanReadbackAllocationCount;
                private static long _lastFrameVulkanReadbackAllocatedBytes;
                private static int _lastFrameVulkanDescriptorPoolCreateCount;
                private static int _lastFrameVulkanDescriptorPoolDestroyCount;
                private static int _lastFrameVulkanDescriptorPoolResetCount;
                private static int _lastFrameVulkanQueueSubmitCount;
                private static int _vulkanOomFallbackCount;
                private static int _lastFrameVulkanOomFallbackCount;

                // GPU->CPU readback / mapping counters (per-frame)
                private static int _gpuMappedBuffers;
                private static long _gpuReadbackBytes;
                private static int _lastFrameGpuMappedBuffers;
                private static long _lastFrameGpuReadbackBytes;
                private static int _rtxIoDecompressCalls;
                private static int _rtxIoCopyIndirectCalls;
                private static long _rtxIoCompressedBytes;
                private static long _rtxIoDecompressedBytes;
                private static long _rtxIoCopyBytes;
                private static long _rtxIoSubmissionTimeTicks;
                private static int _lastFrameRtxIoDecompressCalls;
                private static int _lastFrameRtxIoCopyIndirectCalls;
                private static long _lastFrameRtxIoCompressedBytes;
                private static long _lastFrameRtxIoDecompressedBytes;
                private static long _lastFrameRtxIoCopyBytes;
                private static long _lastFrameRtxIoSubmissionTimeTicks;
                private static int _vrLeftEyeDraws;
                private static int _vrRightEyeDraws;
                private static int _lastFrameVrLeftEyeDraws;
                private static int _lastFrameVrRightEyeDraws;
                private static int _vrLeftEyeVisible;
                private static int _vrRightEyeVisible;
                private static int _lastFrameVrLeftEyeVisible;
                private static int _lastFrameVrRightEyeVisible;
                private static long _vrLeftWorkerBuildTimeTicks;
                private static long _vrRightWorkerBuildTimeTicks;
                private static long _lastFrameVrLeftWorkerBuildTimeTicks;
                private static long _lastFrameVrRightWorkerBuildTimeTicks;
                private static long _vrRenderSubmitTimeTicks;
                private static long _lastFrameVrRenderSubmitTimeTicks;

                // Render-matrix stats use a separate swap cycle aligned with SwapBuffers phase.
                // Current = being written now, Display = last completed swap, Ready = waiting to become Display.
                private static int _renderMatrixAppliedCurrent;
                private static int _renderMatrixBatchCountCurrent;
                private static int _renderMatrixMaxBatchSizeCurrent;
                private static int _renderMatrixSetCallsCurrent;
                private static int _renderMatrixListenerInvocationsCurrent;
                private static int _renderMatrixAppliedDisplay;
                private static int _renderMatrixBatchCountDisplay;
                private static int _renderMatrixMaxBatchSizeDisplay;
                private static int _renderMatrixSetCallsDisplay;
                private static int _renderMatrixListenerInvocationsDisplay;
                private static readonly object _renderMatrixStatsLock = new();
                private static Dictionary<string, int> _renderMatrixListenerCountsCurrent = new(StringComparer.Ordinal);
                private static Dictionary<string, int> _renderMatrixListenerCountsDisplay = new(StringComparer.Ordinal);
                private static bool _renderMatrixStatsReady;
                private static int _renderMatrixStatsDirty;

                // Skinned-bounds refresh stats use the same swap-cycle model as render-matrix stats.
                private static int _skinnedBoundsDeferredScheduledCurrent;
                private static int _skinnedBoundsDeferredCompletedCurrent;
                private static int _skinnedBoundsDeferredFailedCurrent;
                private static int _skinnedBoundsDeferredInFlightLive;
                private static int _skinnedBoundsDeferredMaxInFlightCurrent;
                private static long _skinnedBoundsDeferredQueueWaitTicksCurrent;
                private static long _skinnedBoundsDeferredCpuJobTicksCurrent;
                private static long _skinnedBoundsDeferredApplyTicksCurrent;
                private static long _skinnedBoundsDeferredMaxQueueWaitTicksCurrent;
                private static long _skinnedBoundsDeferredMaxCpuJobTicksCurrent;
                private static long _skinnedBoundsDeferredMaxApplyTicksCurrent;
                private static int _skinnedBoundsGpuCompletedCurrent;
                private static long _skinnedBoundsGpuComputeTicksCurrent;
                private static long _skinnedBoundsGpuApplyTicksCurrent;
                private static long _skinnedBoundsGpuMaxComputeTicksCurrent;
                private static long _skinnedBoundsGpuMaxApplyTicksCurrent;
                private static int _skinnedBoundsDeferredScheduledDisplay;
                private static int _skinnedBoundsDeferredCompletedDisplay;
                private static int _skinnedBoundsDeferredFailedDisplay;
                private static int _skinnedBoundsDeferredInFlightDisplay;
                private static int _skinnedBoundsDeferredMaxInFlightDisplay;
                private static long _skinnedBoundsDeferredQueueWaitTicksDisplay;
                private static long _skinnedBoundsDeferredCpuJobTicksDisplay;
                private static long _skinnedBoundsDeferredApplyTicksDisplay;
                private static long _skinnedBoundsDeferredMaxQueueWaitTicksDisplay;
                private static long _skinnedBoundsDeferredMaxCpuJobTicksDisplay;
                private static long _skinnedBoundsDeferredMaxApplyTicksDisplay;
                private static int _skinnedBoundsGpuCompletedDisplay;
                private static long _skinnedBoundsGpuComputeTicksDisplay;
                private static long _skinnedBoundsGpuApplyTicksDisplay;
                private static long _skinnedBoundsGpuMaxComputeTicksDisplay;
                private static long _skinnedBoundsGpuMaxApplyTicksDisplay;
                private static bool _skinnedBoundsStatsReady;
                private static int _skinnedBoundsStatsDirty;

                // VRAM tracking fields
                private static long _allocatedVRAMBytes;
                private static long _allocatedBufferBytes;
                private static long _allocatedTextureBytes;
                private static long _allocatedRenderBufferBytes;

                // FBO bandwidth tracking fields (per-frame)
                private static long _fboBandwidthBytes;
                private static int _fboBindCount;
                private static long _lastFrameFBOBandwidthBytes;
                private static int _lastFrameFBOBindCount;

                /// <summary>
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
                /// Number of GPU->CPU culling fallback events in the last completed frame.
                /// </summary>
                public static int GpuCpuFallbackEvents => _lastFrameGpuCpuFallbackEvents;

                /// <summary>
                /// Number of commands recovered by GPU->CPU fallback in the last completed frame.
                /// </summary>
                public static int GpuCpuFallbackRecoveredCommands => _lastFrameGpuCpuFallbackRecoveredCommands;

                /// <summary>
                /// Number of forbidden fallback attempts observed in the last completed frame.
                /// Forbidden fallbacks indicate shipping-profile behavior would have fallen back but was blocked.
                /// </summary>
                public static int ForbiddenGpuFallbackEvents => _lastFrameForbiddenGpuFallbackEvents;

                public static int GpuTransparencyOpaqueOrOtherVisible => _lastFrameGpuTransparencyOpaqueOrOtherVisible;

                public static int GpuTransparencyMaskedVisible => _lastFrameGpuTransparencyMaskedVisible;

                public static int GpuTransparencyApproximateVisible => _lastFrameGpuTransparencyApproximateVisible;

                public static int GpuTransparencyExactVisible => _lastFrameGpuTransparencyExactVisible;

                /// <summary>
                /// Number of GPU buffers mapped for CPU access in the last completed frame.
                /// </summary>
                public static int GpuMappedBuffers => _lastFrameGpuMappedBuffers;

                /// <summary>
                /// Total bytes read back from GPU buffers in the last completed frame.
                /// </summary>
                public static long GpuReadbackBytes => _lastFrameGpuReadbackBytes;
                public static int RtxIoDecompressCalls => _lastFrameRtxIoDecompressCalls;
                public static int RtxIoCopyIndirectCalls => _lastFrameRtxIoCopyIndirectCalls;
                public static long RtxIoCompressedBytes => _lastFrameRtxIoCompressedBytes;
                public static long RtxIoDecompressedBytes => _lastFrameRtxIoDecompressedBytes;
                public static long RtxIoCopyBytes => _lastFrameRtxIoCopyBytes;
                public static double RtxIoSubmissionTimeMs => TimeSpan.FromTicks(_lastFrameRtxIoSubmissionTimeTicks).TotalMilliseconds;
                public static int VulkanIndirectCountPathCalls => _lastFrameVulkanIndirectCountPathCalls;
                public static int VulkanIndirectNonCountPathCalls => _lastFrameVulkanIndirectNonCountPathCalls;
                public static int VulkanIndirectLoopFallbackCalls => _lastFrameVulkanIndirectLoopFallbackCalls;
                public static int VulkanIndirectApiCalls => _lastFrameVulkanIndirectApiCalls;
                public static long VulkanIndirectSubmittedDraws => _lastFrameVulkanIndirectSubmittedDraws;
                public static int VulkanIndirectRequestedBatches => _lastFrameVulkanIndirectRequestedBatches;
                public static int VulkanIndirectMergedBatches => _lastFrameVulkanIndirectMergedBatches;
                public static int VulkanIndirectPrimaryRecordOps => _lastFrameVulkanIndirectPrimaryRecordOps;
                public static int VulkanIndirectSecondaryRecordOps => _lastFrameVulkanIndirectSecondaryRecordOps;
                public static int VulkanIndirectParallelSecondaryRecordOps => _lastFrameVulkanIndirectParallelSecondaryRecordOps;
                public static int VulkanPlannedImageBarriers => _lastFrameVulkanPlannedImageBarriers;
                public static int VulkanPlannedBufferBarriers => _lastFrameVulkanPlannedBufferBarriers;
                public static int VulkanQueueOwnershipTransfers => _lastFrameVulkanQueueOwnershipTransfers;
                public static int VulkanBarrierStageFlushes => _lastFrameVulkanBarrierStageFlushes;
                public static int VulkanOverlapCandidatePasses => _lastFrameVulkanOverlapCandidatePasses;
                public static int VulkanOverlapTransferCosts => _lastFrameVulkanOverlapTransferCosts;
                public static double VulkanOverlapFrameDeltaMs => _lastFrameVulkanOverlapFrameDeltaMicros / 1000.0;
                public static int VulkanOverlapModePromotions => _lastFrameVulkanOverlapModePromotions;
                public static int VulkanOverlapModeDemotions => _lastFrameVulkanOverlapModeDemotions;
                public static int VulkanAdhocBarrierEmits => _lastFrameVulkanAdhocBarrierEmits;
                public static int VulkanAdhocBarrierRedundant => _lastFrameVulkanAdhocBarrierRedundant;
                                public static int VulkanPipelineBinds => _lastFrameVulkanPipelineBinds;
                                public static int VulkanDescriptorBinds => _lastFrameVulkanDescriptorBinds;
                                public static int VulkanPushConstantWrites => _lastFrameVulkanPushConstantWrites;
                                public static int VulkanVertexBufferBinds => _lastFrameVulkanVertexBufferBinds;
                                public static int VulkanIndexBufferBinds => _lastFrameVulkanIndexBufferBinds;
                                public static int VulkanPipelineBindSkips => _lastFrameVulkanPipelineBindSkips;
                                public static int VulkanDescriptorBindSkips => _lastFrameVulkanDescriptorBindSkips;
                                public static int VulkanVertexBufferBindSkips => _lastFrameVulkanVertexBufferBindSkips;
                                public static int VulkanIndexBufferBindSkips => _lastFrameVulkanIndexBufferBindSkips;
                                public static int VulkanPipelineCacheLookupHits => _lastFrameVulkanPipelineCacheLookupHits;
                                public static int VulkanPipelineCacheLookupMisses => _lastFrameVulkanPipelineCacheLookupMisses;
                                public static long VulkanRequestedDraws => _lastFrameVulkanRequestedDraws;
                                public static long VulkanCulledDraws => _lastFrameVulkanCulledDraws;
                                public static long VulkanEmittedIndirectDraws => _lastFrameVulkanEmittedIndirectDraws;
                                public static long VulkanConsumedDraws => _lastFrameVulkanConsumedDraws;
                                public static long VulkanOverflowCount => _lastFrameVulkanOverflowCount;
                                public static double VulkanCullEfficiency
                                    => _lastFrameVulkanRequestedDraws <= 0
                                    ? 1.0
                                    : Math.Max(0.0, 1.0 - ((double)_lastFrameVulkanCulledDraws / _lastFrameVulkanRequestedDraws));
                                public static double VulkanResetStageMs => TimeSpan.FromTicks(_lastFrameVulkanStageResetTicks).TotalMilliseconds;
                                public static double VulkanCullStageMs => TimeSpan.FromTicks(_lastFrameVulkanStageCullTicks).TotalMilliseconds;
                                public static double VulkanOcclusionStageMs => TimeSpan.FromTicks(_lastFrameVulkanStageOcclusionTicks).TotalMilliseconds;
                                public static double VulkanIndirectStageMs => TimeSpan.FromTicks(_lastFrameVulkanStageIndirectTicks).TotalMilliseconds;
                                public static double VulkanDrawStageMs => TimeSpan.FromTicks(_lastFrameVulkanStageDrawTicks).TotalMilliseconds;
                                public static double VulkanFrameWaitFenceMs => TimeSpan.FromTicks(_lastFrameVulkanFrameWaitFenceTicks).TotalMilliseconds;
                                public static double VulkanFrameAcquireImageMs => TimeSpan.FromTicks(_lastFrameVulkanFrameAcquireImageTicks).TotalMilliseconds;
                                public static double VulkanFrameRecordCommandBufferMs => TimeSpan.FromTicks(_lastFrameVulkanFrameRecordCommandBufferTicks).TotalMilliseconds;
                                public static double VulkanFrameSubmitMs => TimeSpan.FromTicks(_lastFrameVulkanFrameSubmitTicks).TotalMilliseconds;
                                public static double VulkanFrameTrimMs => TimeSpan.FromTicks(_lastFrameVulkanFrameTrimTicks).TotalMilliseconds;
                                public static double VulkanFramePresentMs => TimeSpan.FromTicks(_lastFrameVulkanFramePresentTicks).TotalMilliseconds;
                                public static double VulkanFrameTotalMs => TimeSpan.FromTicks(_lastFrameVulkanFrameTotalTicks).TotalMilliseconds;
                                public static double VulkanFrameGpuCommandBufferMs => TimeSpan.FromTicks(_lastFrameVulkanFrameGpuCommandBufferTicks).TotalMilliseconds;
                                public static int VulkanDeviceLocalAllocationCount => _lastFrameVulkanDeviceLocalAllocationCount;
                                public static long VulkanDeviceLocalAllocatedBytes => _lastFrameVulkanDeviceLocalAllocatedBytes;
                                public static int VulkanUploadAllocationCount => _lastFrameVulkanUploadAllocationCount;
                                public static long VulkanUploadAllocatedBytes => _lastFrameVulkanUploadAllocatedBytes;
                                public static int VulkanReadbackAllocationCount => _lastFrameVulkanReadbackAllocationCount;
                                public static long VulkanReadbackAllocatedBytes => _lastFrameVulkanReadbackAllocatedBytes;
                                public static int VulkanDescriptorPoolCreateCount => _lastFrameVulkanDescriptorPoolCreateCount;
                                public static int VulkanDescriptorPoolDestroyCount => _lastFrameVulkanDescriptorPoolDestroyCount;
                                public static int VulkanDescriptorPoolResetCount => _lastFrameVulkanDescriptorPoolResetCount;
                                public static int VulkanQueueSubmitCount => _lastFrameVulkanQueueSubmitCount;
                                public static int VulkanOomFallbackCount => _lastFrameVulkanOomFallbackCount;
                                public static double VulkanPipelineCacheLookupHitRate
                                        => (_lastFrameVulkanPipelineCacheLookupHits + _lastFrameVulkanPipelineCacheLookupMisses) <= 0
                                                ? 1.0
                                                : (double)_lastFrameVulkanPipelineCacheLookupHits /
                                                    (_lastFrameVulkanPipelineCacheLookupHits + _lastFrameVulkanPipelineCacheLookupMisses);
                public static double VulkanIndirectBatchMergeRatio
                    => _lastFrameVulkanIndirectRequestedBatches <= 0
                        ? 1.0
                        : (double)_lastFrameVulkanIndirectMergedBatches / _lastFrameVulkanIndirectRequestedBatches;
                public static int VrLeftEyeDraws => _lastFrameVrLeftEyeDraws;
                public static int VrRightEyeDraws => _lastFrameVrRightEyeDraws;
                public static int VrLeftEyeVisible => _lastFrameVrLeftEyeVisible;
                public static int VrRightEyeVisible => _lastFrameVrRightEyeVisible;
                public static double VrLeftWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrLeftWorkerBuildTimeTicks).TotalMilliseconds;
                public static double VrRightWorkerBuildTimeMs => TimeSpan.FromTicks(_lastFrameVrRightWorkerBuildTimeTicks).TotalMilliseconds;
                public static double VrRenderSubmitTimeMs => TimeSpan.FromTicks(_lastFrameVrRenderSubmitTimeTicks).TotalMilliseconds;

                /// <summary>
                /// Enables collection of render-matrix statistics.
                /// </summary>
                public static bool EnableRenderMatrixStats { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif

                /// <summary>
                /// Enables detailed render-matrix listener tracking (per listener type).
                /// </summary>
                public static bool EnableRenderMatrixListenerTracking { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif

                /// <summary>
                /// Enables collection of deferred skinned-bounds refresh statistics.
                /// </summary>
                public static bool EnableSkinnedBoundsStats { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif

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
                /// Whether render-matrix stats have been populated at least once.
                /// </summary>
                public static bool RenderMatrixStatsReady => _renderMatrixStatsReady;

                /// <summary>
                /// Number of render-matrix updates applied in the last completed frame.
                /// </summary>
                public static int RenderMatrixApplied => _renderMatrixAppliedDisplay;

                /// <summary>
                /// Number of non-empty render-matrix batches applied in the last completed frame.
                /// </summary>
                public static int RenderMatrixBatchCount => _renderMatrixBatchCountDisplay;

                /// <summary>
                /// Largest render-matrix batch applied in the last completed frame.
                /// </summary>
                public static int RenderMatrixMaxBatchSize => _renderMatrixMaxBatchSizeDisplay;

                /// <summary>
                /// Number of SetRenderMatrix calls in the last completed frame.
                /// </summary>
                public static int RenderMatrixSetCalls => _renderMatrixSetCallsDisplay;

                /// <summary>
                /// Total number of render-matrix listener invocations in the last completed frame.
                /// </summary>
                public static int RenderMatrixListenerInvocations => _renderMatrixListenerInvocationsDisplay;

                /// <summary>
                /// Whether skinned-bounds refresh stats have been populated at least once.
                /// </summary>
                public static bool SkinnedBoundsStatsReady => _skinnedBoundsStatsReady;

                public static int SkinnedBoundsDeferredScheduledCount => _skinnedBoundsDeferredScheduledDisplay;
                public static int SkinnedBoundsDeferredCompletedCount => _skinnedBoundsDeferredCompletedDisplay;
                public static int SkinnedBoundsDeferredFailedCount => _skinnedBoundsDeferredFailedDisplay;
                public static int SkinnedBoundsDeferredInFlightCount => _skinnedBoundsDeferredInFlightDisplay;
                public static int SkinnedBoundsDeferredMaxInFlightCount => _skinnedBoundsDeferredMaxInFlightDisplay;
                public static double SkinnedBoundsDeferredQueueWaitMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredQueueWaitTicksDisplay);
                public static double SkinnedBoundsDeferredCpuJobMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredCpuJobTicksDisplay);
                public static double SkinnedBoundsDeferredApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredApplyTicksDisplay);
                public static double SkinnedBoundsDeferredMaxQueueWaitMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredMaxQueueWaitTicksDisplay);
                public static double SkinnedBoundsDeferredMaxCpuJobMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredMaxCpuJobTicksDisplay);
                public static double SkinnedBoundsDeferredMaxApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsDeferredMaxApplyTicksDisplay);
                public static int SkinnedBoundsGpuCompletedCount => _skinnedBoundsGpuCompletedDisplay;
                public static double SkinnedBoundsGpuComputeMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuComputeTicksDisplay);
                public static double SkinnedBoundsGpuApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuApplyTicksDisplay);
                public static double SkinnedBoundsGpuMaxComputeMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuMaxComputeTicksDisplay);
                public static double SkinnedBoundsGpuMaxApplyMs => StopwatchTicksToMilliseconds(_skinnedBoundsGpuMaxApplyTicksDisplay);

                /// <summary>
                /// Total currently allocated GPU VRAM in bytes.
                /// </summary>
                public static long AllocatedVRAMBytes => Interlocked.Read(ref _allocatedVRAMBytes);

                /// <summary>
                /// Currently allocated GPU buffer memory in bytes.
                /// </summary>
                public static long AllocatedBufferBytes => Interlocked.Read(ref _allocatedBufferBytes);

                /// <summary>
                /// Currently allocated GPU texture memory in bytes.
                /// </summary>
                public static long AllocatedTextureBytes => Interlocked.Read(ref _allocatedTextureBytes);

                /// <summary>
                /// Currently allocated GPU render buffer memory in bytes.
                /// </summary>
                public static long AllocatedRenderBufferBytes => Interlocked.Read(ref _allocatedRenderBufferBytes);

                /// <summary>
                /// Total currently allocated GPU VRAM in megabytes.
                /// </summary>
                public static double AllocatedVRAMMB => AllocatedVRAMBytes / (1024.0 * 1024.0);

                /// <summary>
                /// Configured VRAM budget in bytes. Returns long.MaxValue when budgeting is disabled.
                /// </summary>
                public static long VramBudgetBytes
                    => Engine.Rendering.Settings.EnableVramBudget
                        ? Math.Max(1L, (long)Engine.Rendering.Settings.VramBudgetMB) * 1024L * 1024L
                        : long.MaxValue;

                /// <summary>
                /// Determines whether a tracked GPU allocation would fit inside the configured VRAM budget.
                /// </summary>
                public static bool CanAllocateVram(long requestedBytes, long existingAllocationBytes, out long projectedBytes, out long budgetBytes)
                {
                    budgetBytes = VramBudgetBytes;
                    long currentBytes = AllocatedVRAMBytes;
                    long retainedBytes = Math.Max(0L, existingAllocationBytes);
                    projectedBytes = Math.Max(0L, currentBytes - retainedBytes) + Math.Max(0L, requestedBytes);
                    return projectedBytes <= budgetBytes;
                }

                /// <summary>
                /// Total FBO render bandwidth in bytes for the last completed frame.
                /// This represents the total size of all render targets written to during rendering.
                /// </summary>
                public static long FBOBandwidthBytes => _lastFrameFBOBandwidthBytes;

                /// <summary>
                /// Total FBO render bandwidth in megabytes for the last completed frame.
                /// </summary>
                public static double FBOBandwidthMB => _lastFrameFBOBandwidthBytes / (1024.0 * 1024.0);

                /// <summary>
                /// Number of times FBOs were bound for writing in the last completed frame.
                /// </summary>
                public static int FBOBindCount => _lastFrameFBOBindCount;

                public static bool GpuRenderPipelineProfilingEnabled
                    => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Enabled;

                public static bool GpuRenderPipelineProfilingSupported
                    => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Supported;

                public static bool GpuRenderPipelineTimingsReady
                    => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Ready;

                public static string GpuRenderPipelineBackend
                    => RenderPipelineGpuProfiler.Instance.LatestSnapshot.BackendName;

                public static string GpuRenderPipelineStatusMessage
                    => RenderPipelineGpuProfiler.Instance.LatestSnapshot.StatusMessage;

                public static double GpuRenderPipelineFrameMs
                    => RenderPipelineGpuProfiler.Instance.LatestSnapshot.FrameMilliseconds;

                public static Data.Profiling.GpuPipelineTimingNodeData[] GetGpuRenderPipelineTimingRoots()
                    => RenderPipelineGpuProfiler.Instance.LatestSnapshot.Roots;

                /// <summary>
                /// Call this at the start of each frame to reset the counters.
                /// </summary>
                public static void BeginFrame()
                {
                    bool gpuPipelineProfilingEnabled = EnableTracking && Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;
                    RenderPipelineGpuProfiler.Instance.BeginFrame(State.RenderFrameId, gpuPipelineProfilingEnabled);

                    // Notify GPU dispatch logger of new frame for logging context
                    GpuDispatchLogger.BeginFrame();
                    
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
                    _lastFrameVrLeftEyeDraws = _vrLeftEyeDraws;
                    _lastFrameVrRightEyeDraws = _vrRightEyeDraws;
                    _lastFrameVrLeftEyeVisible = _vrLeftEyeVisible;
                    _lastFrameVrRightEyeVisible = _vrRightEyeVisible;
                    _lastFrameVrLeftWorkerBuildTimeTicks = _vrLeftWorkerBuildTimeTicks;
                    _lastFrameVrRightWorkerBuildTimeTicks = _vrRightWorkerBuildTimeTicks;
                    _lastFrameVrRenderSubmitTimeTicks = _vrRenderSubmitTimeTicks;
                    _lastFrameFBOBandwidthBytes = _fboBandwidthBytes;
                    _lastFrameFBOBindCount = _fboBindCount;

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
                    _vrLeftEyeDraws = 0;
                    _vrRightEyeDraws = 0;
                    _vrLeftEyeVisible = 0;
                    _vrRightEyeVisible = 0;
                    _vrLeftWorkerBuildTimeTicks = 0;
                    _vrRightWorkerBuildTimeTicks = 0;
                    _vrRenderSubmitTimeTicks = 0;
                    _fboBandwidthBytes = 0;
                    _fboBindCount = 0;
                    // Note: render-matrix and skinned-bounds stats are swapped separately during swap-buffers.
                }

                /// <summary>
                /// Records that a GPU buffer was mapped for CPU access.
                /// </summary>
                public static void RecordGpuBufferMapped(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _gpuMappedBuffers, count);
                }

                /// <summary>
                /// Records the number of bytes read back from GPU buffers.
                /// </summary>
                public static void RecordGpuReadbackBytes(long bytes)
                {
                    if (!EnableTracking || bytes <= 0)
                        return;

                    Interlocked.Add(ref _gpuReadbackBytes, bytes);
                }

                public enum EVulkanAllocationTelemetryClass
                {
                    DeviceLocal,
                    Upload,
                    Readback,
                }

                public static void RecordVulkanAllocation(EVulkanAllocationTelemetryClass allocationClass, long bytes)
                {
                    if (!EnableTracking)
                        return;

                    switch (allocationClass)
                    {
                        case EVulkanAllocationTelemetryClass.DeviceLocal:
                            Interlocked.Increment(ref _vulkanDeviceLocalAllocationCount);
                            if (bytes > 0)
                                Interlocked.Add(ref _vulkanDeviceLocalAllocatedBytes, bytes);
                            break;
                        case EVulkanAllocationTelemetryClass.Upload:
                            Interlocked.Increment(ref _vulkanUploadAllocationCount);
                            if (bytes > 0)
                                Interlocked.Add(ref _vulkanUploadAllocatedBytes, bytes);
                            break;
                        case EVulkanAllocationTelemetryClass.Readback:
                            Interlocked.Increment(ref _vulkanReadbackAllocationCount);
                            if (bytes > 0)
                                Interlocked.Add(ref _vulkanReadbackAllocatedBytes, bytes);
                            break;
                    }
                }

                public static void RecordVulkanDescriptorPoolCreate(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _vulkanDescriptorPoolCreateCount, count);
                }

                public static void RecordVulkanDescriptorPoolDestroy(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _vulkanDescriptorPoolDestroyCount, count);
                }

                public static void RecordVulkanDescriptorPoolReset(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _vulkanDescriptorPoolResetCount, count);
                }

                public static void RecordVulkanQueueSubmit(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _vulkanQueueSubmitCount, count);
                }

                public static void RecordVulkanOomFallback(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _vulkanOomFallbackCount, count);
                }

                public static void RecordRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan submissionTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _rtxIoDecompressCalls);

                    if (compressedBytes > 0)
                        Interlocked.Add(ref _rtxIoCompressedBytes, compressedBytes);

                    if (decompressedBytes > 0)
                        Interlocked.Add(ref _rtxIoDecompressedBytes, decompressedBytes);

                    if (submissionTime.Ticks > 0)
                        Interlocked.Add(ref _rtxIoSubmissionTimeTicks, submissionTime.Ticks);
                }

                public static void RecordRtxIoCopyIndirect(long copiedBytes, TimeSpan submissionTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _rtxIoCopyIndirectCalls);

                    if (copiedBytes > 0)
                        Interlocked.Add(ref _rtxIoCopyBytes, copiedBytes);

                    if (submissionTime.Ticks > 0)
                        Interlocked.Add(ref _rtxIoSubmissionTimeTicks, submissionTime.Ticks);
                }

                public static void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftEyeDraws, (int)Math.Min(leftDraws, int.MaxValue));
                    Interlocked.Exchange(ref _vrRightEyeDraws, (int)Math.Min(rightDraws, int.MaxValue));
                }

                public static void RecordVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftEyeVisible, (int)Math.Min(leftVisible, int.MaxValue));
                    Interlocked.Exchange(ref _vrRightEyeVisible, (int)Math.Min(rightVisible, int.MaxValue));
                }

                public static void RecordVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrLeftWorkerBuildTimeTicks, leftBuildTime.Ticks);
                    Interlocked.Exchange(ref _vrRightWorkerBuildTimeTicks, rightBuildTime.Ticks);
                }

                public static void RecordVrRenderSubmitTime(TimeSpan submitTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vrRenderSubmitTimeTicks, submitTime.Ticks);
                }

                public static void RecordVulkanFrameLifecycleTiming(
                    TimeSpan waitFence,
                    TimeSpan acquireImage,
                    TimeSpan recordCommandBuffer,
                    TimeSpan submit,
                    TimeSpan trim,
                    TimeSpan present,
                    TimeSpan total)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vulkanFrameWaitFenceTicks, waitFence.Ticks);
                    Interlocked.Exchange(ref _vulkanFrameAcquireImageTicks, acquireImage.Ticks);
                    Interlocked.Exchange(ref _vulkanFrameRecordCommandBufferTicks, recordCommandBuffer.Ticks);
                    Interlocked.Exchange(ref _vulkanFrameSubmitTicks, submit.Ticks);
                    Interlocked.Exchange(ref _vulkanFrameTrimTicks, trim.Ticks);
                    Interlocked.Exchange(ref _vulkanFramePresentTicks, present.Ticks);
                    Interlocked.Exchange(ref _vulkanFrameTotalTicks, total.Ticks);
                }

                public static void RecordVulkanFrameGpuCommandBufferTime(TimeSpan commandBufferTime)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vulkanFrameGpuCommandBufferTicks, commandBufferTime.Ticks);
                }

                /// <summary>
                /// Swaps render-matrix stats from current to display buffer. Call from SwapBuffers phase.
                /// </summary>
                public static void SwapRenderMatrixStats()
                {
                    if (!EnableRenderMatrixStats)
                        return;

                    if (Interlocked.Exchange(ref _renderMatrixStatsDirty, 0) == 0)
                        return;

                    // Atomically copy current values to display and reset current.
                    _renderMatrixAppliedDisplay = Interlocked.Exchange(ref _renderMatrixAppliedCurrent, 0);
                    _renderMatrixBatchCountDisplay = Interlocked.Exchange(ref _renderMatrixBatchCountCurrent, 0);
                    _renderMatrixMaxBatchSizeDisplay = Interlocked.Exchange(ref _renderMatrixMaxBatchSizeCurrent, 0);
                    _renderMatrixSetCallsDisplay = Interlocked.Exchange(ref _renderMatrixSetCallsCurrent, 0);
                    _renderMatrixListenerInvocationsDisplay = Interlocked.Exchange(ref _renderMatrixListenerInvocationsCurrent, 0);

                    lock (_renderMatrixStatsLock)
                    {
                        var temp = _renderMatrixListenerCountsDisplay;
                        _renderMatrixListenerCountsDisplay = _renderMatrixListenerCountsCurrent;
                        _renderMatrixListenerCountsCurrent = temp;
                        _renderMatrixListenerCountsCurrent.Clear();
                    }

                    _renderMatrixStatsReady = true;
                }

                /// <summary>
                /// Swaps skinned-bounds refresh stats from current to display buffer. Call from SwapBuffers phase.
                /// </summary>
                public static void SwapSkinnedBoundsStats()
                {
                    if (!EnableSkinnedBoundsStats)
                        return;

                    if (Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 0) == 0)
                        return;

                    _skinnedBoundsDeferredScheduledDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredScheduledCurrent, 0);
                    _skinnedBoundsDeferredCompletedDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredCompletedCurrent, 0);
                    _skinnedBoundsDeferredFailedDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredFailedCurrent, 0);
                    _skinnedBoundsDeferredInFlightDisplay = Math.Max(0, Volatile.Read(ref _skinnedBoundsDeferredInFlightLive));
                    _skinnedBoundsDeferredMaxInFlightDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxInFlightCurrent, 0);
                    _skinnedBoundsDeferredQueueWaitTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredQueueWaitTicksCurrent, 0);
                    _skinnedBoundsDeferredCpuJobTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredCpuJobTicksCurrent, 0);
                    _skinnedBoundsDeferredApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredApplyTicksCurrent, 0);
                    _skinnedBoundsDeferredMaxQueueWaitTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxQueueWaitTicksCurrent, 0);
                    _skinnedBoundsDeferredMaxCpuJobTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxCpuJobTicksCurrent, 0);
                    _skinnedBoundsDeferredMaxApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsDeferredMaxApplyTicksCurrent, 0);
                    _skinnedBoundsGpuCompletedDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuCompletedCurrent, 0);
                    _skinnedBoundsGpuComputeTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuComputeTicksCurrent, 0);
                    _skinnedBoundsGpuApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuApplyTicksCurrent, 0);
                    _skinnedBoundsGpuMaxComputeTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuMaxComputeTicksCurrent, 0);
                    _skinnedBoundsGpuMaxApplyTicksDisplay = Interlocked.Exchange(ref _skinnedBoundsGpuMaxApplyTicksCurrent, 0);
                    _skinnedBoundsStatsReady = true;
                }

                /// <summary>
                /// Record the number of render-matrix updates applied during swap buffers.
                /// </summary>
                public static void RecordRenderMatrixApplied(int count)
                {
                    if (!EnableRenderMatrixStats || count <= 0)
                        return;

                    Interlocked.Add(ref _renderMatrixAppliedCurrent, count);
                    Interlocked.Increment(ref _renderMatrixBatchCountCurrent);
                    UpdateMaxCounter(ref _renderMatrixMaxBatchSizeCurrent, count);
                    Interlocked.Exchange(ref _renderMatrixStatsDirty, 1);
                }

                /// <summary>
                /// Record a render-matrix change event and (optionally) its listeners.
                /// </summary>
                public static void RecordRenderMatrixChange(Delegate? listeners)
                {
                    if (!EnableRenderMatrixStats)
                        return;

                    Interlocked.Increment(ref _renderMatrixSetCallsCurrent);
                    Interlocked.Exchange(ref _renderMatrixStatsDirty, 1);

                    if (!EnableRenderMatrixListenerTracking || listeners is null)
                        return;

                    var invocationList = listeners.GetInvocationList();
                    Interlocked.Add(ref _renderMatrixListenerInvocationsCurrent, invocationList.Length);

                    lock (_renderMatrixStatsLock)
                    {
                        foreach (var handler in invocationList)
                        {
                            var key = handler.Target?.GetType().Name ?? handler.Method.DeclaringType?.Name ?? "Static";
                            if (_renderMatrixListenerCountsCurrent.TryGetValue(key, out int current))
                                _renderMatrixListenerCountsCurrent[key] = current + 1;
                            else
                                _renderMatrixListenerCountsCurrent[key] = 1;
                        }
                    }
                }

                /// <summary>
                /// Returns the last-frame snapshot of render-matrix listener counts per listener type.
                /// </summary>
                public static KeyValuePair<string, int>[] GetRenderMatrixListenerSnapshot()
                {
                    lock (_renderMatrixStatsLock)
                    {
                        if (_renderMatrixListenerCountsDisplay.Count == 0)
                            return [];

                        var copy = new KeyValuePair<string, int>[_renderMatrixListenerCountsDisplay.Count];
                        int index = 0;
                        foreach (var pair in _renderMatrixListenerCountsDisplay)
                            copy[index++] = pair;
                        return copy;
                    }
                }

                public static void RecordSkinnedBoundsRefreshDeferredScheduled()
                {
                    if (!EnableSkinnedBoundsStats)
                        return;

                    int inFlight = Interlocked.Increment(ref _skinnedBoundsDeferredInFlightLive);
                    Interlocked.Increment(ref _skinnedBoundsDeferredScheduledCurrent);
                    UpdateMaxCounter(ref _skinnedBoundsDeferredMaxInFlightCurrent, inFlight);
                    Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 1);
                }

                public static void RecordSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded)
                {
                    if (!EnableSkinnedBoundsStats)
                        return;

                    if (succeeded)
                        Interlocked.Increment(ref _skinnedBoundsDeferredCompletedCurrent);
                    else
                        Interlocked.Increment(ref _skinnedBoundsDeferredFailedCurrent);

                    queueWaitTicks = Math.Max(0L, queueWaitTicks);
                    cpuJobTicks = Math.Max(0L, cpuJobTicks);
                    applyTicks = Math.Max(0L, applyTicks);

                    Interlocked.Add(ref _skinnedBoundsDeferredQueueWaitTicksCurrent, queueWaitTicks);
                    Interlocked.Add(ref _skinnedBoundsDeferredCpuJobTicksCurrent, cpuJobTicks);
                    Interlocked.Add(ref _skinnedBoundsDeferredApplyTicksCurrent, applyTicks);
                    UpdateMaxCounter(ref _skinnedBoundsDeferredMaxQueueWaitTicksCurrent, queueWaitTicks);
                    UpdateMaxCounter(ref _skinnedBoundsDeferredMaxCpuJobTicksCurrent, cpuJobTicks);
                    UpdateMaxCounter(ref _skinnedBoundsDeferredMaxApplyTicksCurrent, applyTicks);

                    int inFlight = Interlocked.Decrement(ref _skinnedBoundsDeferredInFlightLive);
                    if (inFlight < 0)
                    {
                        Interlocked.Exchange(ref _skinnedBoundsDeferredInFlightLive, 0);
                    }

                    Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 1);
                }

                public static void RecordSkinnedBoundsRefreshGpuCompleted(long computeTicks, long applyTicks)
                {
                    if (!EnableSkinnedBoundsStats)
                        return;

                    computeTicks = Math.Max(0L, computeTicks);
                    applyTicks = Math.Max(0L, applyTicks);

                    Interlocked.Increment(ref _skinnedBoundsGpuCompletedCurrent);
                    Interlocked.Add(ref _skinnedBoundsGpuComputeTicksCurrent, computeTicks);
                    Interlocked.Add(ref _skinnedBoundsGpuApplyTicksCurrent, applyTicks);
                    UpdateMaxCounter(ref _skinnedBoundsGpuMaxComputeTicksCurrent, computeTicks);
                    UpdateMaxCounter(ref _skinnedBoundsGpuMaxApplyTicksCurrent, applyTicks);
                    Interlocked.Exchange(ref _skinnedBoundsStatsDirty, 1);
                }

                private static void UpdateMaxCounter(ref int target, int candidate)
                {
                    int current;
                    while (candidate > (current = Volatile.Read(ref target)) &&
                           Interlocked.CompareExchange(ref target, candidate, current) != current)
                    {
                    }
                }

                private static void UpdateMaxCounter(ref long target, long candidate)
                {
                    long current;
                    while (candidate > (current = Interlocked.Read(ref target)) &&
                           Interlocked.CompareExchange(ref target, candidate, current) != current)
                    {
                    }
                }

                private static double StopwatchTicksToMilliseconds(long ticks)
                    => EngineTimer.TicksToSeconds(Math.Max(0L, ticks)) * 1000.0;

                // Octree stats
                private static readonly object _octreeTimingStatsLock = new();
                private static int _octreeCollectCallsCurrent;
                private static int _octreeVisibleRenderablesCurrent;
                private static int _octreeEmittedCommandsCurrent;
                private static int _octreeMaxVisibleRenderablesCurrent;
                private static int _octreeMaxEmittedCommandsCurrent;
                private static int _octreeAddCommandsCurrent;
                private static int _octreeMoveCommandsCurrent;
                private static int _octreeRemoveCommandsCurrent;
                private static int _octreeSkippedMovesCurrent;
                private static int _octreeSwapDrainedCommandsCurrent;
                private static int _octreeSwapBufferedCommandsCurrent;
                private static int _octreeSwapExecutedCommandsCurrent;
                private static long _octreeSwapDrainTicksCurrent;
                private static long _octreeSwapExecuteTicksCurrent;
                private static long _octreeSwapMaxCommandTicksCurrent;
                private static int _octreeSwapMaxCommandKindCurrent;
                private static int _octreeRaycastProcessedCommandsCurrent;
                private static int _octreeRaycastDroppedCommandsCurrent;
                private static long _octreeRaycastTraversalTicksCurrent;
                private static long _octreeRaycastCallbackTicksCurrent;
                private static long _octreeRaycastMaxTraversalTicksCurrent;
                private static long _octreeRaycastMaxCallbackTicksCurrent;
                private static long _octreeRaycastMaxCommandTicksCurrent;
                private static int _octreeCollectCallsDisplay;
                private static int _octreeVisibleRenderablesDisplay;
                private static int _octreeEmittedCommandsDisplay;
                private static int _octreeMaxVisibleRenderablesDisplay;
                private static int _octreeMaxEmittedCommandsDisplay;
                private static int _octreeAddCommandsDisplay;
                private static int _octreeMoveCommandsDisplay;
                private static int _octreeRemoveCommandsDisplay;
                private static int _octreeSkippedMovesDisplay;
                private static int _octreeSwapDrainedCommandsDisplay;
                private static int _octreeSwapBufferedCommandsDisplay;
                private static int _octreeSwapExecutedCommandsDisplay;
                private static long _octreeSwapDrainTicksDisplay;
                private static long _octreeSwapExecuteTicksDisplay;
                private static long _octreeSwapMaxCommandTicksDisplay;
                private static int _octreeSwapMaxCommandKindDisplay;
                private static int _octreeRaycastProcessedCommandsDisplay;
                private static int _octreeRaycastDroppedCommandsDisplay;
                private static long _octreeRaycastTraversalTicksDisplay;
                private static long _octreeRaycastCallbackTicksDisplay;
                private static long _octreeRaycastMaxTraversalTicksDisplay;
                private static long _octreeRaycastMaxCallbackTicksDisplay;
                private static long _octreeRaycastMaxCommandTicksDisplay;
                private static int _octreeStatsDirty;
                private static bool _octreeStatsReady;

                public static bool EnableOctreeStats { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif
                public static bool OctreeStatsReady => _octreeStatsReady;
                public static int OctreeCollectCallCount => _octreeCollectCallsDisplay;
                public static int OctreeVisibleRenderableCount => _octreeVisibleRenderablesDisplay;
                public static int OctreeEmittedCommandCount => _octreeEmittedCommandsDisplay;
                public static int OctreeMaxVisibleRenderablesPerCollect => _octreeMaxVisibleRenderablesDisplay;
                public static int OctreeMaxEmittedCommandsPerCollect => _octreeMaxEmittedCommandsDisplay;
                public static int OctreeAddCount => _octreeAddCommandsDisplay;
                public static int OctreeMoveCount => _octreeMoveCommandsDisplay;
                public static int OctreeRemoveCount => _octreeRemoveCommandsDisplay;
                public static int OctreeSkippedMoveCount => _octreeSkippedMovesDisplay;
                public static int OctreeSwapDrainedCommandCount => _octreeSwapDrainedCommandsDisplay;
                public static int OctreeSwapBufferedCommandCount => _octreeSwapBufferedCommandsDisplay;
                public static int OctreeSwapExecutedCommandCount => _octreeSwapExecutedCommandsDisplay;
                public static double OctreeSwapDrainMs => StopwatchTicksToMilliseconds(_octreeSwapDrainTicksDisplay);
                public static double OctreeSwapExecuteMs => StopwatchTicksToMilliseconds(_octreeSwapExecuteTicksDisplay);
                public static double OctreeSwapMaxCommandMs => StopwatchTicksToMilliseconds(_octreeSwapMaxCommandTicksDisplay);
                public static string OctreeSwapMaxCommandKind => GetOctreeCommandKindName(_octreeSwapMaxCommandKindDisplay);
                public static int OctreeRaycastProcessedCommandCount => _octreeRaycastProcessedCommandsDisplay;
                public static int OctreeRaycastDroppedCommandCount => _octreeRaycastDroppedCommandsDisplay;
                public static double OctreeRaycastTraversalMs => StopwatchTicksToMilliseconds(_octreeRaycastTraversalTicksDisplay);
                public static double OctreeRaycastCallbackMs => StopwatchTicksToMilliseconds(_octreeRaycastCallbackTicksDisplay);
                public static double OctreeRaycastMaxTraversalMs => StopwatchTicksToMilliseconds(_octreeRaycastMaxTraversalTicksDisplay);
                public static double OctreeRaycastMaxCallbackMs => StopwatchTicksToMilliseconds(_octreeRaycastMaxCallbackTicksDisplay);
                public static double OctreeRaycastMaxCommandMs => StopwatchTicksToMilliseconds(_octreeRaycastMaxCommandTicksDisplay);

                public static void RecordOctreeAdd()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeAddCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeMove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeMoveCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeRemove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeRemoveCommandsCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeSkippedMove()
                {
                    if (!EnableOctreeStats) return;
                    Interlocked.Increment(ref _octreeSkippedMovesCurrent);
                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeCollect(int visibleRenderables, int emittedCommands)
                {
                    if (!EnableOctreeStats)
                        return;

                    Interlocked.Increment(ref _octreeCollectCallsCurrent);
                    if (visibleRenderables > 0)
                    {
                        Interlocked.Add(ref _octreeVisibleRenderablesCurrent, visibleRenderables);
                        UpdateMaxCounter(ref _octreeMaxVisibleRenderablesCurrent, visibleRenderables);
                    }

                    if (emittedCommands > 0)
                    {
                        Interlocked.Add(ref _octreeEmittedCommandsCurrent, emittedCommands);
                        UpdateMaxCounter(ref _octreeMaxEmittedCommandsCurrent, emittedCommands);
                    }

                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeSwapTiming(OctreeSwapTimingStats stats)
                {
                    if (!EnableOctreeStats)
                        return;

                    lock (_octreeTimingStatsLock)
                    {
                        _octreeSwapDrainedCommandsCurrent += stats.DrainedCommandCount;
                        _octreeSwapBufferedCommandsCurrent += stats.BufferedCommandCount;
                        _octreeSwapExecutedCommandsCurrent += stats.ExecutedCommandCount;
                        _octreeSwapDrainTicksCurrent += stats.DrainTicks;
                        _octreeSwapExecuteTicksCurrent += stats.ExecuteTicks;

                        if (stats.MaxCommandTicks > _octreeSwapMaxCommandTicksCurrent)
                        {
                            _octreeSwapMaxCommandTicksCurrent = stats.MaxCommandTicks;
                            _octreeSwapMaxCommandKindCurrent = (int)stats.MaxCommandKind;
                        }
                    }

                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void RecordOctreeRaycastTiming(OctreeRaycastTimingStats stats)
                {
                    if (!EnableOctreeStats)
                        return;

                    lock (_octreeTimingStatsLock)
                    {
                        _octreeRaycastProcessedCommandsCurrent += stats.ProcessedCommandCount;
                        _octreeRaycastDroppedCommandsCurrent += stats.DroppedCommandCount;
                        _octreeRaycastTraversalTicksCurrent += stats.TraversalTicks;
                        _octreeRaycastCallbackTicksCurrent += stats.CallbackTicks;

                        if (stats.MaxTraversalTicks > _octreeRaycastMaxTraversalTicksCurrent)
                            _octreeRaycastMaxTraversalTicksCurrent = stats.MaxTraversalTicks;

                        if (stats.MaxCallbackTicks > _octreeRaycastMaxCallbackTicksCurrent)
                            _octreeRaycastMaxCallbackTicksCurrent = stats.MaxCallbackTicks;

                        if (stats.MaxCommandTicks > _octreeRaycastMaxCommandTicksCurrent)
                            _octreeRaycastMaxCommandTicksCurrent = stats.MaxCommandTicks;
                    }

                    Interlocked.Exchange(ref _octreeStatsDirty, 1);
                }

                public static void SwapOctreeStats()
                {
                    if (!EnableOctreeStats) return;
                    if (Interlocked.Exchange(ref _octreeStatsDirty, 0) == 0) return;

                    _octreeCollectCallsDisplay = Interlocked.Exchange(ref _octreeCollectCallsCurrent, 0);
                    _octreeVisibleRenderablesDisplay = Interlocked.Exchange(ref _octreeVisibleRenderablesCurrent, 0);
                    _octreeEmittedCommandsDisplay = Interlocked.Exchange(ref _octreeEmittedCommandsCurrent, 0);
                    _octreeMaxVisibleRenderablesDisplay = Interlocked.Exchange(ref _octreeMaxVisibleRenderablesCurrent, 0);
                    _octreeMaxEmittedCommandsDisplay = Interlocked.Exchange(ref _octreeMaxEmittedCommandsCurrent, 0);
                    _octreeAddCommandsDisplay = Interlocked.Exchange(ref _octreeAddCommandsCurrent, 0);
                    _octreeMoveCommandsDisplay = Interlocked.Exchange(ref _octreeMoveCommandsCurrent, 0);
                    _octreeRemoveCommandsDisplay = Interlocked.Exchange(ref _octreeRemoveCommandsCurrent, 0);
                    _octreeSkippedMovesDisplay = Interlocked.Exchange(ref _octreeSkippedMovesCurrent, 0);

                    lock (_octreeTimingStatsLock)
                    {
                        _octreeSwapDrainedCommandsDisplay = _octreeSwapDrainedCommandsCurrent;
                        _octreeSwapBufferedCommandsDisplay = _octreeSwapBufferedCommandsCurrent;
                        _octreeSwapExecutedCommandsDisplay = _octreeSwapExecutedCommandsCurrent;
                        _octreeSwapDrainTicksDisplay = _octreeSwapDrainTicksCurrent;
                        _octreeSwapExecuteTicksDisplay = _octreeSwapExecuteTicksCurrent;
                        _octreeSwapMaxCommandTicksDisplay = _octreeSwapMaxCommandTicksCurrent;
                        _octreeSwapMaxCommandKindDisplay = _octreeSwapMaxCommandKindCurrent;
                        _octreeRaycastProcessedCommandsDisplay = _octreeRaycastProcessedCommandsCurrent;
                        _octreeRaycastDroppedCommandsDisplay = _octreeRaycastDroppedCommandsCurrent;
                        _octreeRaycastTraversalTicksDisplay = _octreeRaycastTraversalTicksCurrent;
                        _octreeRaycastCallbackTicksDisplay = _octreeRaycastCallbackTicksCurrent;
                        _octreeRaycastMaxTraversalTicksDisplay = _octreeRaycastMaxTraversalTicksCurrent;
                        _octreeRaycastMaxCallbackTicksDisplay = _octreeRaycastMaxCallbackTicksCurrent;
                        _octreeRaycastMaxCommandTicksDisplay = _octreeRaycastMaxCommandTicksCurrent;

                        _octreeSwapDrainedCommandsCurrent = 0;
                        _octreeSwapBufferedCommandsCurrent = 0;
                        _octreeSwapExecutedCommandsCurrent = 0;
                        _octreeSwapDrainTicksCurrent = 0L;
                        _octreeSwapExecuteTicksCurrent = 0L;
                        _octreeSwapMaxCommandTicksCurrent = 0L;
                        _octreeSwapMaxCommandKindCurrent = 0;
                        _octreeRaycastProcessedCommandsCurrent = 0;
                        _octreeRaycastDroppedCommandsCurrent = 0;
                        _octreeRaycastTraversalTicksCurrent = 0L;
                        _octreeRaycastCallbackTicksCurrent = 0L;
                        _octreeRaycastMaxTraversalTicksCurrent = 0L;
                        _octreeRaycastMaxCallbackTicksCurrent = 0L;
                        _octreeRaycastMaxCommandTicksCurrent = 0L;
                    }

                    _octreeStatsReady = true;
                }

                private static string GetOctreeCommandKindName(int kind)
                    => kind switch
                    {
                        (int)EOctreeCommandKind.Add => nameof(EOctreeCommandKind.Add),
                        (int)EOctreeCommandKind.Move => nameof(EOctreeCommandKind.Move),
                        (int)EOctreeCommandKind.Remove => nameof(EOctreeCommandKind.Remove),
                        _ => nameof(EOctreeCommandKind.None),
                    };

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

                /// <summary>
                /// Records usage of GPU->CPU fallback recovery during culling.
                /// </summary>
                public static void RecordGpuCpuFallback(int eventCount, int recoveredCommands)
                {
                    if (!EnableTracking || eventCount <= 0)
                        return;

                    Interlocked.Add(ref _gpuCpuFallbackEvents, eventCount);
                    if (recoveredCommands > 0)
                        Interlocked.Add(ref _gpuCpuFallbackRecoveredCommands, recoveredCommands);
                }

                /// <summary>
                /// Records a forbidden fallback attempt (fallback blocked by profile policy).
                /// </summary>
                public static void RecordForbiddenGpuFallback(int eventCount = 1)
                {
                    if (!EnableTracking || eventCount <= 0)
                        return;

                    Interlocked.Add(ref _forbiddenGpuFallbackEvents, eventCount);
                }

                public static void RecordVulkanIndirectSubmission(bool usedCountPath, bool usedLoopFallback, int apiCalls, uint submittedDraws)
                {
                    if (!EnableTracking)
                        return;

                    if (usedCountPath)
                        Interlocked.Increment(ref _vulkanIndirectCountPathCalls);
                    else
                        Interlocked.Increment(ref _vulkanIndirectNonCountPathCalls);

                    if (usedLoopFallback)
                        Interlocked.Increment(ref _vulkanIndirectLoopFallbackCalls);

                    if (apiCalls > 0)
                        Interlocked.Add(ref _vulkanIndirectApiCalls, apiCalls);

                    if (submittedDraws > 0)
                        Interlocked.Add(ref _vulkanIndirectSubmittedDraws, submittedDraws);
                }

                public static void RecordVulkanIndirectBatchMerge(int requestedBatchCount, int mergedBatchCount)
                {
                    if (!EnableTracking)
                        return;

                    if (requestedBatchCount > 0)
                        Interlocked.Add(ref _vulkanIndirectRequestedBatches, requestedBatchCount);

                    if (mergedBatchCount > 0)
                        Interlocked.Add(ref _vulkanIndirectMergedBatches, mergedBatchCount);
                }

                public static void RecordVulkanIndirectRecordingMode(bool usedSecondary, bool usedParallel, int opCount)
                {
                    if (!EnableTracking || opCount <= 0)
                        return;

                    if (!usedSecondary)
                    {
                        Interlocked.Add(ref _vulkanIndirectPrimaryRecordOps, opCount);
                        return;
                    }

                    if (usedParallel)
                        Interlocked.Add(ref _vulkanIndirectParallelSecondaryRecordOps, opCount);
                    else
                        Interlocked.Add(ref _vulkanIndirectSecondaryRecordOps, opCount);
                }

                public static void RecordVulkanBarrierPlannerPass(int imageBarrierCount, int bufferBarrierCount, int queueOwnershipTransfers, int stageFlushes)
                {
                    if (!EnableTracking)
                        return;

                    if (imageBarrierCount > 0)
                        Interlocked.Add(ref _vulkanPlannedImageBarriers, imageBarrierCount);

                    if (bufferBarrierCount > 0)
                        Interlocked.Add(ref _vulkanPlannedBufferBarriers, bufferBarrierCount);

                    if (queueOwnershipTransfers > 0)
                        Interlocked.Add(ref _vulkanQueueOwnershipTransfers, queueOwnershipTransfers);

                    if (stageFlushes > 0)
                        Interlocked.Add(ref _vulkanBarrierStageFlushes, stageFlushes);
                }

                public static void RecordVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode)
                {
                    if (!EnableTracking)
                        return;

                    if (overlapCandidatePasses > 0)
                        Interlocked.Add(ref _vulkanOverlapCandidatePasses, overlapCandidatePasses);

                    if (transferCost > 0)
                        Interlocked.Add(ref _vulkanOverlapTransferCosts, transferCost);

                    if (frameDelta.Ticks > 0)
                    {
                        long micros = Math.Max(1L, frameDelta.Ticks / 10L);
                        Interlocked.Add(ref _vulkanOverlapFrameDeltaMicros, micros);
                    }

                    if (promotedMode)
                        Interlocked.Increment(ref _vulkanOverlapModePromotions);

                    if (demotedMode)
                        Interlocked.Increment(ref _vulkanOverlapModeDemotions);
                }

                public static void RecordVulkanAdhocBarrier(int emittedCount, int redundantCount)
                {
                    if (!EnableTracking)
                        return;

                    if (emittedCount > 0)
                        Interlocked.Add(ref _vulkanAdhocBarrierEmits, emittedCount);

                    if (redundantCount > 0)
                        Interlocked.Add(ref _vulkanAdhocBarrierRedundant, redundantCount);
                }

                public static void RecordVulkanBindChurn(
                    int pipelineBinds = 0,
                    int descriptorBinds = 0,
                    int pushConstantWrites = 0,
                    int vertexBufferBinds = 0,
                    int indexBufferBinds = 0,
                    int pipelineBindSkips = 0,
                    int descriptorBindSkips = 0,
                    int vertexBufferBindSkips = 0,
                    int indexBufferBindSkips = 0)
                {
                    if (!EnableTracking)
                        return;

                    if (pipelineBinds > 0)
                        Interlocked.Add(ref _vulkanPipelineBinds, pipelineBinds);
                    if (descriptorBinds > 0)
                        Interlocked.Add(ref _vulkanDescriptorBinds, descriptorBinds);
                    if (pushConstantWrites > 0)
                        Interlocked.Add(ref _vulkanPushConstantWrites, pushConstantWrites);
                    if (vertexBufferBinds > 0)
                        Interlocked.Add(ref _vulkanVertexBufferBinds, vertexBufferBinds);
                    if (indexBufferBinds > 0)
                        Interlocked.Add(ref _vulkanIndexBufferBinds, indexBufferBinds);
                    if (pipelineBindSkips > 0)
                        Interlocked.Add(ref _vulkanPipelineBindSkips, pipelineBindSkips);
                    if (descriptorBindSkips > 0)
                        Interlocked.Add(ref _vulkanDescriptorBindSkips, descriptorBindSkips);
                    if (vertexBufferBindSkips > 0)
                        Interlocked.Add(ref _vulkanVertexBufferBindSkips, vertexBufferBindSkips);
                    if (indexBufferBindSkips > 0)
                        Interlocked.Add(ref _vulkanIndexBufferBindSkips, indexBufferBindSkips);
                }

                public static void RecordVulkanPipelineCacheLookup(bool cacheHit)
                {
                    if (!EnableTracking)
                        return;

                    if (cacheHit)
                        Interlocked.Increment(ref _vulkanPipelineCacheLookupHits);
                    else
                        Interlocked.Increment(ref _vulkanPipelineCacheLookupMisses);
                }

                public static void RecordVulkanIndirectEffectiveness(
                    uint requestedDraws,
                    uint culledDraws,
                    uint emittedIndirectDraws,
                    uint consumedDraws,
                    uint overflowCount)
                {
                    if (!EnableTracking)
                        return;

                    if (requestedDraws > 0)
                        Interlocked.Add(ref _vulkanRequestedDraws, requestedDraws);

                    if (culledDraws > 0)
                        Interlocked.Add(ref _vulkanCulledDraws, culledDraws);

                    if (emittedIndirectDraws > 0)
                        Interlocked.Add(ref _vulkanEmittedIndirectDraws, emittedIndirectDraws);

                    if (consumedDraws > 0)
                        Interlocked.Add(ref _vulkanConsumedDraws, consumedDraws);

                    if (overflowCount > 0)
                        Interlocked.Add(ref _vulkanOverflowCount, overflowCount);
                }

                public static void RecordGpuTransparencyDomainCounts(
                    uint opaqueOrOtherVisible,
                    uint maskedVisible,
                    uint approximateVisible,
                    uint exactVisible)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _gpuTransparencyOpaqueOrOtherVisible, checked((int)opaqueOrOtherVisible));
                    Interlocked.Exchange(ref _gpuTransparencyMaskedVisible, checked((int)maskedVisible));
                    Interlocked.Exchange(ref _gpuTransparencyApproximateVisible, checked((int)approximateVisible));
                    Interlocked.Exchange(ref _gpuTransparencyExactVisible, checked((int)exactVisible));
                }

                public enum EVulkanGpuDrivenStageTiming
                {
                    Reset = 0,
                    Cull,
                    Occlusion,
                    Indirect,
                    Draw
                }

                public static void RecordVulkanGpuDrivenStageTiming(EVulkanGpuDrivenStageTiming stage, TimeSpan elapsed)
                {
                    if (!EnableTracking || elapsed.Ticks <= 0)
                        return;

                    switch (stage)
                    {
                        case EVulkanGpuDrivenStageTiming.Reset:
                            Interlocked.Add(ref _vulkanStageResetTicks, elapsed.Ticks);
                            break;
                        case EVulkanGpuDrivenStageTiming.Cull:
                            Interlocked.Add(ref _vulkanStageCullTicks, elapsed.Ticks);
                            break;
                        case EVulkanGpuDrivenStageTiming.Occlusion:
                            Interlocked.Add(ref _vulkanStageOcclusionTicks, elapsed.Ticks);
                            break;
                        case EVulkanGpuDrivenStageTiming.Indirect:
                            Interlocked.Add(ref _vulkanStageIndirectTicks, elapsed.Ticks);
                            break;
                        case EVulkanGpuDrivenStageTiming.Draw:
                            Interlocked.Add(ref _vulkanStageDrawTicks, elapsed.Ticks);
                            break;
                    }
                }

                /// <summary>
                /// Record a GPU buffer memory allocation.
                /// </summary>
                /// <param name="bytes">The number of bytes allocated.</param>
                public static void AddBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedBufferBytes, bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                }

                /// <summary>
                /// Record a GPU buffer memory deallocation.
                /// </summary>
                /// <param name="bytes">The number of bytes deallocated.</param>
                public static void RemoveBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedBufferBytes, -bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                }

                /// <summary>
                /// Record a GPU texture memory allocation.
                /// </summary>
                /// <param name="bytes">The number of bytes allocated.</param>
                public static void AddTextureAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedTextureBytes, bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                }

                /// <summary>
                /// Record a GPU texture memory deallocation.
                /// </summary>
                /// <param name="bytes">The number of bytes deallocated.</param>
                public static void RemoveTextureAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedTextureBytes, -bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                }

                /// <summary>
                /// Record a GPU render buffer memory allocation.
                /// </summary>
                /// <param name="bytes">The number of bytes allocated.</param>
                public static void AddRenderBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedRenderBufferBytes, bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, bytes);
                }

                /// <summary>
                /// Record a GPU render buffer memory deallocation.
                /// </summary>
                /// <param name="bytes">The number of bytes deallocated.</param>
                public static void RemoveRenderBufferAllocation(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _allocatedRenderBufferBytes, -bytes);
                    Interlocked.Add(ref _allocatedVRAMBytes, -bytes);
                }

                /// <summary>
                /// Record FBO render bandwidth when an FBO is bound for writing.
                /// The bandwidth is calculated as the total size of all render target attachments.
                /// </summary>
                /// <param name="bytes">The total size of all render target attachments in bytes.</param>
                public static void AddFBOBandwidth(long bytes)
                {
                    if (bytes <= 0) return;
                    Interlocked.Add(ref _fboBandwidthBytes, bytes);
                    Interlocked.Increment(ref _fboBindCount);
                }

                /// <summary>
                /// Gets the bytes per pixel for a given sized internal format.
                /// </summary>
                public static int GetBytesPerPixel(ESizedInternalFormat format)
                {
                    return format switch
                    {
                        // 1-byte formats
                        ESizedInternalFormat.R8 => 1,
                        ESizedInternalFormat.R8Snorm => 1,
                        ESizedInternalFormat.R8i => 1,
                        ESizedInternalFormat.R8ui => 1,
                        ESizedInternalFormat.StencilIndex8 => 1,

                        // 2-byte formats
                        ESizedInternalFormat.R16 => 2,
                        ESizedInternalFormat.R16Snorm => 2,
                        ESizedInternalFormat.R16f => 2,
                        ESizedInternalFormat.R16i => 2,
                        ESizedInternalFormat.R16ui => 2,
                        ESizedInternalFormat.Rg8 => 2,
                        ESizedInternalFormat.Rg8Snorm => 2,
                        ESizedInternalFormat.Rg8i => 2,
                        ESizedInternalFormat.Rg8ui => 2,
                        ESizedInternalFormat.DepthComponent16 => 2,

                        // 3-byte formats
                        ESizedInternalFormat.Rgb8 => 3,
                        ESizedInternalFormat.Rgb8Snorm => 3,
                        ESizedInternalFormat.Srgb8 => 3,
                        ESizedInternalFormat.Rgb8i => 3,
                        ESizedInternalFormat.Rgb8ui => 3,
                        ESizedInternalFormat.DepthComponent24 => 3,

                        // 4-byte formats
                        ESizedInternalFormat.R32f => 4,
                        ESizedInternalFormat.R32i => 4,
                        ESizedInternalFormat.R32ui => 4,
                        ESizedInternalFormat.Rg16 => 4,
                        ESizedInternalFormat.Rg16Snorm => 4,
                        ESizedInternalFormat.Rg16f => 4,
                        ESizedInternalFormat.Rg16i => 4,
                        ESizedInternalFormat.Rg16ui => 4,
                        ESizedInternalFormat.Rgba8 => 4,
                        ESizedInternalFormat.Rgba8Snorm => 4,
                        ESizedInternalFormat.Srgb8Alpha8 => 4,
                        ESizedInternalFormat.Rgba8i => 4,
                        ESizedInternalFormat.Rgba8ui => 4,
                        ESizedInternalFormat.Rgb10A2 => 4,
                        ESizedInternalFormat.R11fG11fB10f => 4,
                        ESizedInternalFormat.Rgb9E5 => 4,
                        ESizedInternalFormat.DepthComponent32f => 4,
                        ESizedInternalFormat.Depth24Stencil8 => 4,

                        // 5-byte formats
                        ESizedInternalFormat.Depth32fStencil8 => 5,

                        // 6-byte formats
                        ESizedInternalFormat.Rgb16f => 6,
                        ESizedInternalFormat.Rgb16Snorm => 6,
                        ESizedInternalFormat.Rgb16i => 6,
                        ESizedInternalFormat.Rgb16ui => 6,

                        // 8-byte formats
                        ESizedInternalFormat.Rg32f => 8,
                        ESizedInternalFormat.Rg32i => 8,
                        ESizedInternalFormat.Rg32ui => 8,
                        ESizedInternalFormat.Rgba16 => 8,
                        ESizedInternalFormat.Rgba16f => 8,
                        ESizedInternalFormat.Rgba16i => 8,
                        ESizedInternalFormat.Rgba16ui => 8,

                        // 12-byte formats
                        ESizedInternalFormat.Rgb32f => 12,
                        ESizedInternalFormat.Rgb32i => 12,
                        ESizedInternalFormat.Rgb32ui => 12,

                        // 16-byte formats
                        ESizedInternalFormat.Rgba32f => 16,
                        ESizedInternalFormat.Rgba32i => 16,
                        ESizedInternalFormat.Rgba32ui => 16,

                        // Default fallback (estimate 4 bytes for unknown formats)
                        _ => 4
                    };
                }

                /// <summary>
                /// Gets the bytes per pixel for a given render buffer storage format.
                /// </summary>
                public static int GetBytesPerPixel(ERenderBufferStorage format)
                {
                    return format switch
                    {
                        // 1-byte formats
                        ERenderBufferStorage.R8 => 1,
                        ERenderBufferStorage.R8i => 1,
                        ERenderBufferStorage.R8ui => 1,
                        ERenderBufferStorage.StencilIndex1 => 1,
                        ERenderBufferStorage.StencilIndex4 => 1,
                        ERenderBufferStorage.StencilIndex8 => 1,

                        // 2-byte formats
                        ERenderBufferStorage.R16 => 2,
                        ERenderBufferStorage.R16f => 2,
                        ERenderBufferStorage.R16i => 2,
                        ERenderBufferStorage.R16ui => 2,
                        ERenderBufferStorage.DepthComponent16 => 2,
                        ERenderBufferStorage.StencilIndex16 => 2,

                        // 3-byte formats
                        ERenderBufferStorage.Rgb8 => 3,
                        ERenderBufferStorage.Srgb8 => 3,
                        ERenderBufferStorage.Rgb8i => 3,
                        ERenderBufferStorage.Rgb8ui => 3,
                        ERenderBufferStorage.DepthComponent24 => 3,

                        // 4-byte formats
                        ERenderBufferStorage.R32f => 4,
                        ERenderBufferStorage.R32i => 4,
                        ERenderBufferStorage.R32ui => 4,
                        ERenderBufferStorage.Rgba8 => 4,
                        ERenderBufferStorage.Srgb8Alpha8 => 4,
                        ERenderBufferStorage.Rgba8i => 4,
                        ERenderBufferStorage.Rgba8ui => 4,
                        ERenderBufferStorage.Rgb10A2 => 4,
                        ERenderBufferStorage.Rgb10A2ui => 4,
                        ERenderBufferStorage.R11fG11fB10f => 4,
                        ERenderBufferStorage.Rgb9E5 => 4,
                        ERenderBufferStorage.DepthComponent32 => 4,
                        ERenderBufferStorage.DepthComponent32f => 4,
                        ERenderBufferStorage.Depth24Stencil8 => 4,
                        ERenderBufferStorage.DepthComponent => 4,
                        ERenderBufferStorage.DepthStencil => 4,

                        // 5-byte formats
                        ERenderBufferStorage.Depth32fStencil8 => 5,

                        // 6-byte formats
                        ERenderBufferStorage.Rgb16 => 6,
                        ERenderBufferStorage.Rgb16f => 6,
                        ERenderBufferStorage.Rgb16i => 6,
                        ERenderBufferStorage.Rgb16ui => 6,

                        // 8-byte formats
                        ERenderBufferStorage.Rgba16 => 8,
                        ERenderBufferStorage.Rgba16f => 8,
                        ERenderBufferStorage.Rgba16i => 8,
                        ERenderBufferStorage.Rgba16ui => 8,

                        // 12-byte formats
                        ERenderBufferStorage.Rgb32f => 12,
                        ERenderBufferStorage.Rgb32i => 12,
                        ERenderBufferStorage.Rgb32ui => 12,

                        // 16-byte formats
                        ERenderBufferStorage.Rgba32f => 16,
                        ERenderBufferStorage.Rgba32i => 16,
                        ERenderBufferStorage.Rgba32ui => 16,

                        // Default fallback (estimate 4 bytes for unknown formats)
                        _ => 4
                    };
                }
            }
        }
    }
}
