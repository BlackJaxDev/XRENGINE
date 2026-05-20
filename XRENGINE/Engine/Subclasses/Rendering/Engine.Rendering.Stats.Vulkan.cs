using System;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
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
                private static string _vulkanPipelineCacheMissSummary = string.Empty;
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
                private static string _lastFrameVulkanPipelineCacheMissSummary = string.Empty;
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
                private static int _vulkanDroppedFrameOps;
                private static int _vulkanDroppedDrawOps;
                private static int _vulkanDroppedComputeOps;
                private static int _vulkanSceneSwapchainWriters;
                private static int _vulkanOverlaySwapchainWriters;
                private static int _vulkanForcedDiagnosticSwapchainWriters;
                private static int _vulkanFboOnlyDrawOps;
                private static int _vulkanFboOnlyBlitOps;
                private static int _vulkanMissingSceneSwapchainWriteFrames;
                private static int _vulkanFirstFailedFrameOpPassIndex = int.MinValue;
                private static int _vulkanFirstFailedFrameOpPipelineIdentity;
                private static int _vulkanFirstFailedFrameOpViewportIdentity;
                private static string _vulkanFirstFailedFrameOpType = string.Empty;
                private static string _vulkanFirstFailedFrameOpTargetName = string.Empty;
                private static string _vulkanFirstFailedFrameOpMaterialName = string.Empty;
                private static string _vulkanFirstFailedFrameOpShaderName = string.Empty;
                private static string _vulkanFirstFailedFrameOpMessage = string.Empty;
                private static string _vulkanFrameDiagnosticSummary = string.Empty;
                private static int _lastFrameVulkanDroppedFrameOps;
                private static int _lastFrameVulkanDroppedDrawOps;
                private static int _lastFrameVulkanDroppedComputeOps;
                private static int _lastFrameVulkanSceneSwapchainWriters;
                private static int _lastFrameVulkanOverlaySwapchainWriters;
                private static int _lastFrameVulkanForcedDiagnosticSwapchainWriters;
                private static int _lastFrameVulkanFboOnlyDrawOps;
                private static int _lastFrameVulkanFboOnlyBlitOps;
                private static int _lastFrameVulkanMissingSceneSwapchainWriteFrames;
                private static int _lastFrameVulkanFirstFailedFrameOpPassIndex = int.MinValue;
                private static int _lastFrameVulkanFirstFailedFrameOpPipelineIdentity;
                private static int _lastFrameVulkanFirstFailedFrameOpViewportIdentity;
                private static string _lastFrameVulkanFirstFailedFrameOpType = string.Empty;
                private static string _lastFrameVulkanFirstFailedFrameOpTargetName = string.Empty;
                private static string _lastFrameVulkanFirstFailedFrameOpMaterialName = string.Empty;
                private static string _lastFrameVulkanFirstFailedFrameOpShaderName = string.Empty;
                private static string _lastFrameVulkanFirstFailedFrameOpMessage = string.Empty;
                private static string _lastFrameVulkanFrameDiagnosticSummary = string.Empty;
                private static int _vulkanValidationMessageCount;
                private static int _vulkanValidationErrorCount;
                private static string _vulkanLastValidationMessage = string.Empty;
                private static int _lastFrameVulkanValidationMessageCount;
                private static int _lastFrameVulkanValidationErrorCount;
                private static string _lastFrameVulkanLastValidationMessage = string.Empty;
                private static int _vulkanDescriptorFallbackSampledImages;
                private static int _vulkanDescriptorFallbackStorageImages;
                private static int _vulkanDescriptorFallbackUniformBuffers;
                private static int _vulkanDescriptorFallbackStorageBuffers;
                private static int _vulkanDescriptorFallbackTexelBuffers;
                private static int _vulkanDescriptorBindingFailures;
                private static int _vulkanDescriptorSkippedDraws;
                private static int _vulkanDescriptorSkippedDispatches;
                private static string _vulkanDescriptorFallbackSummary = string.Empty;
                private static string _vulkanDescriptorFailureSummary = string.Empty;
                private static int _lastFrameVulkanDescriptorFallbackSampledImages;
                private static int _lastFrameVulkanDescriptorFallbackStorageImages;
                private static int _lastFrameVulkanDescriptorFallbackUniformBuffers;
                private static int _lastFrameVulkanDescriptorFallbackStorageBuffers;
                private static int _lastFrameVulkanDescriptorFallbackTexelBuffers;
                private static int _lastFrameVulkanDescriptorBindingFailures;
                private static int _lastFrameVulkanDescriptorSkippedDraws;
                private static int _lastFrameVulkanDescriptorSkippedDispatches;
                private static string _lastFrameVulkanDescriptorFallbackSummary = string.Empty;
                private static string _lastFrameVulkanDescriptorFailureSummary = string.Empty;
                private static int _vulkanDynamicUniformAllocations;
                private static long _vulkanDynamicUniformAllocatedBytes;
                private static int _vulkanDynamicUniformExhaustions;
                private static int _lastFrameVulkanDynamicUniformAllocations;
                private static long _lastFrameVulkanDynamicUniformAllocatedBytes;
                private static int _lastFrameVulkanDynamicUniformExhaustions;
                private static int _vulkanRetiredResourcePlanReplacements;
                private static int _vulkanRetiredResourcePlanImages;
                private static int _vulkanRetiredResourcePlanBuffers;
                private static int _lastFrameVulkanRetiredResourcePlanReplacements;
                private static int _lastFrameVulkanRetiredResourcePlanImages;
                private static int _lastFrameVulkanRetiredResourcePlanBuffers;
                private static readonly object _vulkanDiagnosticLock = new();

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
                public static string VulkanPipelineCacheMissSummary => _lastFrameVulkanPipelineCacheMissSummary;
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
                public static int VulkanDroppedFrameOps => _lastFrameVulkanDroppedFrameOps;
                public static int VulkanDroppedDrawOps => _lastFrameVulkanDroppedDrawOps;
                public static int VulkanDroppedComputeOps => _lastFrameVulkanDroppedComputeOps;
                public static int VulkanSceneSwapchainWriters => _lastFrameVulkanSceneSwapchainWriters;
                public static int VulkanOverlaySwapchainWriters => _lastFrameVulkanOverlaySwapchainWriters;
                public static int VulkanForcedDiagnosticSwapchainWriters => _lastFrameVulkanForcedDiagnosticSwapchainWriters;
                public static int VulkanFboOnlyDrawOps => _lastFrameVulkanFboOnlyDrawOps;
                public static int VulkanFboOnlyBlitOps => _lastFrameVulkanFboOnlyBlitOps;
                public static int VulkanMissingSceneSwapchainWriteFrames => _lastFrameVulkanMissingSceneSwapchainWriteFrames;
                public static int VulkanFirstFailedFrameOpPassIndex => _lastFrameVulkanFirstFailedFrameOpPassIndex;
                public static int VulkanFirstFailedFrameOpPipelineIdentity => _lastFrameVulkanFirstFailedFrameOpPipelineIdentity;
                public static int VulkanFirstFailedFrameOpViewportIdentity => _lastFrameVulkanFirstFailedFrameOpViewportIdentity;
                public static string VulkanFirstFailedFrameOpType => _lastFrameVulkanFirstFailedFrameOpType;
                public static string VulkanFirstFailedFrameOpTargetName => _lastFrameVulkanFirstFailedFrameOpTargetName;
                public static string VulkanFirstFailedFrameOpMaterialName => _lastFrameVulkanFirstFailedFrameOpMaterialName;
                public static string VulkanFirstFailedFrameOpShaderName => _lastFrameVulkanFirstFailedFrameOpShaderName;
                public static string VulkanFirstFailedFrameOpMessage => _lastFrameVulkanFirstFailedFrameOpMessage;
                public static string VulkanFrameDiagnosticSummary => _lastFrameVulkanFrameDiagnosticSummary;
                public static int VulkanValidationMessageCount => _lastFrameVulkanValidationMessageCount;
                public static int VulkanValidationErrorCount => _lastFrameVulkanValidationErrorCount;
                public static string VulkanLastValidationMessage => _lastFrameVulkanLastValidationMessage;
                public static int VulkanValidationMessageCountCurrentFrame => Volatile.Read(ref _vulkanValidationMessageCount);
                public static int VulkanValidationErrorCountCurrentFrame => Volatile.Read(ref _vulkanValidationErrorCount);
                public static int VulkanDescriptorFallbackSampledImages => _lastFrameVulkanDescriptorFallbackSampledImages;
                public static int VulkanDescriptorFallbackStorageImages => _lastFrameVulkanDescriptorFallbackStorageImages;
                public static int VulkanDescriptorFallbackUniformBuffers => _lastFrameVulkanDescriptorFallbackUniformBuffers;
                public static int VulkanDescriptorFallbackStorageBuffers => _lastFrameVulkanDescriptorFallbackStorageBuffers;
                public static int VulkanDescriptorFallbackTexelBuffers => _lastFrameVulkanDescriptorFallbackTexelBuffers;
                public static int VulkanDescriptorBindingFailures => _lastFrameVulkanDescriptorBindingFailures;
                public static int VulkanDescriptorSkippedDraws => _lastFrameVulkanDescriptorSkippedDraws;
                public static int VulkanDescriptorSkippedDispatches => _lastFrameVulkanDescriptorSkippedDispatches;
                public static string VulkanDescriptorFallbackSummary => _lastFrameVulkanDescriptorFallbackSummary;
                public static string VulkanDescriptorFailureSummary => _lastFrameVulkanDescriptorFailureSummary;
                public static int VulkanDescriptorFallbacksCurrentFrame =>
                    Volatile.Read(ref _vulkanDescriptorFallbackSampledImages) +
                    Volatile.Read(ref _vulkanDescriptorFallbackStorageImages) +
                    Volatile.Read(ref _vulkanDescriptorFallbackUniformBuffers) +
                    Volatile.Read(ref _vulkanDescriptorFallbackStorageBuffers) +
                    Volatile.Read(ref _vulkanDescriptorFallbackTexelBuffers);
                public static int VulkanDescriptorBindingFailuresCurrentFrame => Volatile.Read(ref _vulkanDescriptorBindingFailures);
                public static int VulkanDynamicUniformAllocations => _lastFrameVulkanDynamicUniformAllocations;
                public static long VulkanDynamicUniformAllocatedBytes => _lastFrameVulkanDynamicUniformAllocatedBytes;
                public static int VulkanDynamicUniformExhaustions => _lastFrameVulkanDynamicUniformExhaustions;
                public static int VulkanRetiredResourcePlanReplacements => _lastFrameVulkanRetiredResourcePlanReplacements;
                public static int VulkanRetiredResourcePlanImages => _lastFrameVulkanRetiredResourcePlanImages;
                public static int VulkanRetiredResourcePlanBuffers => _lastFrameVulkanRetiredResourcePlanBuffers;
                public static double VulkanPipelineCacheLookupHitRate
                    => (_lastFrameVulkanPipelineCacheLookupHits + _lastFrameVulkanPipelineCacheLookupMisses) <= 0
                        ? 1.0
                        : (double)_lastFrameVulkanPipelineCacheLookupHits /
                            (_lastFrameVulkanPipelineCacheLookupHits + _lastFrameVulkanPipelineCacheLookupMisses);
                public static double VulkanIndirectBatchMergeRatio
                    => _lastFrameVulkanIndirectRequestedBatches <= 0
                        ? 1.0
                        : (double)_lastFrameVulkanIndirectMergedBatches / _lastFrameVulkanIndirectRequestedBatches;

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

                public static void RecordVulkanFrameDiagnostics(
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
                    string? diagnosticSummary)
                {
                    if (!EnableTracking)
                        return;

                    AddNonNegative(ref _vulkanDroppedFrameOps, droppedFrameOps);
                    AddNonNegative(ref _vulkanDroppedDrawOps, droppedDrawOps);
                    AddNonNegative(ref _vulkanDroppedComputeOps, droppedComputeOps);
                    AddNonNegative(ref _vulkanSceneSwapchainWriters, sceneSwapchainWriters);
                    AddNonNegative(ref _vulkanOverlaySwapchainWriters, overlaySwapchainWriters);
                    AddNonNegative(ref _vulkanForcedDiagnosticSwapchainWriters, forcedDiagnosticSwapchainWriters);
                    AddNonNegative(ref _vulkanFboOnlyDrawOps, fboOnlyDrawOps);
                    AddNonNegative(ref _vulkanFboOnlyBlitOps, fboOnlyBlitOps);

                    if (missingSceneSwapchainWriters)
                        Interlocked.Increment(ref _vulkanMissingSceneSwapchainWriteFrames);

                    bool hasFirstFailure = !string.IsNullOrWhiteSpace(firstFailedOpType);
                    bool hasDiagnosticSummary = !string.IsNullOrWhiteSpace(diagnosticSummary);
                    if (!hasFirstFailure && !hasDiagnosticSummary)
                        return;

                    lock (_vulkanDiagnosticLock)
                    {
                        if (hasFirstFailure && string.IsNullOrEmpty(_vulkanFirstFailedFrameOpType))
                        {
                            _vulkanFirstFailedFrameOpType = firstFailedOpType!;
                            _vulkanFirstFailedFrameOpPassIndex = firstFailedPassIndex;
                            _vulkanFirstFailedFrameOpPipelineIdentity = firstFailedPipelineIdentity;
                            _vulkanFirstFailedFrameOpViewportIdentity = firstFailedViewportIdentity;
                            _vulkanFirstFailedFrameOpTargetName = firstFailedTargetName ?? string.Empty;
                            _vulkanFirstFailedFrameOpMaterialName = firstFailedMaterialName ?? string.Empty;
                            _vulkanFirstFailedFrameOpShaderName = firstFailedShaderName ?? string.Empty;
                            _vulkanFirstFailedFrameOpMessage = TruncateDiagnosticText(firstFailedMessage);
                        }

                        if (hasDiagnosticSummary)
                            _vulkanFrameDiagnosticSummary = TruncateDiagnosticText(diagnosticSummary, 2048);
                    }
                }

                public static void RecordVulkanValidationMessage(bool isError, string? message)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanValidationMessageCount);
                    if (isError)
                        Interlocked.Increment(ref _vulkanValidationErrorCount);

                    lock (_vulkanDiagnosticLock)
                        _vulkanLastValidationMessage = TruncateDiagnosticText(message, 1024);
                }

                public static void RecordVulkanDescriptorFallback(
                    string? programName,
                    string? bindingClass,
                    string? bindingName,
                    uint set,
                    uint binding,
                    int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    switch (NormalizeDescriptorBindingClass(bindingClass))
                    {
                        case "storage-image":
                            Interlocked.Add(ref _vulkanDescriptorFallbackStorageImages, count);
                            break;
                        case "uniform-buffer":
                            Interlocked.Add(ref _vulkanDescriptorFallbackUniformBuffers, count);
                            break;
                        case "storage-buffer":
                            Interlocked.Add(ref _vulkanDescriptorFallbackStorageBuffers, count);
                            break;
                        case "texel-buffer":
                            Interlocked.Add(ref _vulkanDescriptorFallbackTexelBuffers, count);
                            break;
                        default:
                            Interlocked.Add(ref _vulkanDescriptorFallbackSampledImages, count);
                            break;
                    }

                    string summary = $"{programName ?? "<program>"}:{bindingClass ?? "descriptor"}:{bindingName ?? "<unnamed>"}@set{set}/binding{binding} x{count}";
                    lock (_vulkanDiagnosticLock)
                        _vulkanDescriptorFallbackSummary = AppendDiagnosticToken(_vulkanDescriptorFallbackSummary, summary);
                }

                public static void RecordVulkanDescriptorBindingFailure(
                    string? programName,
                    string? bindingClass,
                    string? bindingName,
                    uint set,
                    uint binding,
                    bool skippedDraw,
                    bool skippedDispatch,
                    string? message)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanDescriptorBindingFailures);
                    if (skippedDraw)
                        Interlocked.Increment(ref _vulkanDescriptorSkippedDraws);
                    if (skippedDispatch)
                        Interlocked.Increment(ref _vulkanDescriptorSkippedDispatches);

                    string summary =
                        $"{programName ?? "<program>"}:{bindingClass ?? "descriptor"}:{bindingName ?? "<unnamed>"}@set{set}/binding{binding}: {message ?? "binding failed"}";
                    lock (_vulkanDiagnosticLock)
                        _vulkanDescriptorFailureSummary = AppendDiagnosticToken(_vulkanDescriptorFailureSummary, summary);
                }

                public static void RecordVulkanDynamicUniformAllocation(long bytes)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanDynamicUniformAllocations);
                    if (bytes > 0)
                        Interlocked.Add(ref _vulkanDynamicUniformAllocatedBytes, bytes);
                }

                public static void RecordVulkanDynamicUniformExhaustion()
                {
                    if (EnableTracking)
                        Interlocked.Increment(ref _vulkanDynamicUniformExhaustions);
                }

                public static void RecordVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanRetiredResourcePlanReplacements);
                    AddNonNegative(ref _vulkanRetiredResourcePlanImages, imageCount);
                    AddNonNegative(ref _vulkanRetiredResourcePlanBuffers, bufferCount);
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

                public static void RecordVulkanPipelineCacheMiss(string? summary)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Increment(ref _vulkanPipelineCacheLookupMisses);

                    if (string.IsNullOrWhiteSpace(summary))
                        return;

                    lock (_vulkanDiagnosticLock)
                        _vulkanPipelineCacheMissSummary = AppendDiagnosticToken(_vulkanPipelineCacheMissSummary, summary);
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

            }
        }
    }
}
