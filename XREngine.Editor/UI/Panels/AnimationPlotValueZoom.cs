namespace XREngine.Editor;

/// <summary>
/// Computes a pointer-anchored zoom of the animation curve graph value axis. The view is a window
/// of the auto-fit value range, given as a zoom and a window center in range fractions, so it stays
/// meaningful when the curve values change. The value under the pointer keeps its vertical position
/// unless the window reaches an edge of the auto-fit range.
/// </summary>
internal static class AnimationPlotValueZoom
{
    public const float MinimumZoom = 1;
    public const float MaximumZoom = 32;
    /// <summary>Plot margin below the lowest value line.</summary>
    public const float BottomInset = 12;
    /// <summary>Total vertical margin outside the value lines, which includes the time axis.</summary>
    public const float VerticalInsets = 36;
    public const float DefaultCenter = 0.5f;

    /// <summary>Gets the low end of the visible window as a fraction of the auto-fit range.</summary>
    public static float WindowStart(float zoom, float center) => center - 0.5f / zoom;

    /// <summary>
    /// Returns false for invalid input. Otherwise returns true and the next zoom and window center.
    /// At a zoom limit, the outputs equal the current view.
    /// </summary>
    public static bool TryZoom(float currentZoom, float currentCenter, float plotHeight,
        float pointerY, float wheelDelta, out float zoom, out float center)
    {
        zoom = currentZoom;
        center = currentCenter;
        if (!float.IsFinite(wheelDelta) || wheelDelta == 0 || !float.IsFinite(pointerY) ||
            !float.IsFinite(currentZoom) || currentZoom < MinimumZoom ||
            !float.IsFinite(currentCenter) || !float.IsFinite(plotHeight) ||
            plotHeight <= VerticalInsets)
            return false;
        float fraction = Math.Clamp((pointerY - BottomInset) / (plotHeight - VerticalInsets), 0, 1);
        float anchor = WindowStart(currentZoom, currentCenter) + fraction / currentZoom;
        float next = Math.Clamp(currentZoom * MathF.Pow(AnimationPlotWheelZoom.StepFactor, wheelDelta),
            MinimumZoom, MaximumZoom);
        if (!float.IsFinite(next) || MathF.Abs(next - currentZoom) < 0.0001f)
            return true;
        float start = Math.Clamp(anchor - fraction / next, 0, 1 - 1 / next);
        zoom = next;
        center = start + 0.5f / next;
        return true;
    }
}
