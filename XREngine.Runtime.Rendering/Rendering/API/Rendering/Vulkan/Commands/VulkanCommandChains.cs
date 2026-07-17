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
    SecondaryCommandBufferInvalid = 1 << 7,
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

internal sealed class RenderPacket
{
    private DrawPacket[]? _draws;
    private DispatchPacket[]? _dispatches;

    public RenderPacket()
    {
    }

    public RenderPacket(
        RenderViewKey viewKey,
        int passIndex,
        int targetIdentity,
        string targetName,
        RenderPacketVolatility volatility,
        DrawPacket firstDraw,
        int drawCount,
        DispatchPacket firstDispatch,
        int dispatchCount,
        DescriptorBindingSnapshot descriptorSnapshot,
        ResourcePlanSnapshot resourcePlanSnapshot,
        ulong structuralSignature,
        ulong frameDataSignature,
        int sourceStartIndex,
        int sourceCount,
        bool dynamicOverlay)
        => Reset(
            viewKey,
            passIndex,
            targetIdentity,
            targetName,
            volatility,
            firstDraw,
            drawCount,
            firstDispatch,
            dispatchCount,
            descriptorSnapshot,
            resourcePlanSnapshot,
            structuralSignature,
            frameDataSignature,
            sourceStartIndex,
            sourceCount,
            dynamicOverlay);

