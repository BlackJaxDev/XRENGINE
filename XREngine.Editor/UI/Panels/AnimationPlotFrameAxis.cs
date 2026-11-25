namespace XREngine.Editor;

/// <summary>
/// Maps pointer positions on the animation plot time axis to clip times. The axis strip is
/// the band under the plot lanes that holds the time axis line and its ticks. A time from
/// the strip snaps to the nearest whole frame of the clip sample rate.
/// </summary>
internal static class AnimationPlotFrameAxis
{
    /// <summary>Height of the band at the bottom of the plot that accepts axis input.</summary>
    public const float StripHeight = 30;

    /// <summary>Returns true when a plot-local point is in the axis strip of a plot.</summary>
    public static bool IsOnAxis(float plotWidth, float plotHeight, float x, float y)
        => float.IsFinite(x) && float.IsFinite(y) &&
            float.IsFinite(plotWidth) && float.IsFinite(plotHeight) &&
            plotHeight > StripHeight &&
            x >= AnimationPlotWheelZoom.HorizontalInset &&
            x <= plotWidth - AnimationPlotWheelZoom.HorizontalInset &&
            y >= plotHeight - StripHeight && y <= plotHeight;

    /// <summary>
    /// Snaps <paramref name="second"/> to the nearest frame and clamps it to the clip.
    /// With no positive sample rate, only the clamp applies.
    /// </summary>
    public static float SnapToFrame(float second, int sampleRate, float clipLength)
    {
        if (!float.IsFinite(second) || !float.IsFinite(clipLength) || clipLength <= 0)
            return 0;
        float clamped = Math.Clamp(second, 0, clipLength);
        if (sampleRate <= 0)
            return clamped;
        float frame = MathF.Round(clamped * sampleRate, MidpointRounding.AwayFromZero);
        // The last whole frame can end before the clip; do not snap past the clip end.
        return MathF.Min(frame / sampleRate, clipLength);
    }
}
