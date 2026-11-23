using System.Globalization;
using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Controls the delayed sample copies of the Advanced visibility counters under submission
/// strategies that are not instrumented. On a sampled render frame, the Vulkan backend
/// records a copy of each view's 160-byte counter block into the producer command buffer,
/// after the late visibility pass. The CPU reads the copy only after the graphics timeline
/// shows that the submission is complete, so a sample adds no CPU wait and no extra
/// submission. The completed values go to <see cref="AdvancedGpuCounterReadbacks"/>.
/// </summary>
/// <remarks>
/// Instrumented strategies copy the counters every frame and ignore this setting. A sample
/// counts as a diagnostic readback and map in the render statistics, so keep sampling off
/// when you validate a strict zero-readback run.
/// </remarks>
public static class AdvancedVisibilityCounterSampling
{
    private static int _intervalFrames = ReadLaunchIntervalFrames();

    /// <summary>
    /// Gets or sets the number of render frames between two samples. Zero turns sampling
    /// off. One samples every frame. Negative values are stored as zero.
    /// </summary>
    public static int IntervalFrames
    {
        get => Volatile.Read(ref _intervalFrames);
        set => Volatile.Write(ref _intervalFrames, Math.Max(0, value));
    }

    /// <summary>Gets whether sampling is on.</summary>
    public static bool IsEnabled
        => IntervalFrames > 0;

    /// <summary>
    /// Returns true when the render frame with this identity must copy the counters.
    /// All views and outputs of one render frame get the same answer, so the stereo eyes
    /// and the desktop view of a frame are sampled together.
    /// </summary>
    public static bool ShouldSample(ulong renderFrameId)
    {
        int interval = IntervalFrames;
        return interval > 0 && renderFrameId % (ulong)interval == 0UL;
    }

    /// <summary>
    /// Reads the start interval from <see cref="XREngineEnvironmentVariables.AdvancedVisibilityCounterSampling"/>
    /// one time, so that the per-frame check does not read the process environment.
    /// </summary>
    private static int ReadLaunchIntervalFrames()
    {
        string? value = XREnvironment.GetValue(XREngineEnvironmentVariables.AdvancedVisibilityCounterSampling);
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames))
            return Math.Max(0, frames);

        Debug.RenderingWarning(
            "[AdvancedVisibility] Ignored {0}={1}. Use a frame interval: 0 turns sampling off, 1 samples every frame.",
            XREngineEnvironmentVariables.AdvancedVisibilityCounterSampling,
            value);
        return 0;
    }
}
