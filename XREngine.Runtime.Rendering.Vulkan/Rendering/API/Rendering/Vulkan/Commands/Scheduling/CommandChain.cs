using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChain(CommandChainKey key)
{
    public CommandChainKey Key { get; } = key;
    public CommandChainState State { get; set; }
    public CommandBuffer SecondaryCommandBuffer { get; set; }
    public CommandPool SecondaryCommandPool { get; set; }
    public bool OwnsSecondaryCommandPool { get; set; }
    public bool SecondaryCommandBufferExecutable { get; set; }
    public ulong SecondaryCommandBufferGeneration { get; set; }
    public bool HasSecondaryInheritance { get; set; }
    public bool SecondaryInheritanceDynamicRendering { get; set; }
    public RenderPass SecondaryInheritanceRenderPass { get; set; }
    public Framebuffer SecondaryInheritanceFramebuffer { get; set; }
    public DynamicRenderingFormatSignature SecondaryInheritanceDynamicRenderingFormats { get; set; }
    public bool SecondaryInheritanceDepthStencilReadOnly { get; set; }
    public SampleCountFlags SecondaryInheritanceSamples { get; set; }
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
}