    public RenderPacket(
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
        => Reset(
            viewKey,
            passIndex,
            targetIdentity,
            targetName,
            volatility,
            draws.Span,
            dispatches.Span,
            descriptorSnapshot,
            resourcePlanSnapshot,
            structuralSignature,
            frameDataSignature,
            sourceStartIndex,
            sourceCount,
            dynamicOverlay);

    public RenderViewKey ViewKey { get; private set; }
    public int PassIndex { get; private set; }
    public int TargetIdentity { get; private set; }
    public string TargetName { get; private set; } = string.Empty;
    public RenderPacketVolatility Volatility { get; private set; }
    public DrawPacket FirstDraw { get; private set; }
    public int DrawCount { get; private set; }
    public DispatchPacket FirstDispatch { get; private set; }
    public int DispatchCount { get; private set; }
    public DescriptorBindingSnapshot DescriptorSnapshot { get; private set; }
    public ResourcePlanSnapshot ResourcePlanSnapshot { get; private set; }
    public ulong StructuralSignature { get; private set; }
    public ulong FrameDataSignature { get; private set; }
    public int SourceStartIndex { get; private set; }
    public int SourceCount { get; private set; }
    public bool DynamicOverlay { get; private set; }

    public void Reset(
        RenderViewKey viewKey,
        int passIndex,
        int targetIdentity,
        string targetName,
        RenderPacketVolatility volatility,
        DrawPacket firstDraw,
        int drawCount,
        DispatchPacket firstDispatch,
        int dispatchCount,
        DescriptorBindingSnapshot descriptorSnapshot,
        ResourcePlanSnapshot resourcePlanSnapshot,
        ulong structuralSignature,
        ulong frameDataSignature,
        int sourceStartIndex,
        int sourceCount,
        bool dynamicOverlay)
    {
        ViewKey = viewKey;
        PassIndex = passIndex;
        TargetIdentity = targetIdentity;
        TargetName = targetName;
        Volatility = volatility;
        FirstDraw = firstDraw;
        DrawCount = drawCount;
        FirstDispatch = firstDispatch;
        DispatchCount = dispatchCount;
        DescriptorSnapshot = descriptorSnapshot;
        ResourcePlanSnapshot = resourcePlanSnapshot;
        StructuralSignature = structuralSignature;
        FrameDataSignature = frameDataSignature;
        SourceStartIndex = sourceStartIndex;
        SourceCount = sourceCount;
        DynamicOverlay = dynamicOverlay;
    }

    public void Reset(
        RenderViewKey viewKey,
        int passIndex,
        int targetIdentity,
        string targetName,
        RenderPacketVolatility volatility,
        ReadOnlySpan<DrawPacket> draws,
        ReadOnlySpan<DispatchPacket> dispatches,
        DescriptorBindingSnapshot descriptorSnapshot,
        ResourcePlanSnapshot resourcePlanSnapshot,
        ulong structuralSignature,
        ulong frameDataSignature,
        int sourceStartIndex,
        int sourceCount,
        bool dynamicOverlay)
    {
        Reset(
            viewKey,
            passIndex,
            targetIdentity,
            targetName,
            volatility,
            draws.Length > 0 ? draws[0] : default,
            draws.Length,
            dispatches.Length > 0 ? dispatches[0] : default,
            dispatches.Length,
            descriptorSnapshot,
            resourcePlanSnapshot,
            structuralSignature,
            frameDataSignature,
            sourceStartIndex,
            sourceCount,
            dynamicOverlay);

        if (draws.Length > 1)
        {
            EnsureDrawCapacity(draws.Length);
            draws.CopyTo(_draws);
        }

        if (dispatches.Length > 1)
        {
            EnsureDispatchCapacity(dispatches.Length);
            dispatches.CopyTo(_dispatches);
        }
    }

    private void EnsureDrawCapacity(int required)
    {
        if (_draws is not null && _draws.Length >= required)
            return;

        int capacity = Math.Max(required, _draws is null ? 16 : _draws.Length * 2);
        Array.Resize(ref _draws, capacity);
    }

    private void EnsureDispatchCapacity(int required)
    {
        if (_dispatches is not null && _dispatches.Length >= required)
            return;

        int capacity = Math.Max(required, _dispatches is null ? 4 : _dispatches.Length * 2);
        Array.Resize(ref _dispatches, capacity);
    }

    public DrawPacket GetDraw(int index)
    {
        if ((uint)index >= (uint)DrawCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (_draws is null)
        {
            if (index == 0 && DrawCount == 1)
                return FirstDraw;

            throw new InvalidOperationException("Multi-draw render packet is missing expanded draw storage.");
        }

        return _draws[index];
    }

    public DispatchPacket GetDispatch(int index)
    {
        if ((uint)index >= (uint)DispatchCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (_dispatches is null)
        {
            if (index == 0 && DispatchCount == 1)
                return FirstDispatch;

            throw new InvalidOperationException("Multi-dispatch render packet is missing expanded dispatch storage.");
        }

        return _dispatches[index];
    }
}

internal readonly record struct CommandChainKey(
    int FrameSlot,
    RenderViewKey ViewKey,
    int PassIndex,
    int TargetIdentity,
    bool DynamicOverlay,
    int ChainOrdinal);

internal sealed class CommandChain(CommandChainKey key)
{
    public CommandChainKey Key { get; } = key;
    public CommandChainState State { get; set; }
    public CommandBuffer SecondaryCommandBuffer { get; set; }
    public CommandPool SecondaryCommandPool { get; set; }
    public bool OwnsSecondaryCommandPool { get; set; }
    public bool SecondaryCommandBufferExecutable { get; set; }
    public ulong SecondaryCommandBufferGeneration { get; set; }
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
    public bool FrameDataRefreshTouchedDescriptors { get; set; }
    public int SourceStartIndex { get; set; } = -1;
    public int SourceCount { get; set; }
    public int LastRecordedFrameSlot { get; set; } = -1;
    public ulong LastUsedScheduleGeneration { get; set; }
    public bool ScheduledPacket { get; set; }
    public CommandChainDirtyReason DirtyReason { get; set; }
}

internal sealed class RenderPassChainGroup
{
    private CommandChainKey[] _chainKeys = [];
    private int _chainKeyCount;

    public RenderPassChainGroup()
    {
    }

    public RenderPassChainGroup(
        int passIndex,
        int targetIdentity,
        string targetName,
        ReadOnlyMemory<CommandChainKey> chainKeys,
        ulong structuralSignature,
        bool supportsSecondaryCommandBuffers,
        bool dynamicOverlay)
        => Reset(
            passIndex,
            targetIdentity,
            targetName,
            chainKeys.Span,
            structuralSignature,
            supportsSecondaryCommandBuffers,
            dynamicOverlay);

    public int PassIndex { get; private set; }
    public int TargetIdentity { get; private set; }
    public string TargetName { get; private set; } = string.Empty;
    public ReadOnlyMemory<CommandChainKey> ChainKeys => _chainKeys.AsMemory(0, _chainKeyCount);
    public ulong StructuralSignature { get; private set; }
    public bool SupportsSecondaryCommandBuffers { get; private set; }
    public bool DynamicOverlay { get; private set; }

    public void Reset(
        int passIndex,
        int targetIdentity,
        string targetName,
        ReadOnlySpan<CommandChainKey> chainKeys,
        ulong structuralSignature,
        bool supportsSecondaryCommandBuffers,
        bool dynamicOverlay)
    {
        if (_chainKeys.Length < chainKeys.Length)
        {
            int capacity = Math.Max(chainKeys.Length, _chainKeys.Length == 0 ? 8 : _chainKeys.Length * 2);
            Array.Resize(ref _chainKeys, capacity);
        }

        chainKeys.CopyTo(_chainKeys);
        _chainKeyCount = chainKeys.Length;
        PassIndex = passIndex;
        TargetIdentity = targetIdentity;
        TargetName = targetName;
        StructuralSignature = structuralSignature;
        SupportsSecondaryCommandBuffers = supportsSecondaryCommandBuffers;
        DynamicOverlay = dynamicOverlay;
    }
}

internal sealed class CommandChainSchedule
{
    private RenderPassChainGroup[] _groups = [];
    private int _groupCount;

    public CommandChainSchedule()
    {
    }

    public CommandChainSchedule(
        ulong structuralSignature,
        ulong resourcePlanRevision,
        ReadOnlyMemory<RenderPassChainGroup> groups)
        => Reset(structuralSignature, resourcePlanRevision, groups.Span);

    public ulong StructuralSignature { get; private set; }
    public ulong ResourcePlanRevision { get; private set; }
    public ReadOnlyMemory<RenderPassChainGroup> Groups => _groups.AsMemory(0, _groupCount);

    public RenderPassChainGroup RentGroup(int index)
    {
        EnsureGroupCapacity(index + 1);
        return _groups[index] ??= new RenderPassChainGroup();
    }

    public void Reset(
        ulong structuralSignature,
        ulong resourcePlanRevision,
        ReadOnlySpan<RenderPassChainGroup> groups)
    {
        EnsureGroupCapacity(groups.Length);
        groups.CopyTo(_groups);
        _groupCount = groups.Length;
        StructuralSignature = structuralSignature;
        ResourcePlanRevision = resourcePlanRevision;
    }

    private void EnsureGroupCapacity(int required)
    {
        if (_groups.Length >= required)
            return;

        int capacity = Math.Max(required, _groups.Length == 0 ? 8 : _groups.Length * 2);
        Array.Resize(ref _groups, capacity);
    }
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
