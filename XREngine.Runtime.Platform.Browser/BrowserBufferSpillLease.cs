using System.IO.MemoryMappedFiles;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Browser;

internal sealed class BrowserBufferSpillLease(IntPtr address, uint length, MemoryMappedFile mapping, MemoryMappedViewAccessor view) : IXRBufferSpillLease
{
    private MemoryMappedFile? _mapping = mapping;
    private MemoryMappedViewAccessor? _view = view;

    public IntPtr Address { get; } = address;
    public uint Length { get; } = length;

    public void Release(bool disposing)
    {
        MemoryMappedViewAccessor? heldView = Interlocked.Exchange(ref _view, null);
        MemoryMappedFile? heldMapping = Interlocked.Exchange(ref _mapping, null);
        if (heldView is not null)
        {
            // SafeHandles finalize after DataSource. Release its pointer on both paths.
            heldView.SafeMemoryMappedViewHandle.ReleasePointer();
            if (disposing)
                heldView.Dispose();
        }
        if (disposing)
            heldMapping?.Dispose();
    }
}
