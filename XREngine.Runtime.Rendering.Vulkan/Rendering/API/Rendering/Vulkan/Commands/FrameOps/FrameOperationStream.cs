namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Lowered frame-operation storage. Headers form the canonical ordered stream;
/// each opcode owns a dense payload array. The only object resolution API is
/// reserved for the final primary-recording dispatch boundary.
/// </summary>
internal sealed class FrameOperationStream
{
    private const int KindCount = (int)EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership + 1;
    // Dense per-opcode numeric payload references. Managed producer snapshots
    // are cold encoder sidecar data, never part of the planning stream.
    private readonly int[][] _payloads = new int[KindCount][];
    private FrameOperationHeader[] _headers = new FrameOperationHeader[64];
    private FrameOpContext[] _contexts = new FrameOpContext[64];
    private FrameOpResourceUseList[] _resourceUses = new FrameOpResourceUseList[64];
    private FrameOp[] _encoderPayloads = new FrameOp[64];
    private int _count;

    internal static FrameOperationStream Empty { get; } = new();
    internal int Count => _count;

    /// <summary>Explicit cold compatibility bridge for pre-plan/OpenXR callers.</summary>
    internal static FrameOperationStream CreateCompatibility(FrameOp[] operations)
    {
        FrameOperationIngress ingress = new();
        ingress.Populate(operations);
        int[] order = new int[operations.Length];
        for (int index = 0; index < order.Length; index++)
            order[index] = index;
        FrameOperationStream stream = new();
        stream.Lower(ingress, order);
        return stream;
    }

    internal void Reset()
    {
        if (_count > 0)
        {
            Array.Clear(_headers, 0, _count);
            Array.Clear(_contexts, 0, _count);
            Array.Clear(_resourceUses, 0, _count);
            Array.Clear(_encoderPayloads, 0, _count);
        }
        _count = 0;
    }

    /// <summary>
    /// Lowers the producer-owned authoring array exactly once after its numeric
    /// order has been compiled. Payload snapshots become frame-plan-owned here.
    /// </summary>
    internal void Lower(FrameOperationIngress source, ReadOnlySpan<int> order)
    {
        Reset();
        EnsureCapacity(order.Length);
        Span<int> payloadCounts = stackalloc int[KindCount];
        for (int orderIndex = 0; orderIndex < order.Length; orderIndex++)
        {
            FrameOp operation = source.GetPayload(order[orderIndex]);
            int kind = (int)operation.Kind;
            if ((uint)kind >= KindCount)
                throw new InvalidOperationException("Frame operation has an unsupported opcode.");
            payloadCounts[kind]++;
        }
        for (int kind = 0; kind < KindCount; kind++)
            EnsurePayloadCapacity(kind, payloadCounts[kind]);

        payloadCounts.Clear();
        for (int orderIndex = 0; orderIndex < order.Length; orderIndex++)
        {
            int sourceIndex = order[orderIndex];
            FrameOp operation = source.GetPayload(sourceIndex).CreateSealedPlanSnapshot();
            int kind = (int)operation.Kind;
            int payloadIndex = payloadCounts[kind]++;
            _payloads[kind][payloadIndex] = orderIndex;
            int targetIdentity = ResolveTargetIdentity(operation);
            _headers[orderIndex] = new FrameOperationHeader(
                operation.Kind,
                payloadIndex,
                operation.PassIndex,
                targetIdentity,
                orderIndex,
                orderIndex,
                sourceIndex,
                operation.RequiresPrimaryRecordingContext);
            ref readonly FrameOpContext context = ref operation.ContextReference;
            ref readonly FrameOpResourceUseList resourceUses =
                ref operation.ResourceUsesReference;
            _contexts[orderIndex] = context;
            _resourceUses[orderIndex] = resourceUses;
            _encoderPayloads[orderIndex] = operation;
        }

        _count = order.Length;
    }

    internal ref readonly FrameOperationHeader GetHeader(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ref _headers[index];
    }

    internal ref readonly FrameOpContext GetContext(int index)
        => ref _contexts[GetHeader(index).ContextIndex];

    internal ref readonly FrameOpResourceUseList GetResourceUses(int index)
        => ref _resourceUses[GetHeader(index).ResourceUseIndex];

    /// <summary>Final dispatch boundary only; planning and workers use headers.</summary>
    internal ref readonly FrameOp GetPayloadForPrimaryDispatch(int index)
    {
        ref readonly FrameOperationHeader header = ref GetHeader(index);
        int encoderPayloadIndex = _payloads[(int)header.OpCode][header.PayloadIndex];
        return ref _encoderPayloads[encoderPayloadIndex];
    }

    internal ReadOnlySpan<FrameOp> GetEncoderPayloadRange(int startIndex, int count)
        => _encoderPayloads.AsSpan(startIndex, count);

    private void EnsureCapacity(int required)
    {
        if (_headers.Length < required)
            Array.Resize(ref _headers, Math.Max(required, _headers.Length * 2));
        if (_contexts.Length < required)
            Array.Resize(ref _contexts, Math.Max(required, _contexts.Length * 2));
        if (_resourceUses.Length < required)
            Array.Resize(ref _resourceUses, Math.Max(required, _resourceUses.Length * 2));
        if (_encoderPayloads.Length < required)
            Array.Resize(ref _encoderPayloads, Math.Max(required, _encoderPayloads.Length * 2));
    }

    private void EnsurePayloadCapacity(int kind, int required)
    {
        if (required == 0)
            return;
        int[] payloads = _payloads[kind] ?? Array.Empty<int>();
        if (payloads.Length < required)
            Array.Resize(ref payloads, Math.Max(required, payloads.Length == 0 ? 4 : payloads.Length * 2));
        _payloads[kind] = payloads;
    }

    private static int ResolveTargetIdentity(FrameOp operation)
        => operation.ContextReference.OutputTargetIdentity;

}
