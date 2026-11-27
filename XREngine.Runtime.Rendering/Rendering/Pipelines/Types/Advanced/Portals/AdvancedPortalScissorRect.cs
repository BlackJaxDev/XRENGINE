namespace XREngine.Rendering;

/// <summary>
/// Clamped integer framebuffer scissor rectangle covering a scheduled portal surface.
/// </summary>
public readonly record struct AdvancedPortalScissorRect(int X, int Y, int Width, int Height)
{
    /// <summary>True when both dimensions are strictly positive.</summary>
    public bool IsNonEmpty => Width > 0 && Height > 0;

    /// <summary>Covered area in pixels.</summary>
    public long Area => Width > 0 && Height > 0 ? (long)Width * Height : 0L;

    /// <summary>Returns the intersection of this rectangle with <paramref name="other"/>.</summary>
    public AdvancedPortalScissorRect Intersect(in AdvancedPortalScissorRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(X + Width, other.X + other.Width);
        int bottom = Math.Min(Y + Height, other.Y + other.Height);
        int width = Math.Max(0, right - left);
        int height = Math.Max(0, bottom - top);
        return new AdvancedPortalScissorRect(left, top, width, height);
    }
}
