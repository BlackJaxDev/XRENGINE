namespace XREngine.Rendering;

/// <summary>
/// Pure functions that turn the CPU capacity of the machine and the engine execution topology into texture streaming
/// worker limits. The values come from the processor count and the topology, not from one test machine.
/// </summary>
internal static class TextureStreamingCapacityModel
{
    /// <summary>
    /// The lowest decode width without frame-time evidence. It is the old fixed width; the decode width controller goes
    /// below it only when frames with active decodes go over the frame-time rule more often than frames without decodes.
    /// </summary>
    internal const int MinimumDecodeConcurrency = 2;

    /// <summary>
    /// The highest decode width. A decode of a 2048-texel RGBA source holds the decoded image and its mip chain (about
    /// 16 MB + 22 MB), so 8 decodes bound the transient memory to about 300 MB, and the decode queue of one shot is rarely
    /// deeper than this.
    /// </summary>
    internal const int MaxAutomaticDecodeConcurrency = 8;

    /// <summary>The highest decode width that an explicit override can select.</summary>
    internal const int MaxDecodeConcurrencyOverride = 32;

    /// <summary>
    /// The foreground threads that the engine reserves when no execution topology is installed (render, update, fixed
    /// update, collect-visible and swap), as in <c>EngineExecutionTopology.DefaultForegroundReservation</c>.
    /// </summary>
    internal const int FallbackForegroundThreads = 4;

    /// <summary>
    /// Resolves the start width of the decode gate.
    /// </summary>
    /// <param name="effectiveProcessorCount">The logical processors that the process can use now (its affinity).</param>
    /// <param name="foregroundThreads">The foreground threads that the execution topology reserves.</param>
    /// <param name="dedicatedThreads">The dedicated background threads that the execution topology reserves.</param>
    /// <param name="overrideValue">An explicit value that pins the result, or null for the automatic value.</param>
    /// <returns>
    /// The processors that the topology leaves free, clamped to
    /// <see cref="MinimumDecodeConcurrency"/>..<see cref="MaxAutomaticDecodeConcurrency"/>. The decode width controller then
    /// raises it while decodes wait and the frames allow it, and lowers it on frame-time evidence.
    /// </returns>
    public static int ResolveBaseDecodeWidth(int effectiveProcessorCount, int foregroundThreads, int dedicatedThreads, int? overrideValue)
    {
        if (overrideValue is int pinned)
            return Math.Clamp(pinned, 1, MaxDecodeConcurrencyOverride);

        int free = Math.Max(1, effectiveProcessorCount) - Math.Max(0, foregroundThreads) - Math.Max(0, dedicatedThreads);
        return Math.Clamp(free, MinimumDecodeConcurrency, MaxAutomaticDecodeConcurrency);
    }

    /// <summary>
    /// Resolves the highest automatic decode width: the processors that the foreground threads leave free, clamped to
    /// <see cref="MinimumDecodeConcurrency"/>..<see cref="MaxAutomaticDecodeConcurrency"/>. The feedback never raises the
    /// width onto the processors of the render, update, fixed-update, and collect threads.
    /// </summary>
    public static int ResolveMaxDecodeWidth(int effectiveProcessorCount, int foregroundThreads)
        => Math.Clamp(Math.Max(1, effectiveProcessorCount) - Math.Max(0, foregroundThreads), MinimumDecodeConcurrency, MaxAutomaticDecodeConcurrency);

    /// <summary>
    /// Resolves how many upload preparation workers (staging copies) can run at the same time.
    /// </summary>
    /// <param name="decodeConcurrency">The current decode width.</param>
    /// <param name="stagingBufferCapacity">
    /// The number of staging buffers for background uploads. Each worker holds one buffer until its chunk completes on the
    /// GPU, so more workers than buffers cannot run.
    /// </param>
    public static int ResolveUploadPreparationWorkers(int decodeConcurrency, int stagingBufferCapacity)
        => Math.Clamp(decodeConcurrency, 1, Math.Max(1, stagingBufferCapacity));
}
