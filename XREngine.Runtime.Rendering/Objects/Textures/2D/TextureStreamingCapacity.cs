using System.Diagnostics;
using System.Globalization;
using System.Text;
using XREngine.Execution;

namespace XREngine.Rendering;

/// <summary>
/// Process-wide texture streaming capacity: the shared decode gate, whose width follows the engine execution topology and
/// frame-time feedback, and the work lanes, whose per-frame limits follow the measured upload cost and frame time.
/// </summary>
/// <remarks>
/// Only one renderer backend streams at a time, so the backends share one decode gate. The base width refreshes at most
/// one time in <see cref="CapacityRefreshInterval"/>, from the live process affinity, the topology reservation, and the
/// <see cref="RuntimeEngine.Rendering.EngineSettings.TextureStreamingDecodeConcurrency"/> override. The streaming evaluation
/// gives one frame sample for each render frame to the decode width controller.
/// </remarks>
internal static class TextureStreamingCapacity
{
    /// <summary>The hard upper bound of each adaptive per-frame lane limit.</summary>
    internal const int MaxItemsPerFrame = 16;

    /// <summary>
    /// The share of the measured idle headroom that a lane can use during normal play. Half of the idle time stays free
    /// for gameplay frames that are heavier than the average.
    /// </summary>
    internal const double DefaultHeadroomShare = 0.5;

    /// <summary>
    /// The share of the measured idle headroom that a lane can use while the <see cref="ETextureStreamingBudgetProfile.Cutscene"/>
    /// profile is active and a residency request is not resident yet (for example, at a cut hold).
    /// </summary>
    internal const double CutsceneHeadroomShare = 1.0;

    /// <summary>The number of GPU timestamp queries of the OpenGL apply timer (8 pairs).</summary>
    internal const int OpenGlTimestampQueryCount = 16;

    /// <summary>The shortest time between two base width refreshes.</summary>
    internal static readonly TimeSpan CapacityRefreshInterval = TimeSpan.FromSeconds(1.0);

    private static readonly object s_decodeSync = new();
    private static readonly long s_refreshIntervalTicks = (long)(CapacityRefreshInterval.TotalSeconds * Stopwatch.Frequency);
    private static int s_maxDecodeWidth = TextureStreamingCapacityModel.MaxAutomaticDecodeConcurrency;
    private static readonly TextureStreamingDecodeWidthController s_decodeWidth = CreateDecodeWidthController();
    private static readonly PriorityAsyncSemaphore s_decodeGate = new(s_decodeWidth.Width);
    private static readonly TextureStreamingFrameSampler s_decodeSampler = new();
    private static long s_lastRefreshTimestamp = Stopwatch.GetTimestamp();
    private static int s_effectiveProcessorCount = XREngine.Data.RuntimeProcessorServices.ReadEffectiveProcessorCount();
    private static long s_discontinuityEpoch;
    private static long s_decodeDiscontinuityEpoch;

    /// <summary>
    /// Resident data applies and sparse transitions on the OpenGL render thread. The old fixed limit was one in each frame
    /// for each OpenGL backend.
    /// </summary>
    public static TextureStreamingWorkLane OpenGlResidentApply { get; } = new("OpenGL.ResidentApply", initialItemsPerFrame: 1);

    /// <summary>Vulkan transfer chunks that complete, retire, and publish. The old fixed limit was four in each frame.</summary>
    public static TextureStreamingWorkLane VulkanCompletion { get; } = new("Vulkan.Completion", initialItemsPerFrame: 4);

    /// <summary>GPU timestamps around OpenGL applies and sparse transitions. Use it on the render thread only.</summary>
    public static TextureStreamingGpuTimer OpenGlGpuTimer { get; } =
        new(new RendererTextureStreamingTimestampSource(OpenGlTimestampQueryCount));

    /// <summary>
    /// The decode gate that the texture residency backends share. Each read refreshes the base width when the refresh
    /// interval has passed.
    /// </summary>
    public static PriorityAsyncSemaphore DecodeGate
    {
        get
        {
            RefreshDecodeCapacity();
            return s_decodeGate;
        }
    }

    /// <summary>The current decode gate width. It reads the cached value and never refreshes.</summary>
    public static int DecodeConcurrency => s_decodeGate.Capacity;

