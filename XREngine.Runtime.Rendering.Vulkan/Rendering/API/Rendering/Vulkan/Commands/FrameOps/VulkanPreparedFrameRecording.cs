namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable frame-slot storage frozen before native command recording begins.
/// Writers publish prepared draws serially; workers receive only stable indices
/// into the frozen array.
/// </summary>
internal sealed class VulkanPreparedFrameRecording
{
    private VulkanPrimaryPlanNode[] _primaryPlanNodes =
        new VulkanPrimaryPlanNode[64];
    private VkPreparedMeshDraw[] _meshDraws = new VkPreparedMeshDraw[64];
    private VulkanPreparedCommandChain[] _commandChains =
        new VulkanPreparedCommandChain[16];
    // Prepared chains retain their packet snapshots independently from the
    // schedule cache. The cache may replace a chain's publication while a
    // worker still encodes this frame-slot payload.
    private RenderPacket[] _packets = new RenderPacket[16];
    private int _primaryPlanNodeCount;
    private int _meshDrawCount;
    private int _commandChainCount;
    private bool _hasPrimaryPlan;

    internal int FrameSlot { get; private set; } = -1;
    internal ulong Generation { get; private set; }
    internal bool HasPrimaryPlan => _hasPrimaryPlan;
    internal int PrimaryPlanNodeCount => _primaryPlanNodeCount;
    internal ulong PrimaryPlanIdentity { get; private set; }
    internal int MeshDrawCount => _meshDrawCount;
    internal int CommandChainCount => _commandChainCount;
    internal int PacketCount { get; private set; }
    internal bool IsFrozen { get; private set; }
    /// <summary>
    /// Optional immutable frame-plan publication for consumers that need the
    /// complete lowered operation/output snapshot alongside prepared draws.
    /// </summary>
    internal FramePlan? FramePlan { get; private set; }

    internal void Begin(int frameSlot, ulong generation)
    {
        Reset();
        FrameSlot = frameSlot;
        Generation = generation;
    }

    internal void AddPrimaryPlan(VulkanPrimaryCommandPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording is frozen.");
        if (_hasPrimaryPlan)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording already owns a primary plan.");

        EnsurePrimaryPlanCapacity(plan.Count);
        for (int index = 0; index < plan.Count; index++)
            _primaryPlanNodes[index] = plan.GetNode(index);

        _primaryPlanNodeCount = plan.Count;
        PrimaryPlanIdentity = plan.Identity;
        _hasPrimaryPlan = true;
    }

    /// <summary>
    /// Associates the frame-slot-owned plan built by lifecycle preparation with
    /// this prepared recording. The caller must not attach a plan from another
    /// frame slot or an unsealed plan.
    /// </summary>
    internal void AttachFramePlan(FramePlan framePlan)
    {
        ArgumentNullException.ThrowIfNull(framePlan);
        if (IsFrozen)
            throw new InvalidOperationException("Prepared Vulkan frame recording is frozen.");
        if (!framePlan.IsSealed)
            throw new InvalidOperationException("Only sealed frame plans may be attached to prepared recording.");
        if (framePlan.FrameSlot != FrameSlot)
        {
            throw new InvalidOperationException(
                $"Frame plan slot {framePlan.FrameSlot} does not match prepared recording slot {FrameSlot}.");
        }

        if (ReferenceEquals(FramePlan, framePlan))
            return;

        FramePlan?.ReleaseLease();
        framePlan.AcquireLease();
        FramePlan = framePlan;
    }

