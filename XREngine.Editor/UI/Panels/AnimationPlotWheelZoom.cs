namespace XREngine.Editor;

/// <summary>
/// Computes a pointer-anchored zoom of the animation plot time axis. The time under the
/// pointer keeps its horizontal position unless the window reaches a clip edge.
/// </summary>
internal static class AnimationPlotWheelZoom
{
    /// <summary>Zoom factor for one wheel step.</summary>
    public const float StepFactor = 1.25f;
    public const float MinimumZoom = 1;
    public const float MaximumZoom = 32;
    /// <summary>Horizontal plot margin that <see cref="AnimationPlotPointerSnapshot.TimeForX"/> uses.</summary>
    public const float HorizontalInset = 8;

    /// <summary>
    /// Returns false for invalid input. Otherwise returns true and the next zoom and window
    /// start. At a zoom limit, the outputs equal the current view.
    /// </summary>
    public static bool TryZoom(float currentZoom, float windowStart, float windowDuration,
        float clipLength, float plotWidth, float pointerX, float wheelDelta,
        out float zoom, out float start)
    {
        zoom = currentZoom;
        start = windowStart;
        if (!float.IsFinite(wheelDelta) || wheelDelta == 0 || !float.IsFinite(pointerX) ||
            !float.IsFinite(currentZoom) || currentZoom < MinimumZoom ||
            !float.IsFinite(windowStart) || !float.IsFinite(windowDuration) ||
            !float.IsFinite(clipLength) || clipLength <= 0 || !float.IsFinite(plotWidth))
            return false;
        float fraction = Math.Clamp((pointerX - HorizontalInset) /
            MathF.Max(1, plotWidth - 2 * HorizontalInset), 0, 1);
        float anchor = windowStart + fraction * windowDuration;
        float next = Math.Clamp(currentZoom * MathF.Pow(StepFactor, wheelDelta),
            MinimumZoom, MaximumZoom);
        if (!float.IsFinite(next) || MathF.Abs(next - currentZoom) < 0.0001f)
            return true;
        float duration = clipLength / next;
        zoom = next;
        start = Math.Clamp(anchor - fraction * duration, 0, MathF.Max(0, clipLength - duration));
        return true;
    }
}