    /// <summary>The processor count of the last refresh (the live process affinity).</summary>
    public static int EffectiveProcessorCount => Volatile.Read(ref s_effectiveProcessorCount);

    /// <summary>A counter that grows with each scene discontinuity. The lanes compare it to start a relearn.</summary>
    public static long DiscontinuityEpoch => Volatile.Read(ref s_discontinuityEpoch);

    /// <summary>
    /// True while the <see cref="ETextureStreamingBudgetProfile.Cutscene"/> profile is active and at least one residency
    /// request is not resident. Then the lanes can use all of the measured idle headroom.
    /// </summary>
    public static bool IsCutsceneWaitingForResidency
    {
        get
        {
            ImportedTextureStreamingManager manager = ImportedTextureStreamingManager.Instance;
            return manager.BudgetProfile == ETextureStreamingBudgetProfile.Cutscene
                && manager.PendingResidencyRequestCount > 0;
        }
    }

    /// <summary>
    /// Signals a scene discontinuity, for example a cutscene cut: the scene cost changes, so the controllers relearn the
    /// normal frame time and do not count the new scene cost as streaming cost. Any thread can call it; it allocates nothing.
    /// </summary>
    public static void NotifySceneDiscontinuity()
        => Interlocked.Increment(ref s_discontinuityEpoch);

    /// <summary>
    /// The number of upload preparation workers for a staging pool of <paramref name="stagingBufferCapacity"/> buffers.
    /// It reads the cached decode width, so it is safe under other locks.
    /// </summary>
    public static int ResolveUploadPreparationWorkers(int stagingBufferCapacity)
        => TextureStreamingCapacityModel.ResolveUploadPreparationWorkers(s_decodeGate.Capacity, stagingBufferCapacity);

    /// <summary>Reads the completed OpenGL GPU timestamps into the apply lane, without a wait. Render thread only.</summary>
    public static void PollOpenGlGpuTimer()
    {
        TextureStreamingGpuTimer timer = OpenGlGpuTimer;
        while (timer.TryReadCompleted(out double millisecondsPerItem))
            OpenGlResidentApply.RecordGpuItemCost(millisecondsPerItem);
    }

    /// <summary>
    /// Starts the measurement of one OpenGL apply or sparse transition on the render thread: it reads completed GPU
    /// timestamps, writes the start timestamp, and starts the CPU timer. It allocates nothing.
    /// </summary>
    public static TextureStreamingApplyMeasurement BeginOpenGlApply()
    {
        PollOpenGlGpuTimer();
        int pair = OpenGlGpuTimer.Begin();
        return new TextureStreamingApplyMeasurement(TextureStreamingWorkTimer.Start(), pair);
    }

    /// <summary>Ends the measurement that <see cref="BeginOpenGlApply"/> started and records its CPU time.</summary>
    public static void EndOpenGlApply(in TextureStreamingApplyMeasurement measurement)
    {
        OpenGlGpuTimer.End(measurement.GpuPair, items: 1);
        OpenGlResidentApply.RecordWork(measurement.Timer);
    }

    /// <summary>
    /// Gives the current render frame to the decode width controller. The streaming evaluation calls it one time in each
    /// collect frame; a second call in the same render frame does nothing. It allocates nothing.
    /// </summary>
    public static void ObserveDecodeFrame()
    {
        lock (s_decodeSync)
        {
            long epoch = Volatile.Read(ref s_discontinuityEpoch);
            if (epoch != s_decodeDiscontinuityEpoch)
            {
                s_decodeDiscontinuityEpoch = epoch;
                s_decodeWidth.MarkDiscontinuity();
            }

            if (!s_decodeSampler.TryAdvance(out TextureStreamingClosedFrame closed))
                return;

            int width = s_decodeWidth.Update(new TextureStreamingDecodeSample(
                closed.FrameMilliseconds,
                TextureStreamingFrameSampler.ResolveTargetFrameMilliseconds(),
                s_decodeGate.InUse,
                s_decodeGate.WaitingCount));
            ApplyDecodeWidthNoLock(width);
        }
    }