    internal int AddMeshDraw(in VkPreparedMeshDraw draw)
    {
        if (IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording is frozen.");

        EnsureMeshDrawCapacity(_meshDrawCount + 1);
        int index = _meshDrawCount++;
        _meshDraws[index] = draw;
        return index;
    }

    /// <summary>
    /// Reserves source-index-addressable draw slots without constructing
    /// placeholder draw records. Reused command chains need their range to
    /// remain addressable, but workers consume records only for dirty chains.
    /// </summary>
    internal int ReserveMeshDrawSlots(int count)
    {
        if (IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording is frozen.");
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int startIndex = _meshDrawCount;
        EnsureMeshDrawCapacity(checked(_meshDrawCount + count));
        _meshDrawCount += count;
        return startIndex;
    }

    internal int SetMeshDraw(int index, in VkPreparedMeshDraw draw)
    {
        if (IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording is frozen.");
        if ((uint)index >= (uint)_meshDrawCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        _meshDraws[index] = draw;
        return index;
    }

    internal int AddCommandChain(in VulkanPreparedCommandChain commandChain)
    {
        if (IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording is frozen.");
        if (commandChain.PreparedFrameGeneration != Generation ||
            commandChain.PacketIndex < 0 ||
            commandChain.PacketIndex >= PacketCount ||
            commandChain.SourceCount <= 0 ||
            commandChain.PreparedDrawStartIndex < 0 ||
            commandChain.PreparedDrawStartIndex >
                _meshDrawCount - commandChain.SourceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandChain),
                "Prepared command-chain draw range is outside the published draw storage.");
        }

        EnsureCommandChainCapacity(_commandChainCount + 1);
        int index = _commandChainCount++;
        _commandChains[index] = commandChain;
        return index;
    }

    /// <summary>
    /// Retains a sealed packet for the lifetime of this prepared frame. Native
    /// encoders may only consume this retained authority, never a live chain
    /// publication that can be superseded by a subsequent schedule build.
    /// </summary>
    internal int RetainPacket(RenderPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (IsFrozen)
            throw new InvalidOperationException("Prepared Vulkan frame recording is frozen.");
        if (!packet.IsSealed)
            throw new InvalidOperationException("Prepared command chains require a sealed packet snapshot.");

        EnsurePacketCapacity(PacketCount + 1);
        int index = PacketCount++;
        packet.AcquireLease();
        _packets[index] = packet;
        return index;
    }

    internal void Freeze()
    {
        if (FrameSlot < 0)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording has no frame-slot owner.");

        IsFrozen = true;
    }

    internal ref readonly VkPreparedMeshDraw GetMeshDraw(int index)
    {
        if (!IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording must be frozen before consumption.");
        if ((uint)index >= (uint)_meshDrawCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _meshDraws[index];
    }

    internal ref readonly VulkanPrimaryPlanNode GetPrimaryPlanNode(int index)
    {
        if (!IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording must be frozen before consumption.");
        if ((uint)index >= (uint)_primaryPlanNodeCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _primaryPlanNodes[index];
    }

    internal ref readonly VulkanPreparedCommandChain GetCommandChain(int index)
    {
        if (!IsFrozen)
            throw new InvalidOperationException(
                "Prepared Vulkan frame recording must be frozen before consumption.");
        if ((uint)index >= (uint)_commandChainCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _commandChains[index];
    }

    internal RenderPacket GetPacketForEncoding(
        scoped in VulkanPreparedCommandChain commandChain)
    {
        if (!IsFrozen)
            throw new InvalidOperationException("Prepared Vulkan frame recording must be frozen before consumption.");
        if (commandChain.PreparedFrameGeneration != Generation ||
            (uint)commandChain.PacketIndex >= (uint)PacketCount)
        {
            throw new InvalidOperationException("Prepared command-chain packet ownership is stale.");
        }

        RenderPacket packet = _packets[commandChain.PacketIndex];
        ref readonly RecordedPacketKey packetRecordedKey = ref packet.RecordedPacketKey;
        ref readonly VulkanPreparedCommandChainKey authorityKey =
            ref commandChain.Authority.PreparedKey;
        ref readonly RecordedPacketKey authorityRecordedKey =
            ref VulkanPreparedCommandChainKey.GetRecordedPacketKeyReference(in authorityKey);
        if (!packet.IsSealed ||
            !authorityRecordedKey.IsComplete ||
            !packetRecordedKey.MatchesBindingIndependentState(in authorityRecordedKey) ||
            packet.SourceStartIndex != commandChain.SourceStartIndex ||
            packet.SourceCount != commandChain.SourceCount)
        {
            throw new InvalidOperationException("Prepared command-chain packet no longer matches its frozen native key or source range.");
        }

        return packet;
    }

    internal ref readonly VkPreparedMeshDraw GetMeshDrawForOwnerValidation(
        int index)
    {
        if ((uint)index >= (uint)_meshDrawCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _meshDraws[index];
    }

    /// <summary>
    /// Checks a render-thread-owned draw range while the prepared frame is still
    /// being assembled. Worker consumers must continue to use the frozen accessors.
    /// </summary>
    internal bool ContainsMeshDrawRangeForOwnerValidation(int startIndex, int count)
        => startIndex >= 0 &&
           count > 0 &&
           startIndex <= _meshDrawCount - count;

    internal void Reset()
    {
        if (_meshDrawCount > 0)
        {
            for (int index = 0; index < _meshDrawCount; index++)
            {
                if (_meshDraws[index].OwnerIdentity is not null)
                    _meshDraws[index].Release();
            }
            Array.Clear(_meshDraws, 0, _meshDrawCount);
        }

        if (_commandChainCount > 0)
            Array.Clear(_commandChains, 0, _commandChainCount);
        if (PacketCount > 0)
        {
            for (int index = 0; index < PacketCount; index++)
                _packets[index].ReleaseLease();
            Array.Clear(_packets, 0, PacketCount);
        }
        if (_primaryPlanNodeCount > 0)
            Array.Clear(_primaryPlanNodes, 0, _primaryPlanNodeCount);

        _primaryPlanNodeCount = 0;
        _meshDrawCount = 0;
        _commandChainCount = 0;
        PacketCount = 0;
        _hasPrimaryPlan = false;
        FrameSlot = -1;
        Generation = 0;
        PrimaryPlanIdentity = 0;
        FramePlan?.ReleaseLease();
        FramePlan = null;
        IsFrozen = false;
    }

    private void EnsurePrimaryPlanCapacity(int required)
    {
        if (_primaryPlanNodes.Length >= required)
            return;

        int capacity = Math.Max(required, _primaryPlanNodes.Length * 2);
        Array.Resize(ref _primaryPlanNodes, capacity);
    }

    private void EnsureMeshDrawCapacity(int required)
    {
        if (_meshDraws.Length >= required)
            return;

        int capacity = Math.Max(required, _meshDraws.Length * 2);
        Array.Resize(ref _meshDraws, capacity);
    }

    private void EnsureCommandChainCapacity(int required)
    {
        if (_commandChains.Length >= required)
            return;

        int capacity = Math.Max(required, _commandChains.Length * 2);
        Array.Resize(ref _commandChains, capacity);
    }

    private void EnsurePacketCapacity(int required)
    {
        if (_packets.Length >= required)
            return;

        int capacity = Math.Max(required, _packets.Length * 2);
        Array.Resize(ref _packets, capacity);
    }
}
