namespace XREngine.Cutscenes;

/// <summary>
/// A point in time or a duration on a cutscene timeline, stored as a whole number of flicks.
/// One flick is 1/705,600,000 of a second. Common frame rates and audio sample rates
/// divide one second into whole flicks, so conversions are exact.
/// </summary>
/// <param name="Flicks">The time in flicks.</param>
public readonly record struct CutsceneTime(long Flicks) : IComparable<CutsceneTime>
{
    /// <summary>The number of flicks in one second.</summary>
    public const long FlicksPerSecond = 705_600_000;

    /// <summary>The time zero.</summary>
    public static CutsceneTime Zero => default;

    /// <summary>Creates a time from seconds. The result is rounded to the nearest flick.</summary>
    /// <param name="seconds">The time in seconds.</param>
    public static CutsceneTime FromSeconds(double seconds)
        => new((long)Math.Round(seconds * FlicksPerSecond));

    /// <summary>Creates a time from a frame index at a frame rate.</summary>
    /// <param name="frame">The frame index.</param>
    /// <param name="fps">The frame rate in frames per second. It must be greater than zero.</param>
    public static CutsceneTime FromFrames(long frame, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        return new((long)((Int128)frame * FlicksPerSecond / fps));
    }

    /// <summary>Creates a time from an audio sample index at a sample rate.</summary>
    /// <param name="sample">The sample index.</param>
    /// <param name="rate">The sample rate in samples per second. It must be greater than zero.</param>
    public static CutsceneTime FromSamples(long sample, int rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return new((long)((Int128)sample * FlicksPerSecond / rate));
    }

    /// <summary>Converts this time to seconds.</summary>
    public double ToSeconds()
        => (double)Flicks / FlicksPerSecond;

    /// <summary>Converts this time to an audio sample index. The result is rounded toward zero.</summary>
    /// <param name="rate">The sample rate in samples per second. It must be greater than zero.</param>
    public long ToSamples(int rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return (long)((Int128)Flicks * rate / FlicksPerSecond);
    }

    /// <summary>Limits this time to an inclusive range.</summary>
    /// <param name="min">The lowest allowed time.</param>
    /// <param name="max">The highest allowed time.</param>
    public CutsceneTime Clamp(CutsceneTime min, CutsceneTime max)
    {
        if (Flicks < min.Flicks)
            return min;
        if (Flicks > max.Flicks)
            return max;
        return this;
    }

    /// <inheritdoc/>
    public int CompareTo(CutsceneTime other)
        => Flicks.CompareTo(other.Flicks);

    /// <summary>Adds two times.</summary>
    public static CutsceneTime operator +(CutsceneTime left, CutsceneTime right)
        => new(left.Flicks + right.Flicks);

    /// <summary>Subtracts one time from another.</summary>
    public static CutsceneTime operator -(CutsceneTime left, CutsceneTime right)
        => new(left.Flicks - right.Flicks);

    /// <summary>Tests if the left time is less than the right time.</summary>
    public static bool operator <(CutsceneTime left, CutsceneTime right)
        => left.Flicks < right.Flicks;

    /// <summary>Tests if the left time is greater than the right time.</summary>
    public static bool operator >(CutsceneTime left, CutsceneTime right)
        => left.Flicks > right.Flicks;

    /// <summary>Tests if the left time is less than or equal to the right time.</summary>
    public static bool operator <=(CutsceneTime left, CutsceneTime right)
        => left.Flicks <= right.Flicks;

    /// <summary>Tests if the left time is greater than or equal to the right time.</summary>
    public static bool operator >=(CutsceneTime left, CutsceneTime right)
        => left.Flicks >= right.Flicks;
}