    /// <summary>
    /// Refreshes the base decode width when the refresh interval has passed. It allocates nothing unless the width changes.
    /// </summary>
    public static void RefreshDecodeCapacity()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastRefreshTimestamp);
        if (now - last < s_refreshIntervalTicks)
            return;
        if (Interlocked.CompareExchange(ref s_lastRefreshTimestamp, now, last) != last)
            return;

        lock (s_decodeSync)
        {
            int baseWidth = ResolveBaseDecodeWidth(out int processors);
            Volatile.Write(ref s_effectiveProcessorCount, processors);
            s_decodeWidth.Configure(
                baseWidth,
                ResolveMaxDecodeWidth(),
                RuntimeEngine.Rendering.Settings.TextureStreamingDecodeConcurrency,
                RuntimeEngine.Rendering.Settings.TextureStreamingFrameToleranceMilliseconds);
            ApplyDecodeWidthNoLock(s_decodeWidth.Width);
        }
    }

    /// <summary>Writes the decode width and the state of each lane.</summary>
    public static void AppendDiagnosticSummary(StringBuilder builder)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        builder.Append("TextureStreamingDecode: width=").Append(s_decodeGate.Capacity.ToString(inv))
            .Append(" base=").Append(s_decodeWidth.BaseWidth.ToString(inv))
            .Append(" inUse=").Append(s_decodeGate.InUse.ToString(inv))
            .Append(" waiting=").Append(s_decodeGate.WaitingCount.ToString(inv))
            .Append(" backoffs=").Append(s_decodeWidth.BackoffCount.ToString(inv))
            .Append(" processors=").Append(Volatile.Read(ref s_effectiveProcessorCount).ToString(inv))
            .Append(" cutsceneWaiting=").Append(IsCutsceneWaitingForResidency ? '1' : '0')
            .Append(" glGpuPairsRead=").Append(OpenGlGpuTimer.ReadPairs.ToString(inv))
            .AppendLine();
        OpenGlResidentApply.AppendDiagnosticSummary(builder);
        VulkanCompletion.AppendDiagnosticSummary(builder);
    }

    private static void ApplyDecodeWidthNoLock(int width)
    {
        int previous = s_decodeGate.Capacity;
        if (previous == width)
            return;

        s_decodeGate.SetCapacity(width);
        Debug.Textures($"[TextureStreaming] Decode width changed from {previous} to {width} (base={s_decodeWidth.BaseWidth}, processors={Volatile.Read(ref s_effectiveProcessorCount)}, evidence={s_decodeWidth.HasRecentEvidence}).");
    }

    private static TextureStreamingDecodeWidthController CreateDecodeWidthController()
    {
        int baseWidth = ResolveBaseDecodeWidth(out _);
        return new TextureStreamingDecodeWidthController(
            baseWidth,
            ResolveMaxDecodeWidth(),
            RuntimeEngine.Rendering.Settings.TextureStreamingDecodeConcurrency,
            RuntimeEngine.Rendering.Settings.TextureStreamingFrameToleranceMilliseconds);
    }

    private static int ResolveMaxDecodeWidth()
        => RuntimeEngine.Rendering.Settings.TextureStreamingDecodeConcurrency is int pinned
            ? Math.Clamp(pinned, 1, TextureStreamingCapacityModel.MaxDecodeConcurrencyOverride)
            : Volatile.Read(ref s_maxDecodeWidth);

    private static int ResolveBaseDecodeWidth(out int processors)
    {
        processors = XREngine.Data.RuntimeProcessorServices.ReadEffectiveProcessorCount();
        int foreground = TextureStreamingCapacityModel.FallbackForegroundThreads;
        int dedicated = 0;
        if (RuntimeRenderingHostServices.TryGetWork(out IRuntimeRenderWorkServices? work) && work is not null)
        {
            try
            {
                EngineExecutionTopology topology = work.ExecutionTopology;
                foreground = topology.ReservedForegroundThreadCount;
                dedicated = topology.DedicatedBackgroundThreadCount;
            }
            catch (InvalidOperationException)
            {
                // A host without a scheduler has no topology; the fallback reservation applies.
            }
        }

        Volatile.Write(ref s_maxDecodeWidth, TextureStreamingCapacityModel.ResolveMaxDecodeWidth(processors, foreground));
        return TextureStreamingCapacityModel.ResolveBaseDecodeWidth(
            processors,
            foreground,
            dedicated,
            RuntimeEngine.Rendering.Settings.TextureStreamingDecodeConcurrency);
    }
}
