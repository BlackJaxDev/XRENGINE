using System.Buffers;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Runtime.Memory;

namespace XREngine.Core.Files;

/// <summary>
/// Heap-allocated owner for payload bytes that must cross a job boundary or an awaited
/// continuation. Created from <see cref="CookedPayloadLease.TransferToOwner"/>, or from
/// <see cref="MapFile"/> for a whole file. Exposes the bytes as <see cref="Memory{T}"/> so consumers
/// that take <see cref="ReadOnlyMemory{T}"/> can use mapped, native, or pooled storage without a
/// managed copy. Dispose releases the storage exactly once.
/// </summary>
public sealed unsafe class CookedPayloadOwner : MemoryManager<byte>
{
    private byte[]? _pooled;
    private void* _native;
    private FileMap? _map;
    private PublishedArchiveHandle? _archive;
    private int _leaseState;
    private int _length;
    private NativeMemoryPressureLease _pressure;
    private ECookedPayloadStorage _storage;
    private int _disposed;
    private readonly object _lifetimeGate = new();
    private int _pins;

    private CookedPayloadOwner(byte[]? pooled, void* native, FileMap? map, int length, NativeMemoryPressureLease pressure, ECookedPayloadStorage storage)
    {
        _pooled = pooled;
        _native = native;
        _map = map;
        _length = length;
        _pressure = pressure;
        _storage = storage;
    }

    internal static CookedPayloadOwner FromPooled(byte[] buffer, int length)
        => new(buffer, null, null, length, default, ECookedPayloadStorage.Pooled);

    internal static CookedPayloadOwner FromNative(void* pointer, int length, NativeMemoryPressureLease pressure)
        => new(null, pointer, null, length, pressure, ECookedPayloadStorage.Native);

    internal static CookedPayloadOwner FromArchive(PublishedArchiveHandle archive, void* pointer, int length)
        => new(null, pointer, null, length, default, ECookedPayloadStorage.Mapped) { _archive = archive };

    internal bool IsLeaseActive => Volatile.Read(ref _leaseState) == 0 && !IsDisposed;
    internal Span<byte> LeaseSpan => IsLeaseActive ? GetSpan()
        : throw new ObjectDisposedException(nameof(CookedPayloadLease));
    internal CookedPayloadOwner TransferLease()
    {
        if (Interlocked.CompareExchange(ref _leaseState, 1, 0) != 0 || IsDisposed)
            throw new ObjectDisposedException(nameof(CookedPayloadLease));
        return this;
    }
    internal void DisposeLease()
    {
        if (Interlocked.CompareExchange(ref _leaseState, 2, 0) == 0)
            Dispose(true);
    }

    /// <summary>Copies bytes into owned storage. Used when the source is a mapping the lease does not own.</summary>
    internal static CookedPayloadOwner Copy(ReadOnlySpan<byte> source)
    {
        CookedPayloadLease lease = CookedPayloadBufferPool.Rent(source.Length);
        try
        {
            source.CopyTo(lease.WritableSpan);
            return lease.TransferToOwner();
        }
        finally { lease.Dispose(); }
    }

    /// <summary>
    /// Maps an entire file read-only and returns an owner over the mapping. No managed array is
    /// allocated, so large source or cache files never reach the large object heap. The file must
    /// exist and be smaller than 2 GiB.
    /// </summary>
    public static CookedPayloadOwner MapFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        RuntimeAssetReadServices.EnsureHostFileAccess("Cooked payload file mapping");
        FileInfo info = new(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException($"File '{fullPath}' not found.", fullPath);
        if (info.Length > int.MaxValue)
            throw new IOException($"File '{fullPath}' is {info.Length} bytes, which exceeds the supported single-mapping size.");
        if (info.Length == 0)
            return new CookedPayloadOwner([], null, null, 0, default, ECookedPayloadStorage.Pooled);

        FileMap map = FileMap.FromFile(fullPath, FileMapProtect.Read);
        try
        {
            return new CookedPayloadOwner(null, (byte*)map.Address, map, checked((int)map.Length), default, ECookedPayloadStorage.Mapped);
        }
        catch { map.Dispose(); throw; }
    }

    public int Length => _length;
    public ECookedPayloadStorage Storage => _storage;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Read-only view of the payload bytes.</summary>
    public ReadOnlySpan<byte> Span => GetSpan();

    /// <summary>Read-only memory over the payload bytes for consumers that cannot take a span.</summary>
    public ReadOnlyMemory<byte> ReadOnlyMemory => Memory;

    public override Span<byte> GetSpan()
    {
        ThrowIfDisposed();
        return _storage is ECookedPayloadStorage.Native or ECookedPayloadStorage.Mapped
            ? new Span<byte>(_native, _length)
            : new Span<byte>(_pooled, 0, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        lock (_lifetimeGate)
        {
            ThrowIfDisposed();
            if ((uint)elementIndex > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            if (_storage is ECookedPayloadStorage.Native or ECookedPayloadStorage.Mapped)
            {
                _pins++;
                return new MemoryHandle((byte*)_native + elementIndex, default, this);
            }
            GCHandle handle = GCHandle.Alloc(_pooled, GCHandleType.Pinned);
            _pins++;
            return new MemoryHandle((byte*)handle.AddrOfPinnedObject() + elementIndex, handle, this);
        }
    }

    public override void Unpin()
    {
        lock (_lifetimeGate)
        {
            if (_pins <= 0) throw new InvalidOperationException("Payload memory was unpinned without a matching pin.");
            if (--_pins == 0 && IsDisposed) ReleaseStorage();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_lifetimeGate)
            if (_pins == 0) ReleaseStorage();
    }

    private void ReleaseStorage()
    {
        switch (_storage)
        {
            case ECookedPayloadStorage.Pooled:
                if (_pooled is { Length: > 0 } pooled)
                    CookedPayloadBufferPool.ReturnPooled(pooled);
                break;
            case ECookedPayloadStorage.Native:
                CookedPayloadBufferPool.ReleaseNative(_native, _length, _pressure);
                break;
            case ECookedPayloadStorage.Mapped:
                _map?.Dispose();
                _archive?.ReleasePayloadReference();
                break;
        }

        _pooled = null;
        _native = null;
        _map = null;
        _archive = null;
        _length = 0;
        _pressure = default;
        _storage = ECookedPayloadStorage.None;
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(CookedPayloadOwner), "The cooked payload owner was disposed; its bytes are no longer valid.");
    }
}
