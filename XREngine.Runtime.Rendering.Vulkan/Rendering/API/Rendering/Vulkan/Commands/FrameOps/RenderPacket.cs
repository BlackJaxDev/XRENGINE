using System;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed class RenderPacket
{
    private DrawPacket[]? _draws;
    private DispatchPacket[]? _dispatches;
    private int _leaseCount;

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
    /// <summary>
    /// Immutable native state captured while packetizing this operation. This is
    /// intentionally separate from logical scheduling identity so command reuse
    /// never relies on managed-object hashes or debug names.
    /// </summary>
    public RecordedPacketKey RecordedPacketKey { get; private set; }
    public ulong StructuralSignature { get; private set; }
    public ulong FrameDataSignature { get; private set; }
    public int SourceStartIndex { get; private set; }
    public int SourceCount { get; private set; }
    public bool DynamicOverlay { get; private set; }
    internal bool IsSealed { get; private set; }
    internal bool IsLeased => Volatile.Read(ref _leaseCount) != 0;

    internal void Reset(
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
        EnsureMutable();
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
        RecordedPacketKey = default;
        StructuralSignature = structuralSignature;
        FrameDataSignature = frameDataSignature;
        SourceStartIndex = sourceStartIndex;
        SourceCount = sourceCount;
        DynamicOverlay = dynamicOverlay;
    }

    internal void SetRecordedPacketKey(in RecordedPacketKey key)
    {
        EnsureMutable();
        RecordedPacketKey = key;
    }

    /// <summary>
    /// Publishes this fully lowered packet to command-chain consumers. A pooled
    /// packet cannot be changed until its owner explicitly prepares it for a
    /// later lowering pass after all leases have been released.
    /// </summary>
    internal void Seal()
    {
        EnsureMutable();
        IsSealed = true;
    }

    internal void AcquireLease()
    {
        EnsureSealed();
        Interlocked.Increment(ref _leaseCount);
    }

    internal void ReleaseLease()
    {
        if (Interlocked.Decrement(ref _leaseCount) < 0)
        {
            Interlocked.Increment(ref _leaseCount);
            throw new InvalidOperationException("Render-packet lease underflow.");
        }
    }

    /// <summary>
    /// Returns this packet to its pool's private construction state. This is
    /// deliberately separate from <see cref="Reset"/> so publication cannot be
    /// silently overwritten while a consumer still holds the packet.
    /// </summary>
    internal void PrepareForReuse()
    {
        if (Volatile.Read(ref _leaseCount) != 0)
            throw new InvalidOperationException("A leased render packet cannot be reused.");

        IsSealed = false;
    }

    internal void Reset(
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
        EnsureSealed();
        if ((uint)index >= (uint)DrawCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (DrawCount == 1)
        {
            return FirstDraw;
        }

        if (_draws is null)
            throw new InvalidOperationException("Multi-draw render packet is missing expanded draw storage.");

        return _draws[index];
    }

    public DispatchPacket GetDispatch(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)DispatchCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (DispatchCount == 1)
        {
            return FirstDispatch;
        }

        if (_dispatches is null)
            throw new InvalidOperationException("Multi-dispatch render packet is missing expanded dispatch storage.");

        return _dispatches[index];
    }

    private void EnsureMutable()
    {
        if (IsSealed)
            throw new InvalidOperationException("A sealed render packet cannot be mutated.");
        if (Volatile.Read(ref _leaseCount) != 0)
            throw new InvalidOperationException("A leased render packet cannot be mutated.");
    }

    private void EnsureSealed()
    {
        if (!IsSealed)
            throw new InvalidOperationException("A render packet must be sealed before consumption.");
    }
}
