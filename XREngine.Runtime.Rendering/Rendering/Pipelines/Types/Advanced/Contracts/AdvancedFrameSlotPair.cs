namespace XREngine.Rendering;

/// <summary>
/// Current writable and previous read-only frame-slot indices for one frame.
/// </summary>
public readonly record struct AdvancedFrameSlotPair(
    uint Current,
    uint Previous);
