using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class PrimaryCommandArtifactOwner(
    CommandBuffer primaryCommandBuffer,
    CommandBuffer dynamicUiSecondaryCommandBuffer,
    CommandPool primaryCommandPool,
    CommandPool dynamicUiSecondaryCommandPool,
    bool ownsPrimaryCommandBuffer,
    bool ownsDynamicUiSecondaryCommandBuffer)
{
            private int[] _recordedStaticSourceOrder = [];
            private int[] _recordedDynamicUiSourceOrder = [];

            public CommandBuffer PrimaryCommandBuffer { get; } = primaryCommandBuffer;
            public CommandBuffer DynamicUiSecondaryCommandBuffer { get; set; } = dynamicUiSecondaryCommandBuffer;
            public CommandPool PrimaryCommandPool { get; } = primaryCommandPool;
            public CommandPool DynamicUiSecondaryCommandPool { get; } = dynamicUiSecondaryCommandPool;
            public bool OwnsPrimaryCommandBuffer { get; } = ownsPrimaryCommandBuffer;
            public bool OwnsDynamicUiSecondaryCommandBuffer { get; set; } = ownsDynamicUiSecondaryCommandBuffer;
            public VulkanPrimaryCommandPlan PrimaryCommandPlan { get; } = new();
            public bool Dirty { get; set; } = true;
            public string? DirtyReason { get; set; } = "new variant";
            public ulong FrameOpsSignature { get; set; } = ulong.MaxValue;
            public ulong DynamicUiSignature { get; set; } = ulong.MaxValue;
            public int DynamicUiOpCount { get; set; } = -1;
            public bool DynamicUiSecondaryRecorded { get; set; }
            public bool DynamicUiSecondaryIncludesDepth { get; set; }
            public bool PreserveSwapchainForOverlay { get; set; }
            public ulong RecordedFrameOpContextFingerprint { get; set; } = ulong.MaxValue;
            public ulong RecordedFrameOpContextId { get; set; }
            public ulong RecordedResourceGeneration { get; set; }
            public ulong RecordedDescriptorGeneration { get; set; }
            public CommandBufferGenerationDomains RecordedGenerations { get; set; }
            public CommandRecordingDependencySignature RecordedDependencySignature { get; set; }
            public bool RecordedSwapchainImageEverPresented { get; set; }
            public ImageLayout RecordedSwapchainFinalLayout { get; set; } = ImageLayout.PresentSrcKhr;
            public int RecordedSwapchainWriteCount { get; set; }
            public bool RecordedSwapchainRefreshFromLastPresentSource { get; set; }
            public ulong RecordedImageLayoutStartSignature { get; set; } = ulong.MaxValue;
            public ulong RecordedImageLayoutEndSignature { get; set; } = ulong.MaxValue;
            public VulkanImageLayoutStateSnapshot? RecordedImageLayoutEndState { get; set; }
            public ulong CommandChainScheduleSignature { get; set; } = ulong.MaxValue;
            public CommandChainScheduleCacheIdentity RecordedCommandChainScheduleCacheIdentity { get; set; }
            public ulong CommandChainPrimaryGroupSignature { get; set; } = ulong.MaxValue;
            public VulkanCommandIdentityComponents CommandChainPrimaryIdentityComponents { get; set; }
            public VulkanPrimarySecondaryArtifactSequence RecordedSecondaryArtifactSequence { get; } = new();
            public long RecordedCommandChainArtifactMutationGeneration { get; set; } = -1;
            public bool AllPreparedDrawBindingsUseSecondaryBuffers { get; set; }
            public ulong CommandChainPrimarySkeletonSignature { get; set; } = ulong.MaxValue;
            public int CommandChainPrimaryGroupCount { get; set; } = -1;
            public ulong PlannerRevision { get; set; } = ulong.MaxValue;
            public bool GpuProfilerActive { get; set; }
            public int GpuProfilerFrameSlot { get; set; } = -1;
            public VulkanGpuProfilerPendingScope[]? GpuProfilerScopes { get; set; }
            public int GpuProfilerQueryCount { get; set; }
            public ulong LastUsedFrameId { get; set; }
            public FrameOpSignatureDebugPart[]? SignatureDebugParts { get; set; }
            public VulkanReusableFrameDataRefreshState
                PrimaryFrameDataRefreshState { get; } = new();
            public VulkanReusableFrameDataRefreshState
                DynamicUiFrameDataRefreshState { get; } = new();

            /// <summary>
            /// Captures the exact producer-to-sealed permutations owned by the
            /// primary artifact. Projection buffers are allocated only when a
            /// fresh recording changes the operation counts, never on clean
            /// steady-state reuse.
            /// </summary>
            public void CaptureRecordedOperationOrder(FramePlan framePlan)
            {
                CaptureRecordedOperationOrder(
                    framePlan.StaticOperations,
                    ref _recordedStaticSourceOrder);
                CaptureRecordedOperationOrder(
                    framePlan.DynamicOverlayOperations,
                    ref _recordedDynamicUiSourceOrder);
            }

            public void ClearRecordedOperationOrder()
            {
                _recordedStaticSourceOrder = [];
                _recordedDynamicUiSourceOrder = [];
            }

            public bool TryApplyRecordedOperationOrder(
                FrameOperationSequence staticOperations,
                FrameOperationSequence dynamicUiOperations)
            {
                if (staticOperations.Length != _recordedStaticSourceOrder.Length ||
                    dynamicUiOperations.Length != _recordedDynamicUiSourceOrder.Length)
                    return false;

                staticOperations.Stream.Reorder(_recordedStaticSourceOrder);
                dynamicUiOperations.Stream.Reorder(_recordedDynamicUiSourceOrder);
                return true;
            }

            private static void CaptureRecordedOperationOrder(
                FrameOperationStream stream,
                ref int[] sourceOrder)
            {
                if (sourceOrder.Length != stream.Count)
                    sourceOrder = new int[stream.Count];

                stream.CopySourceOrderTo(sourceOrder);
            }
}


