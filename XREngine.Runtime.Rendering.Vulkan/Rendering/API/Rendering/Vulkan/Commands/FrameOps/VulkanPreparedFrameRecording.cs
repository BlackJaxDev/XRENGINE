namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanPreparedMeshDrawColdData(
    VkMeshRenderer Owner,
    VkRenderProgram Program,
    XRFrameBuffer? Target,
    FrameOpContext Context,
    ulong FrameDataGeneration,
    string DiagnosticMeshName);

internal readonly record struct VulkanPreparedFrameStreamTelemetry(
    int HeaderCount, int HeaderHighWater,
    int HeaderBytes, int HeaderHighWaterBytes,
    int DescriptorBindingCount, int DescriptorBindingHighWater,
    int DescriptorBindingBytes, int DescriptorBindingHighWaterBytes,
    int DynamicOffsetCount, int DynamicOffsetHighWater,
    int DynamicOffsetBytes, int DynamicOffsetHighWaterBytes,
    int DescriptorHeapDwordCount, int DescriptorHeapDwordHighWater,
    int DescriptorHeapDwordBytes, int DescriptorHeapDwordHighWaterBytes,
    int VertexBufferCount, int VertexBufferHighWater,
    int VertexBufferBytes, int VertexBufferHighWaterBytes,
    int FramePayloadCount, int FramePayloadHighWater,
    int FramePayloadBytes, int FramePayloadHighWaterBytes,
    int ViewportCount, int ViewportHighWater,
    int ViewportBytes, int ViewportHighWaterBytes,
    int ScissorCount, int ScissorHighWater,
    int ScissorBytes, int ScissorHighWaterBytes,
    int DescriptorImagePayloadCount, int DescriptorImagePayloadHighWater,
    int DescriptorImagePayloadBytes, int DescriptorImagePayloadHighWaterBytes,
    int DescriptorImageRequirementCount, int DescriptorImageRequirementHighWater,
    int DescriptorImageRequirementBytes, int DescriptorImageRequirementHighWaterBytes);

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
    private VulkanPreparedMeshDrawColdData[] _meshDrawColdData = new VulkanPreparedMeshDrawColdData[64];
    private VulkanPreparedDescriptorSetBinding[] _descriptorBindings = new VulkanPreparedDescriptorSetBinding[64];
    private uint[] _dynamicOffsets = new uint[64];
    private uint[] _descriptorHeapPushDwords = new uint[64];
    private Silk.NET.Vulkan.Buffer[] _vertexBuffers = new Silk.NET.Vulkan.Buffer[64];
    private uint[] _vertexBindings = new uint[64];
    private VulkanPreparedFrameDataPayloadHandle[] _frameDataPayloadHandles = new VulkanPreparedFrameDataPayloadHandle[64];
    private Silk.NET.Vulkan.Viewport[] _viewports = new Silk.NET.Vulkan.Viewport[64];
    private Silk.NET.Vulkan.Rect2D[] _scissors = new Silk.NET.Vulkan.Rect2D[64];
    private VulkanPreparedDescriptorImagePayload[] _descriptorImagePayloads = new VulkanPreparedDescriptorImagePayload[64];
    private VulkanPreparedDescriptorImageRequirement[] _descriptorImageRequirements = new VulkanPreparedDescriptorImageRequirement[128];
    private VulkanPreparedCommandChain[] _commandChains =
        new VulkanPreparedCommandChain[16];
    // Prepared chains retain their packet snapshots independently from the
    // schedule cache. The cache may replace a chain's publication while a
    // worker still encodes this frame-slot payload.
    private RenderPacket[] _packets = new RenderPacket[16];
    private int _primaryPlanNodeCount;
    private int _meshDrawCount;
    private int _meshDrawColdDataCount, _descriptorBindingCount, _dynamicOffsetCount, _descriptorHeapPushDwordCount, _vertexBufferCount, _frameDataPayloadHandleCount, _viewportCount, _scissorCount, _descriptorImagePayloadCount, _descriptorImageRequirementCount;
    private int _meshDrawHighWater, _descriptorBindingHighWater, _dynamicOffsetHighWater, _descriptorHeapDwordHighWater, _vertexBufferHighWater, _framePayloadHighWater, _viewportHighWater, _scissorHighWater, _descriptorImagePayloadHighWater, _descriptorImageRequirementHighWater;
    private int _commandChainCount;
    private bool _hasPrimaryPlan;

    internal int FrameSlot { get; private set; } = -1;
    internal ulong Generation { get; private set; }
    internal bool HasPrimaryPlan => _hasPrimaryPlan;
    internal int PrimaryPlanNodeCount => _primaryPlanNodeCount;
    internal ulong PrimaryPlanIdentity { get; private set; }
    internal int MeshDrawCount => _meshDrawCount;
    internal int DescriptorImagePayloadCount => _descriptorImagePayloadCount;
    internal int DescriptorImageRequirementCount => _descriptorImageRequirementCount;
    internal int CommandChainCount => _commandChainCount;
    internal int PacketCount { get; private set; }
    internal bool IsFrozen { get; private set; }
    internal VulkanPreparedFrameStreamTelemetry StreamTelemetry => new(
        _meshDrawCount, _meshDrawHighWater,
        ByteCount<VkPreparedMeshDraw>(_meshDrawCount), ByteCount<VkPreparedMeshDraw>(_meshDrawHighWater),
        _descriptorBindingCount, _descriptorBindingHighWater,
        ByteCount<VulkanPreparedDescriptorSetBinding>(_descriptorBindingCount), ByteCount<VulkanPreparedDescriptorSetBinding>(_descriptorBindingHighWater),
        _dynamicOffsetCount, _dynamicOffsetHighWater,
        ByteCount<uint>(_dynamicOffsetCount), ByteCount<uint>(_dynamicOffsetHighWater),
        _descriptorHeapPushDwordCount, _descriptorHeapDwordHighWater,
        ByteCount<uint>(_descriptorHeapPushDwordCount), ByteCount<uint>(_descriptorHeapDwordHighWater),
        _vertexBufferCount, _vertexBufferHighWater,
        ByteCount<Silk.NET.Vulkan.Buffer>(_vertexBufferCount), ByteCount<Silk.NET.Vulkan.Buffer>(_vertexBufferHighWater),
        _frameDataPayloadHandleCount, _framePayloadHighWater,
        ByteCount<VulkanPreparedFrameDataPayloadHandle>(_frameDataPayloadHandleCount), ByteCount<VulkanPreparedFrameDataPayloadHandle>(_framePayloadHighWater),
        _viewportCount, _viewportHighWater,
        ByteCount<Silk.NET.Vulkan.Viewport>(_viewportCount), ByteCount<Silk.NET.Vulkan.Viewport>(_viewportHighWater),
        _scissorCount, _scissorHighWater,
        ByteCount<Silk.NET.Vulkan.Rect2D>(_scissorCount), ByteCount<Silk.NET.Vulkan.Rect2D>(_scissorHighWater),
        _descriptorImagePayloadCount, _descriptorImagePayloadHighWater,
        ByteCount<VulkanPreparedDescriptorImagePayload>(_descriptorImagePayloadCount), ByteCount<VulkanPreparedDescriptorImagePayload>(_descriptorImagePayloadHighWater),
        _descriptorImageRequirementCount, _descriptorImageRequirementHighWater,
        ByteCount<VulkanPreparedDescriptorImageRequirement>(_descriptorImageRequirementCount), ByteCount<VulkanPreparedDescriptorImageRequirement>(_descriptorImageRequirementHighWater));
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
        _meshDrawHighWater = Math.Max(_meshDrawHighWater, _meshDrawCount);
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

        ValidatePreparedDrawRanges();
        PublishStreamTelemetry();
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

    internal int AddMeshDrawColdData(in VulkanPreparedMeshDrawColdData value)
    {
        EnsureCapacity(ref _meshDrawColdData, _meshDrawColdDataCount + 1);
        int index = _meshDrawColdDataCount++;
        _meshDrawColdData[index] = value;
        return index;
    }

    internal ref readonly VulkanPreparedMeshDrawColdData GetMeshDrawColdData(int index)
    {
        if ((uint)index >= (uint)_meshDrawColdDataCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ref _meshDrawColdData[index];
    }

    internal VulkanPreparedStreamRange AppendDescriptorBindings(ReadOnlySpan<VulkanPreparedDescriptorSetBinding> values) => Append(values, ref _descriptorBindings, ref _descriptorBindingCount, ref _descriptorBindingHighWater);
    internal VulkanPreparedStreamRange AppendDynamicOffsets(ReadOnlySpan<uint> values) => Append(values, ref _dynamicOffsets, ref _dynamicOffsetCount, ref _dynamicOffsetHighWater);
    internal VulkanPreparedStreamRange AppendDescriptorHeapPushDwords(ReadOnlySpan<uint> values) => Append(values, ref _descriptorHeapPushDwords, ref _descriptorHeapPushDwordCount, ref _descriptorHeapDwordHighWater);
    internal VulkanPreparedStreamRange AppendVertexBuffers(ReadOnlySpan<Silk.NET.Vulkan.Buffer> buffers, ReadOnlySpan<uint> bindings)
    {
        if (buffers.Length != bindings.Length)
            throw new ArgumentException("Vertex buffer and binding snapshots must have the same length.");
        int start = _vertexBufferCount;
        EnsureCapacity(ref _vertexBuffers, checked(start + buffers.Length));
        EnsureCapacity(ref _vertexBindings, checked(start + bindings.Length));
        buffers.CopyTo(_vertexBuffers.AsSpan(start));
        bindings.CopyTo(_vertexBindings.AsSpan(start));
        _vertexBufferCount += buffers.Length;
        _vertexBufferHighWater = Math.Max(_vertexBufferHighWater, _vertexBufferCount);
        return new VulkanPreparedStreamRange(start, buffers.Length);
    }

    internal VulkanPreparedStreamRange ReserveDescriptorBindings(int count) => Reserve(ref _descriptorBindings, ref _descriptorBindingCount, ref _descriptorBindingHighWater, count);
    internal VulkanPreparedStreamRange ReserveDynamicOffsets(int count) => Reserve(ref _dynamicOffsets, ref _dynamicOffsetCount, ref _dynamicOffsetHighWater, count);
    internal VulkanPreparedStreamRange ReserveDescriptorHeapPushDwords(int count) => Reserve(ref _descriptorHeapPushDwords, ref _descriptorHeapPushDwordCount, ref _descriptorHeapDwordHighWater, count);
    internal VulkanPreparedStreamRange ReserveVertexBuffers(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int start = _vertexBufferCount;
        EnsureCapacity(ref _vertexBuffers, checked(start + count));
        EnsureCapacity(ref _vertexBindings, checked(start + count));
        _vertexBufferCount += count;
        _vertexBufferHighWater = Math.Max(_vertexBufferHighWater, _vertexBufferCount);
        return new VulkanPreparedStreamRange(start, count);
    }
    internal VulkanPreparedStreamRange ReserveFrameDataPayloadHandles(int count) => Reserve(ref _frameDataPayloadHandles, ref _frameDataPayloadHandleCount, ref _framePayloadHighWater, count);
    internal void SetDescriptorBinding(in VulkanPreparedStreamRange range, int index, in VulkanPreparedDescriptorSetBinding value) => Set(_descriptorBindings, range, _descriptorBindingCount, index, value);
    internal void SetDynamicOffset(in VulkanPreparedStreamRange range, int index, uint value) => Set(_dynamicOffsets, range, _dynamicOffsetCount, index, value);
    internal void SetDescriptorHeapPushDword(in VulkanPreparedStreamRange range, int index, uint value) => Set(_descriptorHeapPushDwords, range, _descriptorHeapPushDwordCount, index, value);
    internal void SetVertexBuffer(in VulkanPreparedStreamRange range, int index, Silk.NET.Vulkan.Buffer buffer, uint binding)
    {
        Set(_vertexBuffers, range, _vertexBufferCount, index, buffer);
        Set(_vertexBindings, range, _vertexBufferCount, index, binding);
    }
    internal void SetFrameDataPayloadHandle(in VulkanPreparedStreamRange range, int index, in VulkanPreparedFrameDataPayloadHandle value) => Set(_frameDataPayloadHandles, range, _frameDataPayloadHandleCount, index, value);
    internal VulkanPreparedStreamRange AppendFrameDataPayloadHandles(ReadOnlySpan<VulkanPreparedFrameDataPayloadHandle> values) => Append(values, ref _frameDataPayloadHandles, ref _frameDataPayloadHandleCount, ref _framePayloadHighWater);
    internal VulkanPreparedStreamRange AppendViewports(ReadOnlySpan<Silk.NET.Vulkan.Viewport> values) => Append(values, ref _viewports, ref _viewportCount, ref _viewportHighWater);
    internal VulkanPreparedStreamRange AppendScissors(ReadOnlySpan<Silk.NET.Vulkan.Rect2D> values) => Append(values, ref _scissors, ref _scissorCount, ref _scissorHighWater);
    internal VulkanPreparedStreamRange AddDescriptorImagePayload(in VulkanPreparedDescriptorImagePayload value)
    {
        int index = _descriptorImagePayloadCount;
        EnsureCapacity(ref _descriptorImagePayloads, index + 1);
        _descriptorImagePayloads[index] = value;
        _descriptorImagePayloadCount++;
        _descriptorImagePayloadHighWater = Math.Max(_descriptorImagePayloadHighWater, _descriptorImagePayloadCount);
        return new VulkanPreparedStreamRange(index, 1);
    }

    internal VulkanPreparedStreamRange AddDescriptorImageRequirement(in VulkanPreparedDescriptorImageRequirement value)
    {
        int index = _descriptorImageRequirementCount;
        EnsureCapacity(ref _descriptorImageRequirements, index + 1);
        _descriptorImageRequirements[index] = value;
        _descriptorImageRequirementCount++;
        _descriptorImageRequirementHighWater = Math.Max(_descriptorImageRequirementHighWater, _descriptorImageRequirementCount);
        return new VulkanPreparedStreamRange(index, 1);
    }

    internal ReadOnlySpan<VulkanPreparedDescriptorSetBinding> GetDescriptorBindings(in VulkanPreparedStreamRange range) => GetRange(_descriptorBindings, range, _descriptorBindingCount);
    internal ReadOnlySpan<uint> GetDynamicOffsets(in VulkanPreparedStreamRange range) => GetRange(_dynamicOffsets, range, _dynamicOffsetCount);
    internal ReadOnlySpan<uint> GetDescriptorHeapPushDwords(in VulkanPreparedStreamRange range) => GetRange(_descriptorHeapPushDwords, range, _descriptorHeapPushDwordCount);
    internal ReadOnlySpan<Silk.NET.Vulkan.Buffer> GetVertexBuffers(in VulkanPreparedStreamRange range) => GetRange(_vertexBuffers, range, _vertexBufferCount);
    internal ReadOnlySpan<uint> GetVertexBindings(in VulkanPreparedStreamRange range) => GetRange(_vertexBindings, range, _vertexBufferCount);
    internal ReadOnlySpan<VulkanPreparedFrameDataPayloadHandle> GetFrameDataPayloadHandles(in VulkanPreparedStreamRange range) => GetRange(_frameDataPayloadHandles, range, _frameDataPayloadHandleCount);
    internal ReadOnlySpan<Silk.NET.Vulkan.Viewport> GetViewports(in VulkanPreparedStreamRange range) => GetRange(_viewports, range, _viewportCount);
    internal ReadOnlySpan<Silk.NET.Vulkan.Rect2D> GetScissors(in VulkanPreparedStreamRange range) => GetRange(_scissors, range, _scissorCount);
    internal ReadOnlySpan<VulkanPreparedDescriptorImagePayload> GetDescriptorImagePayloads(in VulkanPreparedStreamRange range) => GetRange(_descriptorImagePayloads, range, _descriptorImagePayloadCount);
    internal ReadOnlySpan<VulkanPreparedDescriptorImageRequirement> GetDescriptorImageRequirements(in VulkanPreparedStreamRange range) => GetRange(_descriptorImageRequirements, range, _descriptorImageRequirementCount);

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
            Array.Clear(_meshDraws, 0, _meshDrawCount);

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
        _meshDrawColdDataCount = _descriptorBindingCount = _dynamicOffsetCount = _descriptorHeapPushDwordCount = _vertexBufferCount = _frameDataPayloadHandleCount = _viewportCount = _scissorCount = _descriptorImagePayloadCount = _descriptorImageRequirementCount = 0;
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

    private static VulkanPreparedStreamRange Append<T>(ReadOnlySpan<T> values, ref T[] stream, ref int count, ref int highWater)
    {
        int start = count;
        EnsureCapacity(ref stream, checked(count + values.Length));
        values.CopyTo(stream.AsSpan(start));
        count += values.Length;
        highWater = Math.Max(highWater, count);
        return new(start, values.Length);
    }

    private static VulkanPreparedStreamRange Reserve<T>(ref T[] stream, ref int count, ref int highWater, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        int start = count;
        EnsureCapacity(ref stream, checked(start + length));
        count += length;
        highWater = Math.Max(highWater, count);
        return new(start, length);
    }

    private static void Set<T>(T[] stream, in VulkanPreparedStreamRange range, int count, int index, in T value)
    {
        if ((uint)index >= (uint)range.Count || !range.IsValidFor(count))
            throw new ArgumentOutOfRangeException(nameof(index));
        stream[range.Start + index] = value;
    }

    private static ReadOnlySpan<T> GetRange<T>(T[] stream, in VulkanPreparedStreamRange range, int count)
        => range.IsValidFor(count) ? stream.AsSpan(range.Start, range.Count) : throw new InvalidOperationException("Prepared stream range is outside its frozen frame-slot stream.");

    private static int ByteCount<T>(int count)
        => checked(count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());

    private static void EnsureCapacity<T>(ref T[] stream, int required)
    {
        if (stream.Length >= required)
            return;
        Array.Resize(ref stream, Math.Max(required, stream.Length * 2));
    }

    private void ValidatePreparedDrawRanges()
    {
        for (int index = 0; index < _meshDrawCount; index++)
        {
            VulkanPreparedMeshDrawState state = _meshDraws[index].RecordingState;
            if (!state.DescriptorBindings.IsValidFor(_descriptorBindingCount) || !state.DynamicOffsets.IsValidFor(_dynamicOffsetCount) || !state.DescriptorHeapPushDwords.IsValidFor(_descriptorHeapPushDwordCount) || !state.VertexBuffers.IsValidFor(_vertexBufferCount) || !state.FrameDataPayloadHandles.IsValidFor(_frameDataPayloadHandleCount) || !state.DescriptorImagePayloads.IsValidFor(_descriptorImagePayloadCount) || !state.DescriptorImageRequirements.IsValidFor(_descriptorImageRequirementCount) || (uint)state.ColdDataIndex >= (uint)_meshDrawColdDataCount)
                throw new InvalidOperationException("Prepared mesh draw contains a range outside its frame-slot stream.");
            ref readonly VkPreparedMeshDraw draw = ref _meshDraws[index];
            if (!draw.IndexedViewports.IsValidFor(_viewportCount) || !draw.IndexedScissors.IsValidFor(_scissorCount) || draw.IndexedViewports.Count != draw.IndexedScissors.Count)
                throw new InvalidOperationException("Prepared mesh draw viewport/scissor ranges are invalid.");
        }
    }

    private void PublishStreamTelemetry()
    {
        VulkanPreparedFrameStreamTelemetry telemetry = StreamTelemetry;
        int elements = checked(
            telemetry.HeaderCount + telemetry.DescriptorBindingCount +
            telemetry.DynamicOffsetCount + telemetry.DescriptorHeapDwordCount +
            telemetry.VertexBufferCount + telemetry.FramePayloadCount +
            telemetry.ViewportCount + telemetry.ScissorCount +
            telemetry.DescriptorImagePayloadCount + telemetry.DescriptorImageRequirementCount);
        int bytes = checked(
            telemetry.HeaderBytes + telemetry.DescriptorBindingBytes +
            telemetry.DynamicOffsetBytes + telemetry.DescriptorHeapDwordBytes +
            telemetry.VertexBufferBytes + telemetry.FramePayloadBytes +
            telemetry.ViewportBytes + telemetry.ScissorBytes +
            telemetry.DescriptorImagePayloadBytes + telemetry.DescriptorImageRequirementBytes);
        int highWaterBytes = checked(
            telemetry.HeaderHighWaterBytes + telemetry.DescriptorBindingHighWaterBytes +
            telemetry.DynamicOffsetHighWaterBytes + telemetry.DescriptorHeapDwordHighWaterBytes +
            telemetry.VertexBufferHighWaterBytes + telemetry.FramePayloadHighWaterBytes +
            telemetry.ViewportHighWaterBytes + telemetry.ScissorHighWaterBytes +
            telemetry.DescriptorImagePayloadHighWaterBytes + telemetry.DescriptorImageRequirementHighWaterBytes);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPreparedFrameStreamTelemetry(
            elements, bytes, highWaterBytes);
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
