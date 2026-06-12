namespace XREngine.Rendering;

/// <summary>
/// Stack-only scoped writer for <see cref="XRDataBuffer"/>. It exposes CPU-writable memory only;
/// GPU device addresses remain shader/command-visible addresses and are queried separately.
/// </summary>
public ref struct XRBufferWriter<T> where T : unmanaged
{
    private XRDataBuffer? _buffer;
    private Span<T> _span;
    private XRBufferWriteOptions _options;
    private uint _elementOffset;
    private uint _elementCount;
    private List<XRBufferDirtyRange>? _dirtyRanges;
    private bool _committed;
    private bool _cancelled;
    private bool _disposed;

    internal XRBufferWriter(
        XRDataBuffer buffer,
        Span<T> span,
        uint elementOffset,
        uint elementCount,
        XRBufferWriteOptions options)
    {
        _buffer = buffer;
        _span = span;
        _elementOffset = elementOffset;
        _elementCount = elementCount;
        _options = options;
        _dirtyRanges = null;
        _committed = false;
        _cancelled = false;
        _disposed = false;
    }

    public Span<T> Span
    {
        get
        {
            ThrowIfUnavailable();
            return _span;
        }
    }

    public uint ElementOffset => _elementOffset;
    public uint ElementCount => _elementCount;
    public ulong ByteOffset => (ulong)_elementOffset * (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
    public bool IsCommitted => _committed;
    public bool IsCancelled => _cancelled;

    public void MarkDirty(uint elementOffset, uint elementCount)
    {
        ThrowIfUnavailable();

        if (elementCount == 0u)
            return;

        ulong end = (ulong)elementOffset + elementCount;
        if (end > _elementCount)
            throw new ArgumentOutOfRangeException(nameof(elementCount), $"Dirty range {elementOffset}+{elementCount} exceeds writer length {_elementCount}.");

        _dirtyRanges ??= [];
        _dirtyRanges.Add(new XRBufferDirtyRange(elementOffset, elementCount));
    }

    public void Commit()
    {
        ThrowIfUnavailable();

        XRDataBuffer buffer = _buffer!;
        buffer.CommitWriterRanges<T>(
            _elementOffset,
            _elementCount,
            _dirtyRanges,
            _options);

        _committed = true;
        _disposed = true;
        _span = default;
        _dirtyRanges = null;
        _buffer = null;
    }

    public void Cancel()
    {
        ThrowIfTerminal();
        _cancelled = true;
        _disposed = true;
        _span = default;
        _dirtyRanges = null;
        _buffer = null;
    }

    public void Dispose()
    {
        if (_disposed || _committed || _cancelled || _buffer is null)
            return;

        switch (_options.DisposeBehavior)
        {
            case XRBufferWriterDisposeBehavior.Commit:
                Commit();
                break;
            case XRBufferWriterDisposeBehavior.Cancel:
                Cancel();
                break;
            case XRBufferWriterDisposeBehavior.RequireExplicitCommit:
                _disposed = true;
                _span = default;
                _dirtyRanges = null;
                _buffer = null;
                throw new InvalidOperationException("XRBufferWriter was disposed without an explicit Commit() or Cancel().");
            default:
                throw new ArgumentOutOfRangeException(nameof(_options.DisposeBehavior), _options.DisposeBehavior, null);
        }
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfTerminal();
        if (_buffer is null)
            throw new InvalidOperationException("XRBufferWriter is not initialized.");
    }

    private void ThrowIfTerminal()
    {
        if (_committed)
            throw new InvalidOperationException("XRBufferWriter has already been committed.");
        if (_cancelled)
            throw new InvalidOperationException("XRBufferWriter has already been cancelled.");
        if (_disposed)
            throw new ObjectDisposedException(nameof(XRBufferWriter<T>));
    }
}
