using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace XREngine.Rendering;

/// <summary>
/// Process-wide texture streaming capacity: the shared decode gate, whose width follows the CPU capacity of the machine,
/// and the work lanes, whose per-frame limits follow the measured upload cost and frame time.
/// </summary>
/// <remarks>
/// Only one renderer backend streams at a time, so the backends share one decode gate. The gate refreshes its width at
/// most one time in <see cref="CapacityRefreshInterval"/>, from the live process affinity, the job worker count, and the
/// <see cref="RuntimeEngine.Rendering.EngineSettings.TextureStreamingDecodeConcurrency"/> override.
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

    /// <summary>The shortest time between two decode capacity refreshes.</summary>
    internal static readonly TimeSpan CapacityRefreshInterval = TimeSpan.FromSeconds(1.0);

    private static readonly long s_refreshIntervalTicks = (long)(CapacityRefreshInterval.TotalSeconds * Stopwatch.Frequency);
    private static readonly PriorityAsyncSemaphore s_decodeGate = new(ResolveDecodeConcurrency(out _, out _));
    private static long s_lastRefreshTimestamp = Stopwatch.GetTimestamp();
    private static int s_effectiveProcessorCount = XREngine.Data.RuntimeProcessorServices.ReadEffectiveProcessorCount();
    private static int s_jobWorkerCount;

    /// <summary>
    /// Resident data applies and sparse transitions on the OpenGL render thread. The old fixed limit was one in each frame
    /// for each OpenGL backend.
    /// </summary>
    public static TextureStreamingWorkLane OpenGlResidentApply { get; } = new("OpenGL.ResidentApply", initialItemsPerFrame: 1);

    /// <summary>Vulkan transfer chunks that complete, retire, and publish. The old fixed limit was four in each frame.</summary>
    public static TextureStreamingWorkLane VulkanCompletion { get; } = new("Vulkan.Completion", initialItemsPerFrame: 4);

    /// <summary>
    /// The decode gate that the texture residency backends share. Each read refreshes its width when the refresh interval
    /// has passed.
    /// </summary>
    public static PriorityAsyncSemaphore DecodeGate
    {
        get
        {
            RefreshDecodeCapacity();
            return s_decodeGate;
        }
    }

    /// <summary>The current decode gate width.</summary>
    public static int DecodeConcurrency => s_decodeGate.Capacity;

    /// <summary>The processor count of the last refresh (the live process affinity).</summary>
    public static int EffectiveProcessorCount => Volatile.Read(ref s_effectiveProcessorCount);

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
    /// The number of upload preparation workers for a staging pool of <paramref name="stagingBufferCapacity"/> buffers.
    /// </summary>
    public static int ResolveUploadPreparationWorkers(int stagingBufferCapacity)
    {
        RefreshDecodeCapacity();
        return TextureStreamingCapacityModel.ResolveUploadPreparationWorkers(s_decodeGate.Capacity, stagingBufferCapacity);
    }

    /// <summary>
    /// Refreshes the decode gate width when the refresh interval has passed. It allocates nothing.
    /// </summary>
    public static void RefreshDecodeCapacity()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref s_lastRefreshTimestamp);
        if (now - last < s_refreshIntervalTicks)
            return;
        if (Interlocked.CompareExchange(ref s_lastRefreshTimestamp, now, last) != last)
            return;

        int concurrency = ResolveDecodeConcurrency(out int processors, out int jobWorkers);
        Volatile.Write(ref s_effectiveProcessorCount, processors);
        Volatile.Write(ref s_jobWorkerCount, jobWorkers);
        int previous = s_decodeGate.Capacity;
        if (previous == concurrency)
            return;

        s_decodeGate.SetCapacity(concurrency);
        Debug.Textures($"[TextureStreaming] Decode concurrency changed from {previous} to {concurrency} (processors={processors}, jobWorkers={jobWorkers}, override={RuntimeEngine.Rendering.Settings.TextureStreamingDecodeConcurrency?.ToString(CultureInfo.InvariantCulture) ?? "auto"}).");
    }

    /// <summary>Writes the decode width and the state of each lane.</summary>
    public static void AppendDiagnosticSummary(StringBuilder builder)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        builder.Append("TextureStreamingDecode: width=").Append(s_decodeGate.Capacity.ToString(inv))
            .Append(" inUse=").Append(s_decodeGate.InUse.ToString(inv))
            .Append(" processors=").Append(Volatile.Read(ref s_effectiveProcessorCount).ToString(inv))
            .Append(" jobWorkers=").Append(Volatile.Read(ref s_jobWorkerCount).ToString(inv))
            .Append(" cutsceneWaiting=").Append(IsCutsceneWaitingForResidency ? '1' : '0')
            .AppendLine();
        OpenGlResidentApply.AppendDiagnosticSummary(builder);
        VulkanCompletion.AppendDiagnosticSummary(builder);
    }

    private static int ResolveDecodeConcurrency(out int processors, out int jobWorkers)
    {
        processors = XREngine.Data.RuntimeProcessorServices.ReadEffectiveProcessorCount();
        jobWorkers = RuntimeRenderingHostServices.TryGetWork(out IRuntimeRenderWorkServices? work) && work is not null
            ? work.GeneralJobs.WorkerCount
            : 0;
        return TextureStreamingCapacityModel.ResolveDecodeConcurrency(
            processors,
            jobWorkers,
            RuntimeEngine.Rendering.Settings.TextureStreamingDecodeConcurrency);
    }
}
