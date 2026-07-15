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
                public static class Vulkan
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
                    private static long _vulkanFrameSampleTimingQueriesTicks;
                    private static long _vulkanFrameDrainRetiredResourcesTicks;
                    private static long _vulkanFrameAcquireBridgeSubmitTicks;
                    private static long _vulkanFrameWaitSwapchainImageTicks;
                    private static long _vulkanFrameResetDynamicUniformRingTicks;
                    private static long _vulkanRecordCommandBufferAllocatedBytes;
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
                    private static long _lastFrameVulkanFrameSampleTimingQueriesTicks;
                    private static long _lastFrameVulkanFrameDrainRetiredResourcesTicks;
                    private static long _lastFrameVulkanFrameAcquireBridgeSubmitTicks;
                    private static long _lastFrameVulkanFrameWaitSwapchainImageTicks;
                    private static long _lastFrameVulkanFrameResetDynamicUniformRingTicks;
                    private static long _lastFrameVulkanRecordCommandBufferAllocatedBytes;
                    private static int _vulkanDeviceLocalAllocationCount;
                    private static long _vulkanDeviceLocalAllocatedBytes;
                    private static int _vulkanUploadAllocationCount;
                    private static long _vulkanUploadAllocatedBytes;
                    private static int _vulkanReadbackAllocationCount;
                    private static long _vulkanReadbackAllocatedBytes;
                    private static int _vulkanDescriptorPoolCreateCount;
                    private static int _vulkanDescriptorPoolDestroyCount;
                    private static int _vulkanDescriptorPoolResetCount;
                    private static int _vulkanLifetimeLiveResourceCount;
                    private static int _vulkanTrackedDescriptorSetCount;
                    private static int _vulkanLifetimePendingRetirementCount;
                    private static long _vulkanLifetimeOldestPendingRetirementAgeMilliseconds;
                    private static int _vulkanMeshFrameDataArenaChunkCount;
                    private static long _vulkanMeshFrameDataMappedBytes;
                    private static long _vulkanMeshFrameDataReservedBytes;
                    private static int _vulkanMeshFrameDataReservationCount;
                    private static long _vulkanMeshFrameDataGeneration;
                    private static int _vulkanMeshFrameDataRecordingLeases;
                    private static int _vulkanMeshFrameDataCachedLeases;
                    private static int _vulkanMeshFrameDataSubmittedLeases;
                    private static int _vulkanMeshFrameDataActiveGenerationCount;
                    private static int _vulkanMeshFrameDataLeaseRetainedGenerationCount;
                    private static long _vulkanMeshFrameDataManifestGeneration;
                    private static long _vulkanMeshFrameDataManifestPublicationCount;
                    private static long _vulkanMeshFrameDataManifestLateRegistrationCount;
                    private static int _vulkanMeshFrameDataManifestRendererCount;
                    private static int _vulkanMeshFrameDataManifestFamilyCount;
                    private static int _vulkanMeshFrameDataManifestIsSealed;
                    private static int _vulkanMeshDescriptorAllocationVariants;
                    private static int _vulkanMeshDescriptorPools;
                    private static int _vulkanMeshDescriptorAllocatedSets;
                    private static int _vulkanMeshDescriptorReservedSets;
                    private static int _vulkanMeshFrameDataArenaChunkHighWater;
                    private static long _vulkanMeshFrameDataMappedBytesHighWater;
                    private static long _vulkanMeshFrameDataReservedBytesHighWater;
                    private static int _vulkanMeshFrameDataReservationHighWater;
                    private static int _vulkanMeshFrameDataLeaseHighWater;
                    private static int _vulkanMeshDescriptorAllocationVariantHighWater;
                    private static int _vulkanMeshDescriptorPoolHighWater;
                    private static int _vulkanMeshDescriptorSetHighWater;
                    private static int _vulkanQueueSubmitCount;
                    private static int _vulkanPresentAttemptCount;
                    private static int _vulkanPresentAcceptedCount;
                    private static int _vulkanLastPresentResult;
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
                    private static int _lastFrameVulkanPresentAttemptCount;
                    private static int _lastFrameVulkanPresentAcceptedCount;
                    private static int _lastFrameVulkanLastPresentResult;
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
                    private static int _vulkanFrameOpTotalCount;
                    private static int _vulkanFrameOpClearCount;
                    private static int _vulkanFrameOpMeshDrawCount;
                    private static int _vulkanFrameOpIndirectDrawCount;
                    private static int _vulkanFrameOpMeshTaskDispatchCount;
                    private static int _vulkanFrameOpBlitCount;
                    private static int _vulkanFrameOpComputeCount;
                    private static int _vulkanFrameOpSwapchainWriteCount;
                    private static int _vulkanFrameOpFboWriteCount;
                    private static int _vulkanFrameOpUniquePassCount;
                    private static int _vulkanFrameOpUniqueContextCount;
                    private static int _vulkanFrameOpUniqueTargetCount;
                    private static int _lastFrameVulkanFrameOpTotalCount;
                    private static int _lastFrameVulkanFrameOpClearCount;
                    private static int _lastFrameVulkanFrameOpMeshDrawCount;
                    private static int _lastFrameVulkanFrameOpIndirectDrawCount;
                    private static int _lastFrameVulkanFrameOpMeshTaskDispatchCount;
                    private static int _lastFrameVulkanFrameOpBlitCount;
                    private static int _lastFrameVulkanFrameOpComputeCount;
                    private static int _lastFrameVulkanFrameOpSwapchainWriteCount;
                    private static int _lastFrameVulkanFrameOpFboWriteCount;
                    private static int _lastFrameVulkanFrameOpUniquePassCount;
                    private static int _lastFrameVulkanFrameOpUniqueContextCount;
                    private static int _lastFrameVulkanFrameOpUniqueTargetCount;
                    private static int _vulkanCommandBufferCleanReuseCount;
                    private static int _vulkanCommandBufferRecordCount;
                    private static int _vulkanCommandBufferForcedDirtyCount;
                    private static int _vulkanCommandBufferFrameOpSignatureDirtyCount;
                    private static int _vulkanCommandBufferPlannerDirtyCount;
                    private static int _vulkanCommandBufferProfilerDirtyCount;
                    private static int _vulkanExactVariantsDirtied;
                    private static int _vulkanExactCommandChainsDirtied;
                    private static int _vulkanUnrelatedVariantsPreserved;
                    private static int _vulkanGlobalFallbackInvalidations;
                    private static int _vulkanTrackingDependencyBinds;
                    private static int _vulkanTrackingUniqueDependencies;
                    private static int _vulkanTrackingImageAccessWrites;
                    private static int _vulkanTrackingCompactImageRanges;
                    private static int _vulkanDescriptorExpansionCacheHits;
                    private static int _vulkanDescriptorExpansionCacheMisses;
                    private static int _vulkanLifetimeLockContentions;
                    private static int _vulkanLayoutLockContentions;
                    private static int _lastFrameVulkanCommandBufferCleanReuseCount;
                    private static int _lastFrameVulkanCommandBufferRecordCount;
                    private static int _lastFrameVulkanCommandBufferForcedDirtyCount;
                    private static int _lastFrameVulkanCommandBufferFrameOpSignatureDirtyCount;
                    private static int _lastFrameVulkanCommandBufferPlannerDirtyCount;
                    private static int _lastFrameVulkanCommandBufferProfilerDirtyCount;
                    private static int _lastFrameVulkanExactVariantsDirtied;
                    private static int _lastFrameVulkanExactCommandChainsDirtied;
                    private static int _lastFrameVulkanUnrelatedVariantsPreserved;
                    private static int _lastFrameVulkanGlobalFallbackInvalidations;
                    private static int _lastFrameVulkanTrackingDependencyBinds;
                    private static int _lastFrameVulkanTrackingUniqueDependencies;
                    private static int _lastFrameVulkanTrackingImageAccessWrites;
                    private static int _lastFrameVulkanTrackingCompactImageRanges;
                    private static int _lastFrameVulkanDescriptorExpansionCacheHits;
                    private static int _lastFrameVulkanDescriptorExpansionCacheMisses;
                    private static int _lastFrameVulkanLifetimeLockContentions;
                    private static int _lastFrameVulkanLayoutLockContentions;
                    private static string _vulkanCommandBufferDirtySummary = string.Empty;
                    private static string _lastFrameVulkanCommandBufferDirtySummary = string.Empty;
                    private static int _vulkanCommandChainsScheduled;
                    private static int _vulkanCommandChainsRecorded;
                    private static int _vulkanCommandChainsReused;
                    private static int _vulkanCommandChainsFrameDataRefreshed;
                    private static int _vulkanVolatileCommandChainsRecorded;
                    private static int _vulkanPrimaryCommandBuffersReused;
                    private static int _vulkanPrimaryCommandBuffersRecorded;
                    private static int _vulkanVisibilityPacketCount;
                    private static int _vulkanRenderPacketCount;
                    private static int _vulkanSecondaryCommandBufferCount;
                    private static long _vulkanCommandChainWorkerRecordTicks;
                    private static long _vulkanRenderThreadWaitForChainWorkersTicks;
                    private static string _vulkanFirstCommandChainStructuralDirtyReason = string.Empty;
                    private static string _vulkanFirstCommandChainDescriptorGenerationMismatch = string.Empty;
                    private static string _vulkanFirstCommandChainResourcePlanRevisionMismatch = string.Empty;
                    private static int _lastFrameVulkanCommandChainsScheduled;
                    private static int _lastFrameVulkanCommandChainsRecorded;
                    private static int _lastFrameVulkanCommandChainsReused;
                    private static int _lastFrameVulkanCommandChainsFrameDataRefreshed;
                    private static int _lastFrameVulkanVolatileCommandChainsRecorded;
                    private static int _lastFrameVulkanPrimaryCommandBuffersReused;
                    private static int _lastFrameVulkanPrimaryCommandBuffersRecorded;
                    private static int _lastFrameVulkanVisibilityPacketCount;
                    private static int _lastFrameVulkanRenderPacketCount;
                    private static int _lastFrameVulkanSecondaryCommandBufferCount;
                    private static long _lastFrameVulkanCommandChainWorkerRecordTicks;
                    private static long _lastFrameVulkanRenderThreadWaitForChainWorkersTicks;
                    private static string _lastFrameVulkanFirstCommandChainStructuralDirtyReason = string.Empty;
                    private static string _lastFrameVulkanFirstCommandChainDescriptorGenerationMismatch = string.Empty;
                    private static string _lastFrameVulkanFirstCommandChainResourcePlanRevisionMismatch = string.Empty;
                    private static int _vulkanRetiredDescriptorPoolCount;
                    private static int _vulkanRetiredDescriptorSetCount;
                    private static int _vulkanRetiredCommandBufferCount;
                    private static int _vulkanRetiredQueryPoolCount;
                    private static int _vulkanRetiredBufferViewCount;
                    private static int _vulkanRetiredPipelineCount;
                    private static int _vulkanRetiredFramebufferCount;
                    private static int _vulkanRetiredBufferCount;
                    private static int _vulkanRetiredBufferMemoryCount;
                    private static int _vulkanRetiredImageCount;
                    private static int _vulkanRetiredImageViewCount;
                    private static int _vulkanRetiredSamplerCount;
                    private static int _vulkanRetiredImageMemoryCount;
                    private static long _vulkanRetiredImageBytes;
                    private static int _lastFrameVulkanRetiredDescriptorPoolCount;
                    private static int _lastFrameVulkanRetiredDescriptorSetCount;
                    private static int _lastFrameVulkanRetiredCommandBufferCount;
                    private static int _lastFrameVulkanRetiredQueryPoolCount;
                    private static int _lastFrameVulkanRetiredBufferViewCount;
                    private static int _lastFrameVulkanRetiredPipelineCount;
                    private static int _lastFrameVulkanRetiredFramebufferCount;
                    private static int _lastFrameVulkanRetiredBufferCount;
                    private static int _lastFrameVulkanRetiredBufferMemoryCount;
                    private static int _lastFrameVulkanRetiredImageCount;
                    private static int _lastFrameVulkanRetiredImageViewCount;
                    private static int _lastFrameVulkanRetiredSamplerCount;
                    private static int _lastFrameVulkanRetiredImageMemoryCount;
                    private static long _lastFrameVulkanRetiredImageBytes;
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
                    public static double VulkanFrameSampleTimingQueriesMs => TimeSpan.FromTicks(_lastFrameVulkanFrameSampleTimingQueriesTicks).TotalMilliseconds;
                    public static double VulkanFrameDrainRetiredResourcesMs => TimeSpan.FromTicks(_lastFrameVulkanFrameDrainRetiredResourcesTicks).TotalMilliseconds;
                    public static double VulkanFrameAcquireBridgeSubmitMs => TimeSpan.FromTicks(_lastFrameVulkanFrameAcquireBridgeSubmitTicks).TotalMilliseconds;
                    public static double VulkanFrameWaitSwapchainImageMs => TimeSpan.FromTicks(_lastFrameVulkanFrameWaitSwapchainImageTicks).TotalMilliseconds;
                    public static double VulkanFrameResetDynamicUniformRingMs => TimeSpan.FromTicks(_lastFrameVulkanFrameResetDynamicUniformRingTicks).TotalMilliseconds;
                    public static long VulkanRecordCommandBufferAllocatedBytes => _lastFrameVulkanRecordCommandBufferAllocatedBytes;
                    public static int VulkanDeviceLocalAllocationCount => _lastFrameVulkanDeviceLocalAllocationCount;
                    public static long VulkanDeviceLocalAllocatedBytes => _lastFrameVulkanDeviceLocalAllocatedBytes;
                    public static int VulkanUploadAllocationCount => _lastFrameVulkanUploadAllocationCount;
                    public static long VulkanUploadAllocatedBytes => _lastFrameVulkanUploadAllocatedBytes;
                    public static int VulkanReadbackAllocationCount => _lastFrameVulkanReadbackAllocationCount;
                    public static long VulkanReadbackAllocatedBytes => _lastFrameVulkanReadbackAllocatedBytes;
                    public static int VulkanDescriptorPoolCreateCount => _lastFrameVulkanDescriptorPoolCreateCount;
                    public static int VulkanDescriptorPoolDestroyCount => _lastFrameVulkanDescriptorPoolDestroyCount;
                    public static int VulkanDescriptorPoolResetCount => _lastFrameVulkanDescriptorPoolResetCount;
                    public static int VulkanLifetimeLiveResourceCount => Volatile.Read(ref _vulkanLifetimeLiveResourceCount);
                    public static int VulkanTrackedDescriptorSetCount => Volatile.Read(ref _vulkanTrackedDescriptorSetCount);
                    public static int VulkanLifetimePendingRetirementCount => Volatile.Read(ref _vulkanLifetimePendingRetirementCount);
                    public static long VulkanLifetimeOldestPendingRetirementAgeMilliseconds => Volatile.Read(ref _vulkanLifetimeOldestPendingRetirementAgeMilliseconds);
                    public static int VulkanMeshFrameDataArenaChunkCount => Volatile.Read(ref _vulkanMeshFrameDataArenaChunkCount);
                    public static long VulkanMeshFrameDataMappedBytes => Volatile.Read(ref _vulkanMeshFrameDataMappedBytes);
                    public static long VulkanMeshFrameDataReservedBytes => Volatile.Read(ref _vulkanMeshFrameDataReservedBytes);
                    public static int VulkanMeshFrameDataReservationCount => Volatile.Read(ref _vulkanMeshFrameDataReservationCount);
                    public static ulong VulkanMeshFrameDataGeneration => unchecked((ulong)Math.Max(Volatile.Read(ref _vulkanMeshFrameDataGeneration), 0L));
                    public static int VulkanMeshFrameDataRecordingLeases => Volatile.Read(ref _vulkanMeshFrameDataRecordingLeases);
                    public static int VulkanMeshFrameDataCachedLeases => Volatile.Read(ref _vulkanMeshFrameDataCachedLeases);
                    public static int VulkanMeshFrameDataSubmittedLeases => Volatile.Read(ref _vulkanMeshFrameDataSubmittedLeases);
                    public static int VulkanMeshFrameDataActiveGenerationCount => Volatile.Read(ref _vulkanMeshFrameDataActiveGenerationCount);
                    public static int VulkanMeshFrameDataLeaseRetainedGenerationCount => Volatile.Read(ref _vulkanMeshFrameDataLeaseRetainedGenerationCount);
                    public static ulong VulkanMeshFrameDataManifestGeneration => unchecked((ulong)Math.Max(Volatile.Read(ref _vulkanMeshFrameDataManifestGeneration), 0L));
                    public static long VulkanMeshFrameDataManifestPublicationCount => Volatile.Read(ref _vulkanMeshFrameDataManifestPublicationCount);
                    public static long VulkanMeshFrameDataManifestLateRegistrationCount => Volatile.Read(ref _vulkanMeshFrameDataManifestLateRegistrationCount);
                    public static int VulkanMeshFrameDataManifestRendererCount => Volatile.Read(ref _vulkanMeshFrameDataManifestRendererCount);
                    public static int VulkanMeshFrameDataManifestFamilyCount => Volatile.Read(ref _vulkanMeshFrameDataManifestFamilyCount);
                    public static bool VulkanMeshFrameDataManifestIsSealed => Volatile.Read(ref _vulkanMeshFrameDataManifestIsSealed) != 0;
                    public static int VulkanMeshDescriptorAllocationVariants => Volatile.Read(ref _vulkanMeshDescriptorAllocationVariants);
                    public static int VulkanMeshDescriptorPools => Volatile.Read(ref _vulkanMeshDescriptorPools);
                    public static int VulkanMeshDescriptorAllocatedSets => Volatile.Read(ref _vulkanMeshDescriptorAllocatedSets);
                    public static int VulkanMeshDescriptorReservedSets => Volatile.Read(ref _vulkanMeshDescriptorReservedSets);
                    public static int VulkanMeshFrameDataArenaChunkHighWater => Volatile.Read(ref _vulkanMeshFrameDataArenaChunkHighWater);
                    public static long VulkanMeshFrameDataMappedBytesHighWater => Volatile.Read(ref _vulkanMeshFrameDataMappedBytesHighWater);
                    public static long VulkanMeshFrameDataReservedBytesHighWater => Volatile.Read(ref _vulkanMeshFrameDataReservedBytesHighWater);
                    public static int VulkanMeshFrameDataReservationHighWater => Volatile.Read(ref _vulkanMeshFrameDataReservationHighWater);
                    public static int VulkanMeshFrameDataLeaseHighWater => Volatile.Read(ref _vulkanMeshFrameDataLeaseHighWater);
                    public static int VulkanMeshDescriptorAllocationVariantHighWater => Volatile.Read(ref _vulkanMeshDescriptorAllocationVariantHighWater);
                    public static int VulkanMeshDescriptorPoolHighWater => Volatile.Read(ref _vulkanMeshDescriptorPoolHighWater);
                    public static int VulkanMeshDescriptorSetHighWater => Volatile.Read(ref _vulkanMeshDescriptorSetHighWater);
                    public static int VulkanQueueSubmitCount => _lastFrameVulkanQueueSubmitCount;
                    public static int VulkanPresentAttemptCount => _lastFrameVulkanPresentAttemptCount;
                    public static int VulkanPresentAcceptedCount => _lastFrameVulkanPresentAcceptedCount;
                    public static int VulkanLastPresentResult => _lastFrameVulkanLastPresentResult;
                    public static bool VulkanValidationLayersEnabled
                        => RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled;
                    public static bool VulkanSynchronizationValidationEnabled
                        => RuntimeEngine.Rendering.State.VulkanSynchronizationValidationEnabled;
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
                    public static int VulkanFrameOpTotalCount => _lastFrameVulkanFrameOpTotalCount;
                    public static int VulkanFrameOpClearCount => _lastFrameVulkanFrameOpClearCount;
                    public static int VulkanFrameOpMeshDrawCount => _lastFrameVulkanFrameOpMeshDrawCount;
                    public static int VulkanFrameOpIndirectDrawCount => _lastFrameVulkanFrameOpIndirectDrawCount;
                    public static int VulkanFrameOpMeshTaskDispatchCount => _lastFrameVulkanFrameOpMeshTaskDispatchCount;
                    public static int VulkanFrameOpBlitCount => _lastFrameVulkanFrameOpBlitCount;
                    public static int VulkanFrameOpComputeCount => _lastFrameVulkanFrameOpComputeCount;
                    public static int VulkanFrameOpSwapchainWriteCount => _lastFrameVulkanFrameOpSwapchainWriteCount;
                    public static int VulkanFrameOpFboWriteCount => _lastFrameVulkanFrameOpFboWriteCount;
                    public static int VulkanFrameOpUniquePassCount => _lastFrameVulkanFrameOpUniquePassCount;
                    public static int VulkanFrameOpUniqueContextCount => _lastFrameVulkanFrameOpUniqueContextCount;
                    public static int VulkanFrameOpUniqueTargetCount => _lastFrameVulkanFrameOpUniqueTargetCount;
                    public static int VulkanCommandBufferCleanReuseCount => _lastFrameVulkanCommandBufferCleanReuseCount;
                    public static int VulkanCommandBufferRecordCount => _lastFrameVulkanCommandBufferRecordCount;
                    public static int VulkanCommandBufferForcedDirtyCount => _lastFrameVulkanCommandBufferForcedDirtyCount;
                    public static int VulkanCommandBufferFrameOpSignatureDirtyCount => _lastFrameVulkanCommandBufferFrameOpSignatureDirtyCount;
                    public static int VulkanCommandBufferPlannerDirtyCount => _lastFrameVulkanCommandBufferPlannerDirtyCount;
                    public static int VulkanCommandBufferProfilerDirtyCount => _lastFrameVulkanCommandBufferProfilerDirtyCount;
                    public static int VulkanExactVariantsDirtied => _lastFrameVulkanExactVariantsDirtied;
                    public static int VulkanExactCommandChainsDirtied => _lastFrameVulkanExactCommandChainsDirtied;
                    public static int VulkanUnrelatedVariantsPreserved => _lastFrameVulkanUnrelatedVariantsPreserved;
                    public static int VulkanGlobalFallbackInvalidations => _lastFrameVulkanGlobalFallbackInvalidations;
                    public static int VulkanTrackingDependencyBinds => _lastFrameVulkanTrackingDependencyBinds;
                    public static int VulkanTrackingUniqueDependencies => _lastFrameVulkanTrackingUniqueDependencies;
                    public static int VulkanTrackingImageAccessWrites => _lastFrameVulkanTrackingImageAccessWrites;
                    public static int VulkanTrackingCompactImageRanges => _lastFrameVulkanTrackingCompactImageRanges;
                    public static int VulkanDescriptorExpansionCacheHits => _lastFrameVulkanDescriptorExpansionCacheHits;
                    public static int VulkanDescriptorExpansionCacheMisses => _lastFrameVulkanDescriptorExpansionCacheMisses;
                    public static int VulkanLifetimeLockContentions => _lastFrameVulkanLifetimeLockContentions;
                    public static int VulkanLayoutLockContentions => _lastFrameVulkanLayoutLockContentions;
                    public static string VulkanCommandBufferDirtySummary => _lastFrameVulkanCommandBufferDirtySummary;
                    public static int VulkanCommandChainsScheduled => _lastFrameVulkanCommandChainsScheduled;
                    public static int VulkanCommandChainsRecorded => _lastFrameVulkanCommandChainsRecorded;
                    public static int VulkanCommandChainsReused => _lastFrameVulkanCommandChainsReused;
                    public static int VulkanCommandChainsFrameDataRefreshed => _lastFrameVulkanCommandChainsFrameDataRefreshed;
                    public static int VulkanVolatileCommandChainsRecorded => _lastFrameVulkanVolatileCommandChainsRecorded;
                    public static int VulkanPrimaryCommandBuffersReused => _lastFrameVulkanPrimaryCommandBuffersReused;
                    public static int VulkanPrimaryCommandBuffersRecorded => _lastFrameVulkanPrimaryCommandBuffersRecorded;
                    public static int VulkanVisibilityPacketCount => _lastFrameVulkanVisibilityPacketCount;
                    public static int VulkanRenderPacketCount => _lastFrameVulkanRenderPacketCount;
                    public static int VulkanSecondaryCommandBufferCount => _lastFrameVulkanSecondaryCommandBufferCount;
                    public static double VulkanCommandChainWorkerRecordMs => TimeSpan.FromTicks(_lastFrameVulkanCommandChainWorkerRecordTicks).TotalMilliseconds;
                    public static double VulkanRenderThreadWaitForChainWorkersMs => TimeSpan.FromTicks(_lastFrameVulkanRenderThreadWaitForChainWorkersTicks).TotalMilliseconds;
                    public static string VulkanFirstCommandChainStructuralDirtyReason => _lastFrameVulkanFirstCommandChainStructuralDirtyReason;
                    public static string VulkanFirstCommandChainDescriptorGenerationMismatch => _lastFrameVulkanFirstCommandChainDescriptorGenerationMismatch;
                    public static string VulkanFirstCommandChainResourcePlanRevisionMismatch => _lastFrameVulkanFirstCommandChainResourcePlanRevisionMismatch;
                    public static int VulkanRetiredDescriptorPoolCount => _lastFrameVulkanRetiredDescriptorPoolCount;
                    public static int VulkanRetiredDescriptorSetCount => _lastFrameVulkanRetiredDescriptorSetCount;
                    public static int VulkanRetiredCommandBufferCount => _lastFrameVulkanRetiredCommandBufferCount;
                    public static int VulkanRetiredQueryPoolCount => _lastFrameVulkanRetiredQueryPoolCount;
                    public static int VulkanRetiredBufferViewCount => _lastFrameVulkanRetiredBufferViewCount;
                    public static int VulkanRetiredPipelineCount => _lastFrameVulkanRetiredPipelineCount;
                    public static int VulkanRetiredFramebufferCount => _lastFrameVulkanRetiredFramebufferCount;
                    public static int VulkanRetiredBufferCount => _lastFrameVulkanRetiredBufferCount;
                    public static int VulkanRetiredBufferMemoryCount => _lastFrameVulkanRetiredBufferMemoryCount;
                    public static int VulkanRetiredImageCount => _lastFrameVulkanRetiredImageCount;
                    public static int VulkanRetiredImageViewCount => _lastFrameVulkanRetiredImageViewCount;
                    public static int VulkanRetiredSamplerCount => _lastFrameVulkanRetiredSamplerCount;
                    public static int VulkanRetiredImageMemoryCount => _lastFrameVulkanRetiredImageMemoryCount;
                    public static long VulkanRetiredImageBytes => _lastFrameVulkanRetiredImageBytes;
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

                    public static void RecordVulkanResourceLifetimeGauges(
                        int liveResourceCount,
                        int trackedDescriptorSetCount,
                        int pendingRetirementCount,
                        long oldestPendingRetirementAgeMilliseconds)
                    {
                        if (!EnableTracking)
                            return;

                        Volatile.Write(ref _vulkanLifetimeLiveResourceCount, Math.Max(liveResourceCount, 0));
                        Volatile.Write(ref _vulkanTrackedDescriptorSetCount, Math.Max(trackedDescriptorSetCount, 0));
                        Volatile.Write(ref _vulkanLifetimePendingRetirementCount, Math.Max(pendingRetirementCount, 0));
                        Volatile.Write(ref _vulkanLifetimeOldestPendingRetirementAgeMilliseconds, Math.Max(oldestPendingRetirementAgeMilliseconds, 0L));
                    }

                    public static void RecordVulkanMeshFrameDataGauges(
                        int arenaChunkCount,
                        long mappedBytes,
                        long reservedBytes,
                        int reservationCount,
                        ulong generation,
                        int recordingLeases = 0,
                        int cachedLeases = 0,
                        int submittedLeases = 0,
                        int activeGenerationCount = 0,
                        int leaseRetainedGenerationCount = 0)
                    {
                        if (!EnableTracking)
                            return;

                        Volatile.Write(ref _vulkanMeshFrameDataArenaChunkCount, Math.Max(arenaChunkCount, 0));
                        Volatile.Write(ref _vulkanMeshFrameDataMappedBytes, Math.Max(mappedBytes, 0L));
                        Volatile.Write(ref _vulkanMeshFrameDataReservedBytes, Math.Max(reservedBytes, 0L));
                        Volatile.Write(ref _vulkanMeshFrameDataReservationCount, Math.Max(reservationCount, 0));
						Volatile.Write(ref _vulkanMeshFrameDataGeneration, unchecked((long)Math.Min(generation, (ulong)long.MaxValue)));
                        Volatile.Write(ref _vulkanMeshFrameDataRecordingLeases, Math.Max(recordingLeases, 0));
                        Volatile.Write(ref _vulkanMeshFrameDataCachedLeases, Math.Max(cachedLeases, 0));
                        Volatile.Write(ref _vulkanMeshFrameDataSubmittedLeases, Math.Max(submittedLeases, 0));
                        Volatile.Write(ref _vulkanMeshFrameDataActiveGenerationCount, Math.Max(activeGenerationCount, 0));
                        Volatile.Write(ref _vulkanMeshFrameDataLeaseRetainedGenerationCount, Math.Max(leaseRetainedGenerationCount, 0));
                        UpdateHighWater(ref _vulkanMeshFrameDataArenaChunkHighWater, Math.Max(arenaChunkCount, 0));
                        UpdateHighWater(ref _vulkanMeshFrameDataMappedBytesHighWater, Math.Max(mappedBytes, 0L));
                        UpdateHighWater(ref _vulkanMeshFrameDataReservedBytesHighWater, Math.Max(reservedBytes, 0L));
                        UpdateHighWater(ref _vulkanMeshFrameDataReservationHighWater, Math.Max(reservationCount, 0));
                        UpdateHighWater(
                            ref _vulkanMeshFrameDataLeaseHighWater,
                            Math.Max(recordingLeases, 0) + Math.Max(cachedLeases, 0) + Math.Max(submittedLeases, 0));
                    }

                    public static void RecordVulkanFrameWideMeshFrameDataManifestGauges(
                        ulong generation,
                        long publicationCount,
                        long lateRegistrationCount,
                        int rendererCount,
                        int familyCount,
                        bool isSealed)
                    {
                        if (!EnableTracking)
                            return;

                        Volatile.Write(ref _vulkanMeshFrameDataManifestGeneration, unchecked((long)Math.Min(generation, (ulong)long.MaxValue)));
                        Volatile.Write(ref _vulkanMeshFrameDataManifestPublicationCount, Math.Max(publicationCount, 0L));
                        Volatile.Write(ref _vulkanMeshFrameDataManifestLateRegistrationCount, Math.Max(lateRegistrationCount, 0L));
                        Volatile.Write(ref _vulkanMeshFrameDataManifestRendererCount, Math.Max(rendererCount, 0));
                        Volatile.Write(ref _vulkanMeshFrameDataManifestFamilyCount, Math.Max(familyCount, 0));
                        Volatile.Write(ref _vulkanMeshFrameDataManifestIsSealed, isSealed ? 1 : 0);
                    }

                    public static void AdjustVulkanMeshDescriptorOwnership(
                        int allocationVariants,
                        int pools,
                        int allocatedSets,
                        int reservedSets)
                    {
                        if (!EnableTracking)
                            return;

                        int allocationVariantCount = allocationVariants == 0
                            ? Volatile.Read(ref _vulkanMeshDescriptorAllocationVariants)
                            : Interlocked.Add(ref _vulkanMeshDescriptorAllocationVariants, allocationVariants);
                        int poolCount = pools == 0
                            ? Volatile.Read(ref _vulkanMeshDescriptorPools)
                            : Interlocked.Add(ref _vulkanMeshDescriptorPools, pools);
                        int allocatedSetCount = allocatedSets == 0
                            ? Volatile.Read(ref _vulkanMeshDescriptorAllocatedSets)
                            : Interlocked.Add(ref _vulkanMeshDescriptorAllocatedSets, allocatedSets);
                        if (reservedSets != 0)
                            Interlocked.Add(ref _vulkanMeshDescriptorReservedSets, reservedSets);
                        UpdateHighWater(ref _vulkanMeshDescriptorAllocationVariantHighWater, Math.Max(allocationVariantCount, 0));
                        UpdateHighWater(ref _vulkanMeshDescriptorPoolHighWater, Math.Max(poolCount, 0));
                        UpdateHighWater(ref _vulkanMeshDescriptorSetHighWater, Math.Max(allocatedSetCount, 0));
                    }

                    private static void UpdateHighWater(ref int target, int value)
                    {
                        int observed = Volatile.Read(ref target);
                        while (value > observed)
                        {
                            int prior = Interlocked.CompareExchange(ref target, value, observed);
                            if (prior == observed)
                                return;
                            observed = prior;
                        }
                    }

                    private static void UpdateHighWater(ref long target, long value)
                    {
                        long observed = Volatile.Read(ref target);
                        while (value > observed)
                        {
                            long prior = Interlocked.CompareExchange(ref target, value, observed);
                            if (prior == observed)
                                return;
                            observed = prior;
                        }
                    }

                    public static void RecordVulkanQueueSubmit(int count = 1)
                    {
                        if (!EnableTracking || count <= 0)
                            return;

                        Interlocked.Add(ref _vulkanQueueSubmitCount, count);
                    }

                    public static void RecordVulkanPresentResult(int result, bool accepted)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _vulkanPresentAttemptCount);
                        if (accepted)
                            Interlocked.Increment(ref _vulkanPresentAcceptedCount);
                        Volatile.Write(ref _vulkanLastPresentResult, result);
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

                    public static void RecordVulkanFrameOpCensus(
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
                        int uniqueTargetCount)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _vulkanFrameOpTotalCount, totalCount);
                        AddNonNegative(ref _vulkanFrameOpClearCount, clearCount);
                        AddNonNegative(ref _vulkanFrameOpMeshDrawCount, meshDrawCount);
                        AddNonNegative(ref _vulkanFrameOpIndirectDrawCount, indirectDrawCount);
                        AddNonNegative(ref _vulkanFrameOpMeshTaskDispatchCount, meshTaskDispatchCount);
                        AddNonNegative(ref _vulkanFrameOpBlitCount, blitCount);
                        AddNonNegative(ref _vulkanFrameOpComputeCount, computeCount);
                        AddNonNegative(ref _vulkanFrameOpSwapchainWriteCount, swapchainWriteCount);
                        AddNonNegative(ref _vulkanFrameOpFboWriteCount, fboWriteCount);
                        AddNonNegative(ref _vulkanFrameOpUniquePassCount, uniquePassCount);
                        AddNonNegative(ref _vulkanFrameOpUniqueContextCount, uniqueContextCount);
                        AddNonNegative(ref _vulkanFrameOpUniqueTargetCount, uniqueTargetCount);
                    }

                    public static void RecordVulkanCommandBufferCacheOutcome(
                        bool reusedClean,
                        bool recorded,
                        bool forcedDirty,
                        bool frameOpSignatureDirty,
                        bool plannerDirty,
                        bool profilerDirty,
                        string? dirtyReason)
                    {
                        if (!EnableTracking)
                            return;

                        if (reusedClean)
                            Interlocked.Increment(ref _vulkanCommandBufferCleanReuseCount);
                        if (recorded)
                            Interlocked.Increment(ref _vulkanCommandBufferRecordCount);
                        if (forcedDirty)
                            Interlocked.Increment(ref _vulkanCommandBufferForcedDirtyCount);
                        if (frameOpSignatureDirty)
                            Interlocked.Increment(ref _vulkanCommandBufferFrameOpSignatureDirtyCount);
                        if (plannerDirty)
                            Interlocked.Increment(ref _vulkanCommandBufferPlannerDirtyCount);
                        if (profilerDirty)
                            Interlocked.Increment(ref _vulkanCommandBufferProfilerDirtyCount);

                        if (!string.IsNullOrWhiteSpace(dirtyReason))
                        {
                            lock (_vulkanDiagnosticLock)
                                _vulkanCommandBufferDirtySummary = AppendDiagnosticToken(_vulkanCommandBufferDirtySummary, dirtyReason);
                        }
                    }

                    public static void RecordVulkanCommandBuffersDirty(string? reason)
                    {
                        if (!EnableTracking || string.IsNullOrWhiteSpace(reason))
                            return;

                        lock (_vulkanDiagnosticLock)
                            _vulkanCommandBufferDirtySummary = AppendDiagnosticToken(_vulkanCommandBufferDirtySummary, $"dirty:{reason}");
                    }

                    public static void RecordVulkanExactResourceInvalidation(
                        int exactVariantsDirtied,
                        int exactCommandChainsDirtied,
                        int unrelatedVariantsPreserved,
                        int globalFallbackInvalidations)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _vulkanExactVariantsDirtied, exactVariantsDirtied);
                        AddNonNegative(ref _vulkanExactCommandChainsDirtied, exactCommandChainsDirtied);
                        AddNonNegative(ref _vulkanUnrelatedVariantsPreserved, unrelatedVariantsPreserved);
                        AddNonNegative(ref _vulkanGlobalFallbackInvalidations, globalFallbackInvalidations);
                    }

                    public static void RecordVulkanTrackingBatch(
                        int dependencyBinds,
                        int uniqueDependencies,
                        int imageAccessWrites,
                        int compactImageRanges)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _vulkanTrackingDependencyBinds, dependencyBinds);
                        AddNonNegative(ref _vulkanTrackingUniqueDependencies, uniqueDependencies);
                        AddNonNegative(ref _vulkanTrackingImageAccessWrites, imageAccessWrites);
                        AddNonNegative(ref _vulkanTrackingCompactImageRanges, compactImageRanges);
                    }

                    public static void RecordVulkanDescriptorExpansion(int cacheHits, int cacheMisses)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _vulkanDescriptorExpansionCacheHits, cacheHits);
                        AddNonNegative(ref _vulkanDescriptorExpansionCacheMisses, cacheMisses);
                    }

                    public static void RecordVulkanTrackingContention(int lifetimeLockContentions, int layoutLockContentions)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _vulkanLifetimeLockContentions, lifetimeLockContentions);
                        AddNonNegative(ref _vulkanLayoutLockContentions, layoutLockContentions);
                    }

                    public static void RecordVulkanCommandChainMetrics(
                        int chainsScheduled = 0,
                        int chainsRecorded = 0,
                        int chainsReused = 0,
                        int chainsFrameDataRefreshed = 0,
                        int volatileChainsRecorded = 0,
                        int primaryCommandBuffersReused = 0,
                        int primaryCommandBuffersRecorded = 0,
                        int visibilityPackets = 0,
                        int renderPackets = 0,
                        int secondaryCommandBuffers = 0,
                        TimeSpan chainWorkerRecordTime = default,
                        TimeSpan renderThreadWaitForWorkersTime = default,
                        string? firstStructuralDirtyReason = null,
                        string? firstDescriptorGenerationMismatch = null,
                        string? firstResourcePlanRevisionMismatch = null)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _vulkanCommandChainsScheduled, chainsScheduled);
                        AddNonNegative(ref _vulkanCommandChainsRecorded, chainsRecorded);
                        AddNonNegative(ref _vulkanCommandChainsReused, chainsReused);
                        AddNonNegative(ref _vulkanCommandChainsFrameDataRefreshed, chainsFrameDataRefreshed);
                        AddNonNegative(ref _vulkanVolatileCommandChainsRecorded, volatileChainsRecorded);
                        AddNonNegative(ref _vulkanPrimaryCommandBuffersReused, primaryCommandBuffersReused);
                        AddNonNegative(ref _vulkanPrimaryCommandBuffersRecorded, primaryCommandBuffersRecorded);
                        AddNonNegative(ref _vulkanVisibilityPacketCount, visibilityPackets);
                        AddNonNegative(ref _vulkanRenderPacketCount, renderPackets);
                        AddNonNegative(ref _vulkanSecondaryCommandBufferCount, secondaryCommandBuffers);

                        if (chainWorkerRecordTime.Ticks > 0)
                            Interlocked.Add(ref _vulkanCommandChainWorkerRecordTicks, chainWorkerRecordTime.Ticks);
                        if (renderThreadWaitForWorkersTime.Ticks > 0)
                            Interlocked.Add(ref _vulkanRenderThreadWaitForChainWorkersTicks, renderThreadWaitForWorkersTime.Ticks);

                        if (string.IsNullOrWhiteSpace(firstStructuralDirtyReason) &&
                            string.IsNullOrWhiteSpace(firstDescriptorGenerationMismatch) &&
                            string.IsNullOrWhiteSpace(firstResourcePlanRevisionMismatch))
                        {
                            return;
                        }

                        lock (_vulkanDiagnosticLock)
                        {
                            if (string.IsNullOrEmpty(_vulkanFirstCommandChainStructuralDirtyReason) &&
                                !string.IsNullOrWhiteSpace(firstStructuralDirtyReason))
                            {
                                _vulkanFirstCommandChainStructuralDirtyReason = firstStructuralDirtyReason;
                            }

                            if (string.IsNullOrEmpty(_vulkanFirstCommandChainDescriptorGenerationMismatch) &&
                                !string.IsNullOrWhiteSpace(firstDescriptorGenerationMismatch))
                            {
                                _vulkanFirstCommandChainDescriptorGenerationMismatch = firstDescriptorGenerationMismatch;
                            }

                            if (string.IsNullOrEmpty(_vulkanFirstCommandChainResourcePlanRevisionMismatch) &&
                                !string.IsNullOrWhiteSpace(firstResourcePlanRevisionMismatch))
                            {
                                _vulkanFirstCommandChainResourcePlanRevisionMismatch = firstResourcePlanRevisionMismatch;
                            }
                        }
                    }

                    public static void RecordVulkanRetiredResourceDrain(
                        int descriptorPools = 0,
                        int descriptorSets = 0,
                        int commandBuffers = 0,
                        int queryPools = 0,
                        int bufferViews = 0,
                        int pipelines = 0,
                        int framebuffers = 0,
                        int buffers = 0,
                        int bufferMemories = 0,
                        int images = 0,
                        int imageViews = 0,
                        int samplers = 0,
                        int imageMemories = 0,
                        long imageBytes = 0)
                    {
                        if (!EnableTracking)
                            return;

                        AddNonNegative(ref _vulkanRetiredDescriptorPoolCount, descriptorPools);
                        AddNonNegative(ref _vulkanRetiredDescriptorSetCount, descriptorSets);
                        AddNonNegative(ref _vulkanRetiredCommandBufferCount, commandBuffers);
                        AddNonNegative(ref _vulkanRetiredQueryPoolCount, queryPools);
                        AddNonNegative(ref _vulkanRetiredBufferViewCount, bufferViews);
                        AddNonNegative(ref _vulkanRetiredPipelineCount, pipelines);
                        AddNonNegative(ref _vulkanRetiredFramebufferCount, framebuffers);
                        AddNonNegative(ref _vulkanRetiredBufferCount, buffers);
                        AddNonNegative(ref _vulkanRetiredBufferMemoryCount, bufferMemories);
                        AddNonNegative(ref _vulkanRetiredImageCount, images);
                        AddNonNegative(ref _vulkanRetiredImageViewCount, imageViews);
                        AddNonNegative(ref _vulkanRetiredSamplerCount, samplers);
                        AddNonNegative(ref _vulkanRetiredImageMemoryCount, imageMemories);
                        if (imageBytes > 0)
                            Interlocked.Add(ref _vulkanRetiredImageBytes, imageBytes);
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

                    public static void RecordVulkanFrameLifecycleDetailTiming(
                        TimeSpan sampleTimingQueries,
                        TimeSpan drainRetiredResources,
                        TimeSpan acquireBridgeSubmit,
                        TimeSpan waitSwapchainImage,
                        TimeSpan resetDynamicUniformRing)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Exchange(ref _vulkanFrameSampleTimingQueriesTicks, sampleTimingQueries.Ticks);
                        Interlocked.Exchange(ref _vulkanFrameDrainRetiredResourcesTicks, drainRetiredResources.Ticks);
                        Interlocked.Exchange(ref _vulkanFrameAcquireBridgeSubmitTicks, acquireBridgeSubmit.Ticks);
                        Interlocked.Exchange(ref _vulkanFrameWaitSwapchainImageTicks, waitSwapchainImage.Ticks);
                        Interlocked.Exchange(ref _vulkanFrameResetDynamicUniformRingTicks, resetDynamicUniformRing.Ticks);
                    }

                    public static void RecordVulkanRecordCommandBufferAllocation(long bytes)
                    {
                        if (!EnableTracking || bytes <= 0)
                            return;

                        Interlocked.Add(ref _vulkanRecordCommandBufferAllocatedBytes, bytes);
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

                    internal static void SnapshotAndReset()
                    {
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
                        _lastFrameVulkanFrameSampleTimingQueriesTicks = _vulkanFrameSampleTimingQueriesTicks;
                        _lastFrameVulkanFrameDrainRetiredResourcesTicks = _vulkanFrameDrainRetiredResourcesTicks;
                        _lastFrameVulkanFrameAcquireBridgeSubmitTicks = _vulkanFrameAcquireBridgeSubmitTicks;
                        _lastFrameVulkanFrameWaitSwapchainImageTicks = _vulkanFrameWaitSwapchainImageTicks;
                        _lastFrameVulkanFrameResetDynamicUniformRingTicks = _vulkanFrameResetDynamicUniformRingTicks;
                        _lastFrameVulkanRecordCommandBufferAllocatedBytes = _vulkanRecordCommandBufferAllocatedBytes;
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
                        _lastFrameVulkanPresentAttemptCount = _vulkanPresentAttemptCount;
                        _lastFrameVulkanPresentAcceptedCount = _vulkanPresentAcceptedCount;
                        _lastFrameVulkanLastPresentResult = _vulkanLastPresentResult;
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
                        _lastFrameVulkanFrameOpTotalCount = _vulkanFrameOpTotalCount;
                        _lastFrameVulkanFrameOpClearCount = _vulkanFrameOpClearCount;
                        _lastFrameVulkanFrameOpMeshDrawCount = _vulkanFrameOpMeshDrawCount;
                        _lastFrameVulkanFrameOpIndirectDrawCount = _vulkanFrameOpIndirectDrawCount;
                        _lastFrameVulkanFrameOpMeshTaskDispatchCount = _vulkanFrameOpMeshTaskDispatchCount;
                        _lastFrameVulkanFrameOpBlitCount = _vulkanFrameOpBlitCount;
                        _lastFrameVulkanFrameOpComputeCount = _vulkanFrameOpComputeCount;
                        _lastFrameVulkanFrameOpSwapchainWriteCount = _vulkanFrameOpSwapchainWriteCount;
                        _lastFrameVulkanFrameOpFboWriteCount = _vulkanFrameOpFboWriteCount;
                        _lastFrameVulkanFrameOpUniquePassCount = _vulkanFrameOpUniquePassCount;
                        _lastFrameVulkanFrameOpUniqueContextCount = _vulkanFrameOpUniqueContextCount;
                        _lastFrameVulkanFrameOpUniqueTargetCount = _vulkanFrameOpUniqueTargetCount;
                        _lastFrameVulkanCommandBufferCleanReuseCount = _vulkanCommandBufferCleanReuseCount;
                        _lastFrameVulkanCommandBufferRecordCount = _vulkanCommandBufferRecordCount;
                        _lastFrameVulkanCommandBufferForcedDirtyCount = _vulkanCommandBufferForcedDirtyCount;
                        _lastFrameVulkanCommandBufferFrameOpSignatureDirtyCount = _vulkanCommandBufferFrameOpSignatureDirtyCount;
                        _lastFrameVulkanCommandBufferPlannerDirtyCount = _vulkanCommandBufferPlannerDirtyCount;
                        _lastFrameVulkanCommandBufferProfilerDirtyCount = _vulkanCommandBufferProfilerDirtyCount;
                        _lastFrameVulkanExactVariantsDirtied = _vulkanExactVariantsDirtied;
                        _lastFrameVulkanExactCommandChainsDirtied = _vulkanExactCommandChainsDirtied;
                        _lastFrameVulkanUnrelatedVariantsPreserved = _vulkanUnrelatedVariantsPreserved;
                        _lastFrameVulkanGlobalFallbackInvalidations = _vulkanGlobalFallbackInvalidations;
                        _lastFrameVulkanTrackingDependencyBinds = _vulkanTrackingDependencyBinds;
                        _lastFrameVulkanTrackingUniqueDependencies = _vulkanTrackingUniqueDependencies;
                        _lastFrameVulkanTrackingImageAccessWrites = _vulkanTrackingImageAccessWrites;
                        _lastFrameVulkanTrackingCompactImageRanges = _vulkanTrackingCompactImageRanges;
                        _lastFrameVulkanDescriptorExpansionCacheHits = _vulkanDescriptorExpansionCacheHits;
                        _lastFrameVulkanDescriptorExpansionCacheMisses = _vulkanDescriptorExpansionCacheMisses;
                        _lastFrameVulkanLifetimeLockContentions = _vulkanLifetimeLockContentions;
                        _lastFrameVulkanLayoutLockContentions = _vulkanLayoutLockContentions;
                        _lastFrameVulkanCommandChainsScheduled = _vulkanCommandChainsScheduled;
                        _lastFrameVulkanCommandChainsRecorded = _vulkanCommandChainsRecorded;
                        _lastFrameVulkanCommandChainsReused = _vulkanCommandChainsReused;
                        _lastFrameVulkanCommandChainsFrameDataRefreshed = _vulkanCommandChainsFrameDataRefreshed;
                        _lastFrameVulkanVolatileCommandChainsRecorded = _vulkanVolatileCommandChainsRecorded;
                        _lastFrameVulkanPrimaryCommandBuffersReused = _vulkanPrimaryCommandBuffersReused;
                        _lastFrameVulkanPrimaryCommandBuffersRecorded = _vulkanPrimaryCommandBuffersRecorded;
                        _lastFrameVulkanVisibilityPacketCount = _vulkanVisibilityPacketCount;
                        _lastFrameVulkanRenderPacketCount = _vulkanRenderPacketCount;
                        _lastFrameVulkanSecondaryCommandBufferCount = _vulkanSecondaryCommandBufferCount;
                        _lastFrameVulkanCommandChainWorkerRecordTicks = _vulkanCommandChainWorkerRecordTicks;
                        _lastFrameVulkanRenderThreadWaitForChainWorkersTicks = _vulkanRenderThreadWaitForChainWorkersTicks;
                        _lastFrameVulkanRetiredDescriptorPoolCount = _vulkanRetiredDescriptorPoolCount;
                        _lastFrameVulkanRetiredDescriptorSetCount = _vulkanRetiredDescriptorSetCount;
                        _lastFrameVulkanRetiredCommandBufferCount = _vulkanRetiredCommandBufferCount;
                        _lastFrameVulkanRetiredQueryPoolCount = _vulkanRetiredQueryPoolCount;
                        _lastFrameVulkanRetiredBufferViewCount = _vulkanRetiredBufferViewCount;
                        _lastFrameVulkanRetiredPipelineCount = _vulkanRetiredPipelineCount;
                        _lastFrameVulkanRetiredFramebufferCount = _vulkanRetiredFramebufferCount;
                        _lastFrameVulkanRetiredBufferCount = _vulkanRetiredBufferCount;
                        _lastFrameVulkanRetiredBufferMemoryCount = _vulkanRetiredBufferMemoryCount;
                        _lastFrameVulkanRetiredImageCount = _vulkanRetiredImageCount;
                        _lastFrameVulkanRetiredImageViewCount = _vulkanRetiredImageViewCount;
                        _lastFrameVulkanRetiredSamplerCount = _vulkanRetiredSamplerCount;
                        _lastFrameVulkanRetiredImageMemoryCount = _vulkanRetiredImageMemoryCount;
                        _lastFrameVulkanRetiredImageBytes = _vulkanRetiredImageBytes;

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
                            _lastFrameVulkanCommandBufferDirtySummary = _vulkanCommandBufferDirtySummary;
                            _lastFrameVulkanFirstCommandChainStructuralDirtyReason = _vulkanFirstCommandChainStructuralDirtyReason;
                            _lastFrameVulkanFirstCommandChainDescriptorGenerationMismatch = _vulkanFirstCommandChainDescriptorGenerationMismatch;
                            _lastFrameVulkanFirstCommandChainResourcePlanRevisionMismatch = _vulkanFirstCommandChainResourcePlanRevisionMismatch;
                        }

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
                        _vulkanFrameSampleTimingQueriesTicks = 0;
                        _vulkanFrameDrainRetiredResourcesTicks = 0;
                        _vulkanFrameAcquireBridgeSubmitTicks = 0;
                        _vulkanFrameWaitSwapchainImageTicks = 0;
                        _vulkanFrameResetDynamicUniformRingTicks = 0;
                        _vulkanRecordCommandBufferAllocatedBytes = 0;
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
                        _vulkanPresentAttemptCount = 0;
                        _vulkanPresentAcceptedCount = 0;
                        _vulkanLastPresentResult = 0;
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
                        _vulkanFrameOpTotalCount = 0;
                        _vulkanFrameOpClearCount = 0;
                        _vulkanFrameOpMeshDrawCount = 0;
                        _vulkanFrameOpIndirectDrawCount = 0;
                        _vulkanFrameOpMeshTaskDispatchCount = 0;
                        _vulkanFrameOpBlitCount = 0;
                        _vulkanFrameOpComputeCount = 0;
                        _vulkanFrameOpSwapchainWriteCount = 0;
                        _vulkanFrameOpFboWriteCount = 0;
                        _vulkanFrameOpUniquePassCount = 0;
                        _vulkanFrameOpUniqueContextCount = 0;
                        _vulkanFrameOpUniqueTargetCount = 0;
                        _vulkanCommandBufferCleanReuseCount = 0;
                        _vulkanCommandBufferRecordCount = 0;
                        _vulkanCommandBufferForcedDirtyCount = 0;
                        _vulkanCommandBufferFrameOpSignatureDirtyCount = 0;
                        _vulkanCommandBufferPlannerDirtyCount = 0;
                        _vulkanCommandBufferProfilerDirtyCount = 0;
                        _vulkanExactVariantsDirtied = 0;
                        _vulkanExactCommandChainsDirtied = 0;
                        _vulkanUnrelatedVariantsPreserved = 0;
                        _vulkanGlobalFallbackInvalidations = 0;
                        _vulkanTrackingDependencyBinds = 0;
                        _vulkanTrackingUniqueDependencies = 0;
                        _vulkanTrackingImageAccessWrites = 0;
                        _vulkanTrackingCompactImageRanges = 0;
                        _vulkanDescriptorExpansionCacheHits = 0;
                        _vulkanDescriptorExpansionCacheMisses = 0;
                        _vulkanLifetimeLockContentions = 0;
                        _vulkanLayoutLockContentions = 0;
                        _vulkanCommandChainsScheduled = 0;
                        _vulkanCommandChainsRecorded = 0;
                        _vulkanCommandChainsReused = 0;
                        _vulkanCommandChainsFrameDataRefreshed = 0;
                        _vulkanVolatileCommandChainsRecorded = 0;
                        _vulkanPrimaryCommandBuffersReused = 0;
                        _vulkanPrimaryCommandBuffersRecorded = 0;
                        _vulkanVisibilityPacketCount = 0;
                        _vulkanRenderPacketCount = 0;
                        _vulkanSecondaryCommandBufferCount = 0;
                        _vulkanCommandChainWorkerRecordTicks = 0;
                        _vulkanRenderThreadWaitForChainWorkersTicks = 0;
                        _vulkanRetiredDescriptorPoolCount = 0;
                        _vulkanRetiredDescriptorSetCount = 0;
                        _vulkanRetiredCommandBufferCount = 0;
                        _vulkanRetiredQueryPoolCount = 0;
                        _vulkanRetiredBufferViewCount = 0;
                        _vulkanRetiredPipelineCount = 0;
                        _vulkanRetiredFramebufferCount = 0;
                        _vulkanRetiredBufferCount = 0;
                        _vulkanRetiredBufferMemoryCount = 0;
                        _vulkanRetiredImageCount = 0;
                        _vulkanRetiredImageViewCount = 0;
                        _vulkanRetiredSamplerCount = 0;
                        _vulkanRetiredImageMemoryCount = 0;
                        _vulkanRetiredImageBytes = 0;

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
                            _vulkanCommandBufferDirtySummary = string.Empty;
                            _vulkanFirstCommandChainStructuralDirtyReason = string.Empty;
                            _vulkanFirstCommandChainDescriptorGenerationMismatch = string.Empty;
                            _vulkanFirstCommandChainResourcePlanRevisionMismatch = string.Empty;
                        }
                    }

                }
            }
        }
    }
}
