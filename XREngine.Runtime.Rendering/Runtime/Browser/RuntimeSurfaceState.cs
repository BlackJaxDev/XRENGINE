namespace XREngine.Rendering;

/// <summary>A host-owned snapshot of a live rendering surface's size and lifecycle.</summary>
public readonly record struct RuntimeSurfaceState(
    double LogicalWidth,
    double LogicalHeight,
    int PhysicalWidth,
    int PhysicalHeight,
    double PixelRatio,
    int Generation,
    bool IsVisible,
    bool IsFocused,
    bool IsAttached)
{
    /// <summary>Whether the surface currently has a drawable extent.</summary>
    public bool CanRender => IsAttached && IsVisible &&
        PhysicalWidth > 0 && PhysicalHeight > 0 &&
        LogicalWidth > 0 && LogicalHeight > 0;

    /// <summary>Rejects invalid dimensions and lifecycle generations.</summary>
    public void Validate()
    {
        if (!double.IsFinite(LogicalWidth) || LogicalWidth < 0 ||
            !double.IsFinite(LogicalHeight) || LogicalHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(LogicalWidth), "Logical dimensions must be finite and non-negative.");
        if (PhysicalWidth < 0 || PhysicalHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(PhysicalWidth), "Physical dimensions must be non-negative.");
        if (!double.IsFinite(PixelRatio) || PixelRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(PixelRatio), "Pixel ratio must be finite and positive.");
        if (Generation < 0)
            throw new ArgumentOutOfRangeException(nameof(Generation), "Surface generation must be non-negative.");
    }
}
