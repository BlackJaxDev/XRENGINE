using System.Buffers;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit single-owner lease for readback bytes that cross into asynchronous CPU
/// processing. The pooled array is private and is returned exactly once.
/// </summary>
internal sealed class VulkanPooledReadbackBytes : IDisposable
{
    private byte[]? _buffer;

    internal VulkanPooledReadbackBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Length = length;
        _buffer = ArrayPool<byte>.Shared.Rent(length);
    }

    internal int Length { get; }

    internal Span<byte> WritableBytes
        => (_buffer ?? throw new ObjectDisposedException(nameof(VulkanPooledReadbackBytes))).AsSpan(0, Length);

    internal ReadOnlySpan<byte> Bytes
        => (_buffer ?? throw new ObjectDisposedException(nameof(VulkanPooledReadbackBytes))).AsSpan(0, Length);

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
