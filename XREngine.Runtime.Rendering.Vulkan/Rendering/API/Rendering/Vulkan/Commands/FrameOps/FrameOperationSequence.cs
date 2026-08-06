namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stack-friendly view over either a sealed numeric frame-operation stream or
/// an explicitly retained compatibility array. It carries no spans and can be
/// stored in recording state without materializing the stream.
/// </summary>
internal readonly struct FrameOperationSequence
{
    private readonly FrameOperationStream? _stream;
    private readonly FrameOp[]? _compatibilityOperations;

    internal FrameOperationSequence(FrameOperationStream stream)
        => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    internal FrameOperationSequence(FrameOp[] compatibilityOperations)
        => _compatibilityOperations = compatibilityOperations ??
            throw new ArgumentNullException(nameof(compatibilityOperations));

    internal int Length => _stream?.Count ?? _compatibilityOperations?.Length ?? 0;
    internal bool IsNumericStream => _stream is not null;
    internal FrameOp[] CompatibilityOperations
        => _compatibilityOperations ??
            throw new InvalidOperationException("Numeric operation streams cannot be materialized for compatibility sorting.");

    internal FrameOp this[int index]
        => _stream is null
            ? _compatibilityOperations![index]
            : _stream.GetPayloadForPrimaryDispatch(index);

    internal ref readonly FrameOperationHeader GetHeader(int index)
    {
        if (_stream is null)
            throw new InvalidOperationException("Compatibility operation sequences do not publish numeric headers.");
        return ref _stream.GetHeader(index);
    }

    public Enumerator GetEnumerator() => new(this);

    public struct Enumerator(FrameOperationSequence sequence)
    {
        private int _index = -1;
        public FrameOp Current => sequence[_index];
        public bool MoveNext() => ++_index < sequence.Length;
    }

    public static implicit operator FrameOperationSequence(FrameOp[] operations)
        => new(operations);
}
