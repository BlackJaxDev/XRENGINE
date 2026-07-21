using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private sealed class CommandBufferCacheVariant(
            CommandBuffer primaryCommandBuffer,
            CommandBuffer dynamicUiSecondaryCommandBuffer,
            CommandPool primaryCommandPool,
            CommandPool dynamicUiSecondaryCommandPool,
            bool ownsPrimaryCommandBuffer,
            bool ownsDynamicUiSecondaryCommandBuffer)
        {
            public CommandBuffer PrimaryCommandBuffer { get; } = primaryCommandBuffer;
            public CommandBuffer DynamicUiSecondaryCommandBuffer { get; } = dynamicUiSecondaryCommandBuffer;
            public CommandPool PrimaryCommandPool { get; } = primaryCommandPool;
            public CommandPool DynamicUiSecondaryCommandPool { get; } = dynamicUiSecondaryCommandPool;
            public bool OwnsPrimaryCommandBuffer { get; } = ownsPrimaryCommandBuffer;
            public bool OwnsDynamicUiSecondaryCommandBuffer { get; } = ownsDynamicUiSecondaryCommandBuffer;
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
            public ulong CommandChainPrimaryGroupSignature { get; set; } = ulong.MaxValue;
            public int CommandChainPrimaryGroupCount { get; set; } = -1;
            public ulong PlannerRevision { get; set; } = ulong.MaxValue;
            public bool GpuProfilerActive { get; set; }
            public int GpuProfilerFrameSlot { get; set; } = -1;
            public VulkanGpuProfilerPendingScope[]? GpuProfilerScopes { get; set; }
            public int GpuProfilerQueryCount { get; set; }
            public ulong LastUsedFrameId { get; set; }
            public FrameOpSignatureDebugPart[]? SignatureDebugParts { get; set; }
        }

    }
}
