using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compact command-chain packet header. Variable draw/dispatch payloads and
/// diagnostic target names are owned by the frame publication arena rather
/// than copied into every pooled packet.
/// </summary>
internal sealed class RenderPacket
{
    // Kept only for direct construction in isolated diagnostics/tests. Runtime
    // packet lowering always supplies a frame-publication arena.
    private RenderPacketPayloadArena? _standalonePayloadArena;
    private RenderPacketPayloadArena? _payloadArena;
    private int _leaseCount;

    public RenderViewKey ViewKey { get; private set; }
    public int PassIndex { get; private set; }
    public int TargetIdentity { get; private set; }
    internal int TargetNameDiagnosticIndex { get; private set; } = -1;
    public RenderPacketVolatility Volatility { get; private set; }
    public int DrawStartIndex { get; private set; }
    public int DrawCount { get; private set; }
    public int DispatchStartIndex { get; private set; }
    public int DispatchCount { get; private set; }
    public DescriptorBindingSnapshot DescriptorSnapshot { get; private set; }
    public ResourcePlanSnapshot ResourcePlanSnapshot { get; private set; }
    public RecordedPacketKey RecordedPacketKey { get; private set; }
    public ulong StructuralSignature { get; private set; }
    public ulong FrameDataSignature { get; private set; }
    public int SourceStartIndex { get; private set; }
    public int SourceCount { get; private set; }
    public bool DynamicOverlay { get; private set; }
    internal bool IsSealed { get; private set; }
    internal bool IsLeased => Volatile.Read(ref _leaseCount) != 0;

    internal RenderPacket()
    {
    }

    internal RenderPacket(
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

    /// <summary>Cold diagnostic text. Worker recording must use <see cref="TargetIdentity"/>.</summary>
    internal string GetDiagnosticTargetName()
        => _payloadArena is null || TargetNameDiagnosticIndex < 0
            ? string.Empty
            : _payloadArena.GetTargetName(TargetNameDiagnosticIndex);

    // Compatibility properties intentionally resolve a range through the arena;
    // they do not reintroduce packet-owned draw/dispatch arrays.
    public DrawPacket FirstDraw
        => DrawCount == 0 ? default : _payloadArena!.GetDraw(DrawStartIndex);
    public DispatchPacket FirstDispatch
        => DispatchCount == 0 ? default : _payloadArena!.GetDispatch(DispatchStartIndex);

    internal void Reset(
        RenderPacketPayloadArena payloadArena,
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
        ArgumentNullException.ThrowIfNull(payloadArena);
        EnsureMutable();
        _payloadArena = payloadArena;
        ViewKey = viewKey;
        PassIndex = passIndex;
        TargetIdentity = targetIdentity;
        TargetNameDiagnosticIndex = payloadArena.AppendTargetName(targetName);
        Volatility = volatility;
        DrawStartIndex = payloadArena.AppendDraws(draws);
        DrawCount = draws.Length;
        DispatchStartIndex = payloadArena.AppendDispatches(dispatches);
        DispatchCount = dispatches.Length;
        DescriptorSnapshot = descriptorSnapshot;
        ResourcePlanSnapshot = resourcePlanSnapshot;
        RecordedPacketKey = default;
        StructuralSignature = structuralSignature;
        FrameDataSignature = frameDataSignature;
        SourceStartIndex = sourceStartIndex;
        SourceCount = sourceCount;
        DynamicOverlay = dynamicOverlay;
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
        RenderPacketPayloadArena standaloneArena = _standalonePayloadArena ??= new();
        standaloneArena.ResetForPublication();
        Reset(
            standaloneArena,
            viewKey,
            passIndex,
            targetIdentity,
            targetName,
            volatility,
            draws,
            dispatches,
            descriptorSnapshot,
            resourcePlanSnapshot,
            structuralSignature,
            frameDataSignature,
            sourceStartIndex,
            sourceCount,
            dynamicOverlay);
    }

    internal void Reset(
        RenderPacketPayloadArena payloadArena,
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
        if (drawCount is < 0 or > 1 || dispatchCount is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                drawCount > 1 ? nameof(drawCount) : nameof(dispatchCount),
                "Single-payload reset accepts at most one draw and dispatch.");

        Span<DrawPacket> draws = stackalloc DrawPacket[1];
        Span<DispatchPacket> dispatches = stackalloc DispatchPacket[1];
        if (drawCount != 0)
            draws[0] = firstDraw;
        if (dispatchCount != 0)
            dispatches[0] = firstDispatch;
        Reset(
            payloadArena,
            viewKey,
            passIndex,
            targetIdentity,
            targetName,
            volatility,
            draws[..drawCount],
            dispatches[..dispatchCount],
            descriptorSnapshot,
            resourcePlanSnapshot,
            structuralSignature,
            frameDataSignature,
            sourceStartIndex,
            sourceCount,
            dynamicOverlay);
    }

    internal void SetRecordedPacketKey(in RecordedPacketKey key)
    {
        EnsureMutable();
        RecordedPacketKey = key;
    }

    internal void Seal()
    {
        EnsureMutable();
        IsSealed = true;
    }

    internal void AcquireLease()
    {
        EnsureSealed();
        _payloadArena!.AcquireLease();
        Interlocked.Increment(ref _leaseCount);
    }

    internal void ReleaseLease()
    {
        if (Interlocked.Decrement(ref _leaseCount) >= 0)
        {
            _payloadArena!.ReleaseLease();
            return;
        }

        Interlocked.Increment(ref _leaseCount);
        throw new InvalidOperationException("Render-packet lease underflow.");
    }

    internal void PrepareForReuse()
    {
        if (Volatile.Read(ref _leaseCount) != 0)
            throw new InvalidOperationException("A leased render packet cannot be reused.");

        IsSealed = false;
        _payloadArena = null;
        TargetNameDiagnosticIndex = -1;
    }

    public DrawPacket GetDraw(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)DrawCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _payloadArena!.GetDraw(DrawStartIndex + index);
    }

    public DispatchPacket GetDispatch(int index)
    {
        EnsureSealed();
        if ((uint)index >= (uint)DispatchCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _payloadArena!.GetDispatch(DispatchStartIndex + index);
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
