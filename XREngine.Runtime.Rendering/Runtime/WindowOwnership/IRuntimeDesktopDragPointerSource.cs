namespace XREngine.Rendering;

/// <summary>Supplies optional native drag evidence from one desktop window.</summary>
public interface IRuntimeDesktopDragPointerSource
{
    RuntimeDesktopDragPointerSnapshot ReadDesktopDragPointer();
}
