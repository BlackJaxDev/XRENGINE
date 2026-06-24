using System;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal enum RenderViewKind
{
    Main,
    VREye,
    Shadow,
    Reflection,
    Probe,
    Overlay,
}

internal enum RenderPacketVolatility
{
    StaticStructural,
    FrameDataOnly,
    DynamicCommand,
    StructuralDirty,
}

[Flags]
internal enum CommandChainDirtyReason
{
    None = 0,
    Structure = 1 << 0,
    ResourcePlan = 1 << 1,
    DescriptorGeneration = 1 << 2,
    PipelineGeneration = 1 << 3,
    ProfilerMode = 1 << 4,
    FrameDataRefreshFailed = 1 << 5,
    VolatileCommand = 1 << 6,
}

internal enum CommandChainState
{
    Unrecorded,
    Reused,
    FrameDataRefreshed,
    Recorded,
    NotReady,
}

internal enum CommandChainQueueKind
{
    Graphics,
    Compute,
    Transfer,
    SecondaryGraphics,
}

[Flags]
internal enum CommandChainQueueEligibility
{
    None = 0,
    Graphics = 1 << 0,
    Compute = 1 << 1,
    Transfer = 1 << 2,
    SecondaryGraphics = 1 << 3,
}

[Flags]
internal enum PrimaryCommandBufferDirtyReason
{
    None = 0,
    ScheduleStructure = 1 << 0,
    GroupStructure = 1 << 1,
    ResourcePlan = 1 << 2,
    ProfilerMode = 1 << 3,
}

internal readonly record struct RenderViewKey(
    int PipelineIdentity,
    int ViewportIdentity,
    int ViewIndex,
    RenderViewKind Kind,
    int LightIdentity,
    int CascadeIndex);

internal readonly record struct VisibilityPacket(
    RenderViewKey ViewKey,
    ulong SceneRevision,
    ulong CameraRevision,
    ReadOnlyMemory<int> RenderableIds,
    ulong StructuralSignature,
    ulong FrameDataSignature);

internal readonly record struct DrawPacket(
    int OpIndex,
    int RendererIdentity,
    int MeshIdentity,
    int MaterialIdentity,
    int ProgramIdentity,
    uint InstanceCount,
    bool Transparent,
    ulong StructuralSignature,
    ulong FrameDataSignature);

internal readonly record struct DispatchPacket(
    int OpIndex,
    int ProgramIdentity,
    uint GroupsX,
    uint GroupsY,
    uint GroupsZ,
    ulong StructuralSignature,
    ulong FrameDataSignature);

internal readonly record struct DescriptorBindingSnapshot(
    ulong DescriptorGeneration,
    int DescriptorSetCount,
    ulong DescriptorSetSignature);

internal readonly record struct ResourcePlanSnapshot(
    ulong Revision,
    ulong PhysicalImageSignature,
    ulong FramebufferSignature,
    ulong PipelineGeneration);

internal sealed class RenderPacket(
    RenderViewKey viewKey,
    int passIndex,
    int targetIdentity,
    string targetName,
    RenderPacketVolatility volatility,
    ReadOnlyMemory<DrawPacket> draws,
    ReadOnlyMemory<DispatchPacket> dispatches,
    DescriptorBindingSnapshot descriptorSnapshot,
    ResourcePlanSnapshot resourcePlanSnapshot,
    ulong structuralSignature,
    ulong frameDataSignature,
    int sourceStartIndex,
    int sourceCount,
    bool dynamicOverlay)
{
    public RenderViewKey ViewKey { get; } = viewKey;
    public int PassIndex { get; } = passIndex;
    public int TargetIdentity { get; } = targetIdentity;
    public string TargetName { get; } = targetName;
    public RenderPacketVolatility Volatility { get; } = volatility;
    public ReadOnlyMemory<DrawPacket> Draws { get; } = draws;
    public ReadOnlyMemory<DispatchPacket> Dispatches { get; } = dispatches;
    public DescriptorBindingSnapshot DescriptorSnapshot { get; } = descriptorSnapshot;
    public ResourcePlanSnapshot ResourcePlanSnapshot { get; } = resourcePlanSnapshot;
    public ulong StructuralSignature { get; } = structuralSignature;
    public ulong FrameDataSignature { get; } = frameDataSignature;
    public int SourceStartIndex { get; } = sourceStartIndex;
    public int SourceCount { get; } = sourceCount;
    public bool DynamicOverlay { get; } = dynamicOverlay;
}

