using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Scoped native access to a validated mapped-memory slice.</summary>
internal unsafe ref struct VulkanMappedMemoryLease
{
    private readonly VulkanBufferResourceService _owner;
    private readonly VulkanBackendObjectContext _context;
    private readonly VulkanMappedMemorySlice _slice;
    private void* _pointer;
    private readonly bool _write;

    internal VulkanMappedMemoryLease(VulkanBufferResourceService owner, VulkanBackendObjectContext context, scoped in VulkanMappedMemorySlice slice, void* pointer, bool write)
    {
        _owner = owner;
        _context = context;
        _slice = slice;
        _pointer = pointer;
        _write = write;
    }

    public readonly nuint Length => checked((nuint)_slice.Length);
    public readonly Span<byte> Bytes => new(_pointer, checked((int)_slice.Length));
    public void Dispose()
    {
        if (_pointer is null)
            return;
        if (_write)
            _owner.Flush(_context, in _slice);
        _owner.Release(_context, in _slice);
        _pointer = null;
    }
}
