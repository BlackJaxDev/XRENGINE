using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChain(CommandChainKey key)
{
    private RenderPacket? _packetSnapshot;

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
    /// <summary>
    /// Exact prepared native pipeline/layout and descriptor publication state
    /// used by the executable secondary artifact.
    /// </summary>
    public VulkanPreparedCommandChainKey PreparedKey { get; set; }
    public VulkanPreparedCommandChainAuthority? PreparedAuthority { get; set; }
    public ulong RecordedUniformSlotSignature { get; set; }
    public VulkanIndirectSecondaryRecordingContract RecordedIndirectSecondaryContract { get; set; }
    public bool FrameDataRefreshTouchedDescriptors { get; set; }
    public int SourceStartIndex { get; set; } = -1;
    public int SourceCount { get; set; }
    public int LastRecordedFrameSlot { get; set; } = -1;
    public ulong LastUsedScheduleGeneration { get; set; }
    public bool ScheduledPacket { get; set; }
    public CommandChainDirtyReason DirtyReason { get; set; }
    public EVulkanCommandChainWorkerEligibility WorkerEligibility { get; set; }
    public VulkanPreparedComputePayload? PreparedComputePayload { get; set; }

    /// <summary>
    /// The sealed packet that authorized the current dependency publication.
    /// The chain retains a lease so pooled lowering storage cannot be rewritten
    /// while native recording or reuse validation still consumes the snapshot.
    /// </summary>
    public RenderPacket? PacketSnapshot => _packetSnapshot;

    public void PublishPacketSnapshot(RenderPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!packet.IsSealed)
            throw new InvalidOperationException("Only sealed render packets may be published to a command chain.");
        if (ReferenceEquals(_packetSnapshot, packet))
            return;

        packet.AcquireLease();
        _packetSnapshot?.ReleaseLease();
        _packetSnapshot = packet;
    }

    public void ReleasePacketSnapshot()
    {
        _packetSnapshot?.ReleaseLease();
        _packetSnapshot = null;
    }
}