internal readonly record struct CommandChainKey(
    int FrameSlot,
    RenderViewKey ViewKey,
    int PassIndex,
    int TargetIdentity,
    int ChainOrdinal);

internal sealed class CommandChain(CommandChainKey key)
{
    public CommandChainKey Key { get; } = key;
    public CommandChainState State { get; set; }
    public CommandBuffer SecondaryCommandBuffer { get; set; }
    public CommandPool SecondaryCommandPool { get; set; }
    public ulong StructuralSignature { get; set; }
    public ulong FrameDataSignature { get; set; }
    public ulong ResourcePlanRevision { get; set; }
    public ulong PhysicalImageSignature { get; set; }
    public ulong FramebufferSignature { get; set; }
    public ulong DescriptorGeneration { get; set; }
    public ulong PipelineGeneration { get; set; }
    public int DrawCount { get; set; }
    public int DispatchCount { get; set; }
    public ulong InstanceCountSignature { get; set; }
    public int DescriptorSetCount { get; set; }
    public ulong DescriptorSetSignature { get; set; }
    public int LastRecordedFrameSlot { get; set; } = -1;
    public CommandChainDirtyReason DirtyReason { get; set; }
}

internal sealed class RenderPassChainGroup(
    int passIndex,
    int targetIdentity,
    string targetName,
    ReadOnlyMemory<CommandChainKey> chainKeys,
    ulong structuralSignature,
    bool supportsSecondaryCommandBuffers,
    bool dynamicOverlay)
{
    public int PassIndex { get; } = passIndex;
    public int TargetIdentity { get; } = targetIdentity;
    public string TargetName { get; } = targetName;
    public ReadOnlyMemory<CommandChainKey> ChainKeys { get; } = chainKeys;
    public ulong StructuralSignature { get; } = structuralSignature;
    public bool SupportsSecondaryCommandBuffers { get; } = supportsSecondaryCommandBuffers;
    public bool DynamicOverlay { get; } = dynamicOverlay;
}

internal sealed class CommandChainSchedule(
    ulong structuralSignature,
    ulong resourcePlanRevision,
    ReadOnlyMemory<RenderPassChainGroup> groups)
{
    public ulong StructuralSignature { get; } = structuralSignature;
    public ulong ResourcePlanRevision { get; } = resourcePlanRevision;
    public ReadOnlyMemory<RenderPassChainGroup> Groups { get; } = groups;
}

internal readonly record struct CommandChainQueueDependency(
    int SourceNodeIndex,
    int DestinationNodeIndex,
    ulong TimelineSignalValue,
    bool RequiresQueueFamilyOwnershipTransfer);

internal sealed class CommandChainQueueNode(
    CommandChainQueueKind queueKind,
    CommandChainQueueEligibility eligibility,
    ReadOnlyMemory<int> groupIndices,
    ulong timelineWaitValue,
    ulong timelineSignalValue,
    string diagnosticLabel)
{
    public CommandChainQueueKind QueueKind { get; } = queueKind;
    public CommandChainQueueEligibility Eligibility { get; } = eligibility;
    public ReadOnlyMemory<int> GroupIndices { get; } = groupIndices;
    public ulong TimelineWaitValue { get; } = timelineWaitValue;
    public ulong TimelineSignalValue { get; } = timelineSignalValue;
    public string DiagnosticLabel { get; } = diagnosticLabel;
}

internal sealed class CommandChainQueueSchedule(
    bool multiQueueEnabled,
    bool singleQueueFallbackAvailable,
    ReadOnlyMemory<CommandChainQueueNode> nodes,
    ReadOnlyMemory<CommandChainQueueDependency> dependencies,
    string diagnostics)
{
    public bool MultiQueueEnabled { get; } = multiQueueEnabled;
    public bool SingleQueueFallbackAvailable { get; } = singleQueueFallbackAvailable;
    public ReadOnlyMemory<CommandChainQueueNode> Nodes { get; } = nodes;
    public ReadOnlyMemory<CommandChainQueueDependency> Dependencies { get; } = dependencies;
    public string Diagnostics { get; } = diagnostics;
}

internal sealed class CommandChainPacketOwner(ulong frameId) : IDisposable
{
    private int _retired;

    public ulong FrameId { get; } = frameId;
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public void ThrowIfRetired()
    {
        if (IsRetired)
            throw new ObjectDisposedException(nameof(CommandChainPacketOwner), $"Command-chain packet memory for frame {FrameId} was used after retirement.");
    }

    public void Dispose()
    {
        Volatile.Write(ref _retired, 1);
    }
}
