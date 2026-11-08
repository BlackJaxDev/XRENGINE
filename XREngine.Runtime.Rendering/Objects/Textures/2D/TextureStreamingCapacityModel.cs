namespace XREngine.Rendering;

/// <summary>
/// Pure functions that turn the measured CPU capacity of the machine into texture streaming worker limits.
/// The values come from the processor count and the job system, not from one test machine.
/// </summary>
internal static class TextureStreamingCapacityModel
{
    /// <summary>
    /// The threads that the frame needs for itself: the render thread, the update thread, the fixed-update thread, the
    /// collect-visible and swap thread (the four foreground threads of the engine execution topology), and one thread for
    /// audio mixing and the operating system.
    /// </summary>
    internal const int ReservedFrameThreads = 5;

    /// <summary>
    /// The highest automatic decode concurrency. A decode of a 2048-texel RGBA source holds the decoded image and its mip
    /// chain (about 16 MB + 22 MB), so 8 decodes bound the transient memory to about 300 MB, and the decode queue of one
    /// shot is rarely deeper than this.
    /// </summary>
    internal const int MaxAutomaticDecodeConcurrency = 8;

    /// <summary>The highest decode concurrency that an explicit override can select.</summary>
    internal const int MaxDecodeConcurrencyOverride = 32;

    /// <summary>
    /// Resolves how many texture decodes can run at the same time.
    /// </summary>
    /// <param name="effectiveProcessorCount">The logical processors that the process can use now (its affinity).</param>
    /// <param name="jobWorkerCount">The general job worker count. Zero when it is not known.</param>
    /// <param name="overrideValue">An explicit value that pins the result, or null for the automatic value.</param>
    /// <returns>
    /// The processor count less the frame threads (never less than one processor for the frame), not more than the job
    /// worker count, clamped to 1..<see cref="MaxAutomaticDecodeConcurrency"/>.
    /// </returns>
    public static int ResolveDecodeConcurrency(int effectiveProcessorCount, int jobWorkerCount, int? overrideValue)
    {
        if (overrideValue is int pinned)
            return Math.Clamp(pinned, 1, MaxDecodeConcurrencyOverride);

        int processors = Math.Max(1, effectiveProcessorCount);
        int reserved = Math.Min(ReservedFrameThreads, processors - 1);
        int available = processors - reserved;
        if (jobWorkerCount > 0)
            available = Math.Min(available, jobWorkerCount);

        return Math.Clamp(available, 1, MaxAutomaticDecodeConcurrency);
    }

    /// <summary>
    /// Resolves how many upload preparation workers (staging copies) can run at the same time.
    /// </summary>
    /// <param name="decodeConcurrency">The decode concurrency from <see cref="ResolveDecodeConcurrency"/>.</param>
    /// <param name="stagingBufferCapacity">
    /// The number of staging buffers for background uploads. Each worker holds one buffer until its chunk completes on the
    /// GPU, so more workers than buffers cannot run.
    /// </param>
    public static int ResolveUploadPreparationWorkers(int decodeConcurrency, int stagingBufferCapacity)
        => Math.Clamp(decodeConcurrency, 1, Math.Max(1, stagingBufferCapacity));
}
