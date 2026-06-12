using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Rendering;

public enum XRBufferReadbackTicketStatus
{
    Pending,
    Complete,
    Rejected,
    Cancelled,
    Faulted,
}

/// <summary>
/// Explicit CPU readback request for GPU-written data. The ticket never exposes memory until it is complete.
/// </summary>
public sealed class XRBufferReadbackTicket : IDisposable
{
    private DataSource? _ownedData;
    private readonly XRDataBuffer _buffer;
    private readonly uint _byteOffset;
    private readonly uint _byteCount;
    private readonly ulong _requestedRevision;
    private string? _message;
    private bool _disposed;

    internal XRBufferReadbackTicket(
        XRDataBuffer buffer,
        uint byteOffset,
        uint byteCount,
        ulong requestedRevision,
        XRBufferReadbackTicketStatus status,
        string? message = null)
    {
        _buffer = buffer;
        _byteOffset = byteOffset;
        _byteCount = byteCount;
        _requestedRevision = requestedRevision;
        Status = status;
        _message = message;
    }

    public XRDataBuffer Buffer => _buffer;
    public uint ByteOffset => _byteOffset;
    public uint ByteCount => _byteCount;
    public ulong RequestedRevision => _requestedRevision;
    public XRBufferReadbackTicketStatus Status { get; private set; }
    public string? Message => _message;
    public bool IsComplete => Status == XRBufferReadbackTicketStatus.Complete;

    public bool TryGetSpan<T>(out ReadOnlySpan<T> data) where T : unmanaged
    {
        data = default;
        if (_disposed || Status != XRBufferReadbackTicketStatus.Complete || _ownedData is null)
            return false;

        uint byteLength = checked((uint)(_ownedData.Length / (uint)Marshal.SizeOf<T>() * (uint)Marshal.SizeOf<T>()));
        if (byteLength == 0)
        {
            data = ReadOnlySpan<T>.Empty;
            return true;
        }

        unsafe
        {
            data = new ReadOnlySpan<T>(_ownedData.Address.Pointer, checked((int)(byteLength / (uint)sizeof(T))));
        }
        return true;
    }

    internal unsafe bool TryCompleteFromCpuMirror(string route)
    {
        if (_disposed || Status is XRBufferReadbackTicketStatus.Cancelled or XRBufferReadbackTicketStatus.Rejected)
            return false;

        if (!_buffer.TryGetAddress(out VoidPtr address) || _byteCount == 0)
        {
            _ownedData = DataSource.Allocate(0);
            Status = XRBufferReadbackTicketStatus.Complete;
            _message = route;
            return true;
        }

        DataSource? source = _buffer.ClientSideSource;
        if (source is null || _byteOffset > source.Length || _byteCount > source.Length - _byteOffset)
        {
            Status = XRBufferReadbackTicketStatus.Faulted;
            _message = $"Readback range {_byteOffset}+{_byteCount} exceeds CPU mirror length {source?.Length ?? 0}.";
            return false;
        }

        _ownedData?.Dispose();
        _ownedData = DataSource.Allocate(_byteCount);
        Memory.Move(_ownedData.Address, address + _byteOffset, _byteCount);
        Status = XRBufferReadbackTicketStatus.Complete;
        _message = route;
        XRBufferWriteTelemetry.RecordHostCachedReadback(_byteCount);
        return true;
    }

    public void Cancel()
    {
        if (_disposed || Status == XRBufferReadbackTicketStatus.Complete)
            return;

        Status = XRBufferReadbackTicketStatus.Cancelled;
        _message = "Cancelled by caller.";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _ownedData?.Dispose();
        _ownedData = null;
        _disposed = true;
    }
}
