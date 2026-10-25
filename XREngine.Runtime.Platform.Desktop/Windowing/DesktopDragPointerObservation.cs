namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Identifies the native press state before a query that can run callbacks.</summary>
internal readonly record struct DesktopDragPointerObservation(
    nint SourceWindowHandle, ulong PressSequence, ulong Sequence);
