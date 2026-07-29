using System;

namespace XREngine.Rendering.Vulkan;

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
