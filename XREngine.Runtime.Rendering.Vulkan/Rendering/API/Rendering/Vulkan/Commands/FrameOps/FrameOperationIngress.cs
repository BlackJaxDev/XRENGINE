namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Short-lived authoring ingress. It lowers producer objects into numeric
/// headers before output/dependency ordering; only final plan publication may
/// resolve a payload to create its sealed encoder-side snapshot.
/// </summary>
internal sealed class FrameOperationIngress
{
    private const int KindCount = (int)EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership + 1;
    private FrameOperationHeader[] _headers = new FrameOperationHeader[64];
    private FrameOpContext[] _contexts = new FrameOpContext[64];
    private FrameOpResourceUseList[] _resourceUses = new FrameOpResourceUseList[64];
    private readonly FrameOp[][] _payloads = new FrameOp[KindCount][];
    private int _count;

    internal int Count => _count;

    internal FrameOperationIngress()
    {
        for (int kind = 0; kind < KindCount; kind++)
            _payloads[kind] = [];
    }

    internal void Populate(FrameOp[] source)
    {
        EnsureCapacity(source.Length);
        Span<int> counts = stackalloc int[KindCount];
        for (int index = 0; index < source.Length; index++)
            counts[(int)source[index].Kind]++;
        for (int kind = 0; kind < KindCount; kind++)
            EnsurePayloadCapacity(kind, counts[kind]);
        counts.Clear();
        for (int index = 0; index < source.Length; index++)
        {
            FrameOp operation = source[index];
            int kind = (int)operation.Kind;
            int payloadIndex = counts[kind]++;
            _payloads[kind][payloadIndex] = operation;
            ref readonly FrameOpContext context = ref operation.ContextReference;
            ref readonly FrameOpResourceUseList resourceUses =
                ref operation.ResourceUsesReference;
            _contexts[index] = context;
            _resourceUses[index] = resourceUses;
            _headers[index] = new FrameOperationHeader(
                operation.Kind,
                payloadIndex,
                operation.PassIndex,
                ResolveTargetIdentity(operation),
                index,
                index,
                index,
                operation.RequiresPrimaryRecordingContext);
        }
        _count = source.Length;
    }

    internal ref readonly FrameOperationHeader GetHeader(int index) => ref _headers[index];
    internal ref readonly FrameOpContext GetContext(int index) => ref _contexts[_headers[index].ContextIndex];
    internal ref readonly FrameOpResourceUseList GetResourceUses(int index) => ref _resourceUses[_headers[index].ResourceUseIndex];
    internal FrameOp GetPayload(int index)
    {
        ref readonly FrameOperationHeader header = ref _headers[index];
        return _payloads[(int)header.OpCode][header.PayloadIndex];
    }

    private static int ResolveTargetIdentity(FrameOp operation)
        => operation.ContextReference.OutputTargetIdentity;

    private void EnsureCapacity(int required)
    {
        if (_headers.Length < required)
            Array.Resize(ref _headers, Math.Max(required, _headers.Length * 2));
        if (_contexts.Length < required)
            Array.Resize(ref _contexts, Math.Max(required, _contexts.Length * 2));
        if (_resourceUses.Length < required)
            Array.Resize(ref _resourceUses, Math.Max(required, _resourceUses.Length * 2));
    }

    private void EnsurePayloadCapacity(int kind, int required)
    {
        if (required == 0 || _payloads[kind].Length >= required)
            return;
        Array.Resize(ref _payloads[kind], Math.Max(required, _payloads[kind].Length == 0 ? 4 : _payloads[kind].Length * 2));
    }
}
