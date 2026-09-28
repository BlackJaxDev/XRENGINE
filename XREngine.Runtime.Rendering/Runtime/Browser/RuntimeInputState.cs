namespace XREngine.Rendering;

/// <summary>Input sampled by the host independently of the presentation target.</summary>
public readonly record struct RuntimeInputState(
    float PointerX,
    float PointerY,
    bool IsPrimaryPointerDown,
    float DirectionX,
    float DirectionY)
{
    /// <summary>Rejects non-finite input values; pointer coordinates may leave the surface during a drag.</summary>
    public void Validate()
    {
        if (!float.IsFinite(PointerX) || !float.IsFinite(PointerY) ||
            !float.IsFinite(DirectionX) || !float.IsFinite(DirectionY))
            throw new ArgumentOutOfRangeException(nameof(PointerX), "Input coordinates and axes must be finite.");
    }
}
