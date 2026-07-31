using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChain(CommandChainKey key)
{
    public CommandChainKey Key { get; } = key;
    public CommandChainState State { get; set; }
    public VulkanRecordedCommandArtifact RecordedArtifact { get; } =
        new(CommandBufferLevel.Secondary, key.FrameSlot);
    public CommandBuffer SecondaryCommandBuffer => RecordedArtifact.NativeBuffer;
    public CommandPool SecondaryCommandPool => RecordedArtifact.OwnerPool;
    public bool OwnsSecondaryCommandPool => RecordedArtifact.OwnsPool;
    public bool SecondaryCommandBufferExecutable => RecordedArtifact.IsExecutable;
    public ulong SecondaryCommandBufferGeneration => RecordedArtifact.Generation;
    public bool HasSecondaryInheritance => RecordedArtifact.HasInheritance;
    public bool SecondaryInheritanceDynamicRendering =>
        RecordedArtifact.Inheritance.DynamicRendering;
    public RenderPass SecondaryInheritanceRenderPass =>
        RecordedArtifact.Inheritance.RenderPass;
    public Framebuffer SecondaryInheritanceFramebuffer =>
        RecordedArtifact.Inheritance.Framebuffer;
    public DynamicRenderingFormatSignature SecondaryInheritanceDynamicRenderingFormats =>
        RecordedArtifact.Inheritance.DynamicRenderingFormats;
    public bool SecondaryInheritanceDepthStencilReadOnly =>
        RecordedArtifact.Inheritance.DepthStencilReadOnly;
    public SampleCountFlags SecondaryInheritanceSamples =>
        RecordedArtifact.Inheritance.Samples;
    public ulong StructuralSignature { get; set; }
    public ulong FrameDataSignature { get; set; }
    public ulong ResourcePlanRevision { get; set; }
    public ulong PhysicalImageSignature { get; set; }
    public ulong FramebufferSignature { get; set; }
    public ulong DescriptorGeneration { get; set; }
    public ulong PipelineGeneration { get; set; }
    public CommandRecordingDependencySignature DependencySignature { get; set; }
    public int DrawCount { get; set; }
    public int DispatchCount { get; set; }
    public ulong InstanceCountSignature { get; set; }
    public int DescriptorSetCount { get; set; }
    public ulong DescriptorSetSignature { get; set; }
    public ulong RecordedUniformSlotSignature { get; set; }
    public bool FrameDataRefreshTouchedDescriptors { get; set; }
    public int SourceStartIndex { get; set; } = -1;
    public int SourceCount { get; set; }
    public int LastRecordedFrameSlot { get; set; } = -1;
    public ulong LastUsedScheduleGeneration { get; set; }
    public bool ScheduledPacket { get; set; }
    public CommandChainDirtyReason DirtyReason { get; set; }
    public EVulkanCommandChainWorkerEligibility WorkerEligibility { get; set; }
}
