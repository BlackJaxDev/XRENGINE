namespace XREngine.Editor;

/// <summary>
/// A drop target of the dock drop overlay. <see cref="Center"/> adds the dragged panel to the stack as a tab. The
/// sides split the stack and put the panel on that side.
/// </summary>
internal enum ENativeDockDropZone
{
    Center,
    Left,
    Right,
    Top,
    Bottom,
}
