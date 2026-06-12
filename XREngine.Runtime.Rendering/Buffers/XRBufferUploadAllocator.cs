using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Rendering;

public sealed class XRBufferUploadAllocation : IDisposable
{
    private DataSource? _data;
    private XRBufferCpuUploadAllocator? _owner;
    private bool _disposed;

    public static XRBufferUploadAllocation Empty { get; } = new();

    private XRBufferUploadAllocation()
    {
    }

    internal XRBufferUploadAllocation(
        XRBufferResolvedRoute route,
        uint byteCount,
        DataSource data,
        bool reused,
        XRBufferCpuUploadAllocator? owner)
    {
        Route = route;
        ByteCount = byteCount;
        _data = data;
        Reused = reused;
        _owner = owner;
    }

    public XRBufferResolvedRoute Route { get; }
    public uint ByteCount { get; }
    public bool Reused { get; }
    public bool IsEmpty => _data is null;
    public DataSource Data => _data ?? throw new ObjectDisposedException(nameof(XRBufferUploadAllocation));
    public VoidPtr Address => Data.Address;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DataSource? data = _data;
        XRBufferCpuUploadAllocator? owner = _owner;
        _data = null;
        _owner = null;
        if (data is null)
            return;

        if (owner is not null)
            owner.Release(data);
        else
            data.Dispose();
    }
}

public interface IXRBufferUploadAllocator
{
    bool TryAcquire(
        uint byteCount,
        XRBufferMemoryPolicy policy,
        XRBufferResolvedRoute preferredRoute,
        out XRBufferUploadAllocation allocation,
        out string? reason);
}

/// <summary>
/// Backend-neutral CPU upload allocator used when a backend-specific allocator is not available.
/// Native backends should prefer their own staging/persistent allocation implementations.
/// </summary>
public sealed class XRBufferCpuUploadAllocator : IXRBufferUploadAllocator
{
    public static XRBufferCpuUploadAllocator Shared { get; } = new();

    private readonly Stack<DataSource> _pool = [];
    private readonly object _sync = new();

    public bool TryAcquire(
        uint byteCount,
        XRBufferMemoryPolicy policy,
        XRBufferResolvedRoute preferredRoute,
        out XRBufferUploadAllocation allocation,
        out string? reason)
    {
        allocation = XRBufferUploadAllocation.Empty;
        reason = null;

        if (byteCount == 0u)
        {
            reason = "zero-byte-upload";
            return false;
        }

        DataSource? data = null;
        lock (_sync)
        {
            while (_pool.Count > 0)
            {
                DataSource candidate = _pool.Pop();
                if (candidate.Length >= byteCount)
                {
                    data = candidate;
                    break;
                }

                candidate.Dispose();
            }
        }

        bool reused = data is not null;
        data ??= DataSource.Allocate(byteCount);
        allocation = new XRBufferUploadAllocation(preferredRoute, byteCount, data, reused, this);
        XRBufferWriteTelemetry.RecordStagingAllocation(reused);
        return true;
    }

    internal void Release(DataSource data)
    {
        if (data.Length == 0)
            return;

        lock (_sync)
            _pool.Push(data);
    }

    public void Clear()
    {
        lock (_sync)
        {
            while (_pool.Count > 0)
                _pool.Pop().Dispose();
        }
    }
}
