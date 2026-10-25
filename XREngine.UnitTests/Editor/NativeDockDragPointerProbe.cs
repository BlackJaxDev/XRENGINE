using XREngine.Rendering;

namespace XREngine.UnitTests.Editor;

/// <summary>Supplies one native pointer receipt to the editor drag route.</summary>
internal sealed class NativeDockDragPointerProbe : IRuntimeDesktopDragPointerSource
{
    internal RuntimeDesktopDragPointerSnapshot Snapshot { get; set; }

    public RuntimeDesktopDragPointerSnapshot ReadDesktopDragPointer() => Snapshot;
}
