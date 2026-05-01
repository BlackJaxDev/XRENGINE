using System.Diagnostics;
using System.Text;
using System.Threading;

namespace XREngine.Rendering;

public enum TextureRuntimeLogMode
{
    Disabled = 0,
    Summary = 1,
    SlowOnly = 2,
    Verbose = 3,
}

internal enum TextureRuntimeEventImportance
{
    Verbose,
    Summary,
    Slow,
    Warning,
    Error,
}

internal static class TextureRuntimeDiagnostics
{
    private const string Unknown = "<unknown>";

    private static long s_transitionQueuedCount;
    private static long s_transitionCoalescedCount;
    private static long s_transitionCanceledCount;
    private static long s_transitionAppliedCount;
    private static long s_staleUploadCanceledCount;
    private static long s_uploadValidationFailureCount;
    private static long s_slowUploadCount;
    private static long s_vramPressureCount;
    private static double s_maxUploadMilliseconds;
    private static double s_maxQueueWaitMilliseconds;
    [ThreadStatic]
    private static StringBuilder? t_builder;

    public static bool IsEnabled => RuntimeRenderingHostServices.Current.TextureLogMode != TextureRuntimeLogMode.Disabled;
    public static bool IsVerboseEnabled => RuntimeRenderingHostServices.Current.TextureLogMode == TextureRuntimeLogMode.Verbose;

    public static long TransitionQueuedCount => Interlocked.Read(ref s_transitionQueuedCount);
    public static long TransitionCoalescedCount => Interlocked.Read(ref s_transitionCoalescedCount);
    public static long TransitionCanceledCount => Interlocked.Read(ref s_transitionCanceledCount);
    public static long TransitionAppliedCount => Interlocked.Read(ref s_transitionAppliedCount);
    public static long StaleUploadCanceledCount => Interlocked.Read(ref s_staleUploadCanceledCount);
    public static long UploadValidationFailureCount => Interlocked.Read(ref s_uploadValidationFailureCount);
    public static long SlowUploadCount => Interlocked.Read(ref s_slowUploadCount);
    public static long VramPressureCount => Interlocked.Read(ref s_vramPressureCount);
    public static double MaxUploadMilliseconds => Volatile.Read(ref s_maxUploadMilliseconds);
    public static double MaxQueueWaitMilliseconds => Volatile.Read(ref s_maxQueueWaitMilliseconds);

    public static long StartTiming()
        => Stopwatch.GetTimestamp();

    public static double ElapsedMilliseconds(long startTimestamp)
        => startTimestamp == 0L
            ? 0.0
            : (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public static void RecordQueueWait(double milliseconds)
        => RecordMax(ref s_maxQueueWaitMilliseconds, milliseconds);

    public static void RecordUploadDuration(double milliseconds)
        => RecordMax(ref s_maxUploadMilliseconds, milliseconds);

    public static void LogImportPreviewQueued(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint maxResidentDimension,
        string backendName)
    {
        if (!ShouldLog(TextureRuntimeEventImportance.Verbose))
            return;

        Log("Texture.ImportPreviewQueued",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' residentTarget={maxResidentDimension} backend={backendName}");
    }

    public static void LogImportPreviewReady(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint sourceWidth,
        uint sourceHeight,
        uint residentMaxDimension,
        long committedBytes,
        string backendName)
    {
        if (!ShouldLog(TextureRuntimeEventImportance.Verbose))
            return;

        Log("Texture.ImportPreviewReady",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' logical={sourceWidth}x{sourceHeight} resident={residentMaxDimension} committedBytes={committedBytes} backend={backendName}");
    }

    public static void LogVisibilityRecorded(
        long frameId,
        string? textureName,
        string? sourcePath,
        float projectedPixelSpan,
        float screenCoverage,
        float priorityScore)
    {
        if (!ShouldLog(TextureRuntimeEventImportance.Verbose))
            return;

        StringBuilder builder = RentBuilder();
        builder.Append("frame=").Append(frameId)
            .Append(" texture='").AppendLabel(textureName)
            .Append("' source='").AppendLabel(sourcePath)
            .Append("' projectedPx=").Append(projectedPixelSpan)
            .Append(" coverage=").Append(screenCoverage)
            .Append(" priority=").Append(priorityScore);
        LogPooled("Texture.VisibilityRecorded", builder);
    }

    public static void LogResidencyDesired(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint currentResident,
        uint desiredResident,
        long currentBytes,
        long desiredBytes,
        string backendName,
        string reason)
    {
        if (!ShouldLog(TextureRuntimeEventImportance.Verbose))
            return;

        StringBuilder builder = RentBuilder();
        builder.Append("frame=").Append(frameId)
            .Append(" texture='").AppendLabel(textureName)
            .Append("' source='").AppendLabel(sourcePath)
            .Append("' resident=").Append(currentResident)
            .Append(" desired=").Append(desiredResident)
            .Append(" committedBytes=").Append(currentBytes)
            .Append(" desiredBytes=").Append(desiredBytes)
            .Append(" backend=").Append(backendName)
            .Append(" reason='").AppendLabel(reason).Append('\'');
        LogPooled("Texture.ResidencyDesired", builder);
    }

    public static void LogTransitionQueued(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint currentResident,
        uint targetResident,
        long currentBytes,
        long targetBytes,
        string backendName,
        string reason)
    {
        Interlocked.Increment(ref s_transitionQueuedCount);
        if (!ShouldLog(TextureRuntimeEventImportance.Verbose))
            return;

        StringBuilder builder = RentBuilder();
        builder.Append("frame=").Append(frameId)
            .Append(" texture='").AppendLabel(textureName)
            .Append("' source='").AppendLabel(sourcePath)
            .Append("' resident=").Append(currentResident)
            .Append(" target=").Append(targetResident)
            .Append(" committedBytes=").Append(currentBytes)
            .Append(" targetBytes=").Append(targetBytes)
            .Append(" backend=").Append(backendName)
            .Append(" reason='").AppendLabel(reason).Append('\'');
        LogPooled("Texture.TransitionQueued", builder);
    }

    public static void LogTransitionCoalesced(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint targetResident,
        string backendName,
        string reason)
    {
        Interlocked.Increment(ref s_transitionCoalescedCount);
        if (!ShouldLog(TextureRuntimeEventImportance.Verbose))
            return;

        Log("Texture.TransitionCoalesced",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' target={targetResident} backend={backendName} reason='{Label(reason)}'");
    }

    public static void LogTransitionCanceled(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint pendingResident,
        string backendName,
        string reason)
    {
        Interlocked.Increment(ref s_transitionCanceledCount);
        if (!ShouldLog(TextureRuntimeEventImportance.Warning))
            return;

        Log("Texture.TransitionCanceled",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' pending={pendingResident} backend={backendName} reason='{Label(reason)}'");
    }

    public static void LogTransitionApplied(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint previousResident,
        uint completedResident,
        long committedBytes,
        double queueWaitMilliseconds,
        double executionMilliseconds,
        string backendName)
    {
        Interlocked.Increment(ref s_transitionAppliedCount);
        RecordQueueWait(queueWaitMilliseconds);
        RecordUploadDuration(executionMilliseconds);
        bool slow = queueWaitMilliseconds >= RuntimeRenderingHostServices.Current.TextureSlowQueueWaitMilliseconds
            || executionMilliseconds >= RuntimeRenderingHostServices.Current.TextureSlowTransitionMilliseconds;
        if (slow)
            Interlocked.Increment(ref s_slowUploadCount);

        if (!ShouldLog(slow ? TextureRuntimeEventImportance.Slow : TextureRuntimeEventImportance.Verbose))
            return;

        Log(slow ? "Texture.UploadSlow" : "Texture.TransitionApplied",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' previous={previousResident} resident={completedResident} committedBytes={committedBytes} queueWaitMs={queueWaitMilliseconds:F2} executionMs={executionMilliseconds:F2} backend={backendName}");
    }

    public static void LogUploadChunk(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint glBindingId,
        int mipLevel,
        int startRow,
        int rowCount,
        long bytesUploaded,
        double executionMilliseconds,
        int storageGeneration,
        string backendName)
    {
        RecordUploadDuration(executionMilliseconds);
        bool slow = executionMilliseconds >= RuntimeRenderingHostServices.Current.TextureSlowUploadChunkMilliseconds;
        if (slow)
            Interlocked.Increment(ref s_slowUploadCount);

        if (!ShouldLog(slow ? TextureRuntimeEventImportance.Slow : TextureRuntimeEventImportance.Verbose))
            return;

        StringBuilder builder = RentBuilder();
        builder.Append("frame=").Append(frameId)
            .Append(" texture='").AppendLabel(textureName)
            .Append("' source='").AppendLabel(sourcePath)
            .Append("' gl=").Append(glBindingId)
            .Append(" mip=").Append(mipLevel)
            .Append(" rows=").Append(startRow).Append('+').Append(rowCount)
            .Append(" bytes=").Append(bytesUploaded)
            .Append(" executionMs=").Append(executionMilliseconds)
            .Append(" generation=").Append(storageGeneration)
            .Append(" backend=").Append(backendName);
        LogPooled(slow ? "Texture.UploadSlow" : "Texture.UploadChunk", builder);
    }

    public static void LogCpuTextureWorkSlow(
        long frameId,
        string? sourcePath,
        uint sourceWidth,
        uint sourceHeight,
        uint requestedResidentMaxDimension,
        uint residentMaxDimension,
        bool includeMipChain,
        int mipCount,
        double decodeMilliseconds,
        double resizeMilliseconds,
        double mipBuildMilliseconds,
        double totalMilliseconds)
    {
        bool slow = decodeMilliseconds >= RuntimeRenderingHostServices.Current.TextureSlowCpuDecodeResizeMilliseconds
            || resizeMilliseconds >= RuntimeRenderingHostServices.Current.TextureSlowCpuDecodeResizeMilliseconds
            || mipBuildMilliseconds >= RuntimeRenderingHostServices.Current.TextureSlowMipBuildMilliseconds;
        if (slow)
            Interlocked.Increment(ref s_slowUploadCount);

        if (!ShouldLog(slow ? TextureRuntimeEventImportance.Slow : TextureRuntimeEventImportance.Verbose))
            return;

        Log(slow ? "Texture.UploadSlow" : "Texture.CpuResidentBuilt",
            $"frame={frameId} source='{Label(sourcePath)}' logical={sourceWidth}x{sourceHeight} requestedResident={requestedResidentMaxDimension} resident={residentMaxDimension} includeMipChain={includeMipChain} mips={mipCount} decodeMs={decodeMilliseconds:F2} resizeMs={resizeMilliseconds:F2} mipBuildMs={mipBuildMilliseconds:F2} totalMs={totalMilliseconds:F2} backend=CPU");
    }

    public static void LogStorageAllocated(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint glBindingId,
        uint width,
        uint height,
        uint levels,
        long committedBytes,
        int storageGeneration,
        string backendName,
        string reason)
    {
        if (!ShouldLog(TextureRuntimeEventImportance.Verbose))
            return;

        Log("Texture.StorageAllocated",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' gl={glBindingId} logical={width}x{height} levels={levels} committedBytes={committedBytes} generation={storageGeneration} backend={backendName} reason='{Label(reason)}'");
    }

    public static void LogStorageRecreated(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint glBindingId,
        uint width,
        uint height,
        uint levels,
        int previousGeneration,
        int currentGeneration,
        string backendName,
        string reason)
    {
        if (!ShouldLog(TextureRuntimeEventImportance.Warning))
            return;

        Log("Texture.StorageRecreated",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' gl={glBindingId} logical={width}x{height} levels={levels} previousGeneration={previousGeneration} generation={currentGeneration} backend={backendName} reason='{Label(reason)}'");
    }

    public static void LogUploadValidationFailed(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint glBindingId,
        int mipLevel,
        uint xOffset,
        uint yOffset,
        uint width,
        uint height,
        uint allocatedWidth,
        uint allocatedHeight,
        uint allocatedLevels,
        int storageGeneration,
        string backendName,
        string reason)
    {
        Interlocked.Increment(ref s_uploadValidationFailureCount);
        if (!ShouldLog(TextureRuntimeEventImportance.Error))
            return;

        Log("Texture.UploadValidationFailed",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' gl={glBindingId} mip={mipLevel} uploadRect={xOffset},{yOffset} {width}x{height} allocated={allocatedWidth}x{allocatedHeight} levels={allocatedLevels} generation={storageGeneration} backend={backendName} reason='{Label(reason)}'");
    }

    public static void LogStaleUploadCanceled(
        long frameId,
        string? textureName,
        string? sourcePath,
        uint glBindingId,
        int scheduledGeneration,
        int currentGeneration,
        string backendName,
        string reason)
    {
        Interlocked.Increment(ref s_staleUploadCanceledCount);
        if (!ShouldLog(TextureRuntimeEventImportance.Warning))
            return;

        Log("Texture.TransitionCanceled",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' gl={glBindingId} scheduledGeneration={scheduledGeneration} generation={currentGeneration} backend={backendName} reason='{Label(reason)}'");
    }

    public static void LogVramPressure(
        long frameId,
        string? textureName,
        string? sourcePath,
        long bytesReclaimed,
        long currentBytes,
        long budgetBytes,
        string reason)
    {
        Interlocked.Increment(ref s_vramPressureCount);
        if (!ShouldLog(TextureRuntimeEventImportance.Warning))
            return;

        Log("Texture.VramPressure",
            $"frame={frameId} texture='{Label(textureName)}' source='{Label(sourcePath)}' reclaimedBytes={bytesReclaimed} currentBytes={currentBytes} budgetBytes={budgetBytes} reason='{Label(reason)}'");
    }

    public static void LogRenderWorkBudget(
        long frameId,
        string eventName,
        RenderWorkBudgetSnapshot snapshot,
        string reason)
    {
        if (!ShouldLog(TextureRuntimeEventImportance.Summary))
            return;

        Log(eventName,
            $"frame={frameId} textureQueue={snapshot.TextureUploadQueueDepth} urgentTextureRepair={snapshot.UrgentTextureRepairQueueDepth} shadowQueue={snapshot.ShadowAtlasQueueDepth} textureBudgetMs={snapshot.TextureUploadBudgetMilliseconds:F2} textureConsumedMs={snapshot.TextureUploadConsumedMilliseconds:F2} oldestTextureWaitMs={snapshot.OldestTextureQueueWaitMilliseconds:F2} lastShadowMs={snapshot.LastShadowAtlasMilliseconds:F2} startupBoost={snapshot.StartupBoostActive} reason='{Label(reason)}'");
    }

    public static void LogSummary(
        long frameId,
        ImportedTextureStreamingTelemetry telemetry,
        IReadOnlyList<ImportedTextureStreamingTextureTelemetry> textures,
        bool force = false)
    {
        if (!force && !ShouldLog(TextureRuntimeEventImportance.Summary))
            return;

        int visible = 0;
        int pending = 0;
        int slow = 0;
        long residentBytes = 0L;
        double oldestWait = 0.0;
        double maxUpload = 0.0;

        string topAName = string.Empty;
        string topBName = string.Empty;
        string topCName = string.Empty;
        long topABytes = -1;
        long topBBytes = -1;
        long topCBytes = -1;

        for (int i = 0; i < textures.Count; i++)
        {
            ImportedTextureStreamingTextureTelemetry texture = textures[i];
            if (texture.LastVisibleFrameId == frameId)
                visible++;
            if (texture.HasPendingTransition)
                pending++;
            if (texture.IsSlow)
                slow++;

            long bytes = texture.CurrentCommittedBytes;
            residentBytes += bytes;
            oldestWait = Math.Max(oldestWait, texture.OldestQueueWaitMilliseconds);
            maxUpload = Math.Max(maxUpload, texture.LastUploadMilliseconds);

            if (bytes > topABytes)
            {
                topCBytes = topBBytes;
                topCName = topBName;
                topBBytes = topABytes;
                topBName = topAName;
                topABytes = bytes;
                topAName = texture.TextureName ?? texture.FilePath ?? Unknown;
            }
            else if (bytes > topBBytes)
            {
                topCBytes = topBBytes;
                topCName = topBName;
                topBBytes = bytes;
                topBName = texture.TextureName ?? texture.FilePath ?? Unknown;
            }
            else if (bytes > topCBytes)
            {
                topCBytes = bytes;
                topCName = texture.TextureName ?? texture.FilePath ?? Unknown;
            }
        }

        StringBuilder builder = RentBuilder(512);
        builder.Append("[TextureSummary] frame=").Append(frameId)
            .Append(" tracked=").Append(telemetry.TrackedTextureCount)
            .Append(" visible=").Append(visible)
            .Append(" pending=").Append(pending)
            .Append(" slow=").Append(slow)
            .Append(" uploading=").Append(telemetry.ActiveGpuUploadCount)
            .Append(" residentBytes=").Append(residentBytes)
            .Append(" budget=").Append(telemetry.AvailableManagedBytes)
            .Append(" pressure=").Append(telemetry.AvailableManagedBytes != long.MaxValue && residentBytes > telemetry.AvailableManagedBytes)
            .Append(" promotionsQueued=").Append(telemetry.QueuedPromotionTransitionsThisFrame)
            .Append(" demotionsQueued=").Append(telemetry.QueuedDemotionTransitionsThisFrame)
            .Append(" coalesced=").Append(TransitionCoalescedCount)
            .Append(" canceledStale=").Append(StaleUploadCanceledCount)
            .Append(" slowUploads=").Append(SlowUploadCount)
            .Append(" maxUploadMs=").Append(maxUpload.ToString("F2"))
            .Append(" maxQueueWaitMs=").Append(oldestWait.ToString("F2"))
            .Append(" topResident=");

        AppendTopResident(builder, topAName, topABytes);
        AppendTopResident(builder, topBName, topBBytes);
        AppendTopResident(builder, topCName, topCBytes);

        LogPooled("Texture.VramSummary", builder);
    }

    private static bool ShouldLog(TextureRuntimeEventImportance importance)
    {
        TextureRuntimeLogMode mode = RuntimeRenderingHostServices.Current.TextureLogMode;
        return importance switch
        {
            TextureRuntimeEventImportance.Error or TextureRuntimeEventImportance.Warning => mode != TextureRuntimeLogMode.Disabled,
            TextureRuntimeEventImportance.Slow => mode is TextureRuntimeLogMode.SlowOnly or TextureRuntimeLogMode.Summary or TextureRuntimeLogMode.Verbose,
            TextureRuntimeEventImportance.Summary => mode is TextureRuntimeLogMode.Summary or TextureRuntimeLogMode.Verbose,
            TextureRuntimeEventImportance.Verbose => mode == TextureRuntimeLogMode.Verbose,
            _ => false,
        };
    }

    private static void Log(string eventName, string message)
        => XREngine.Debug.Log(ELogCategory.Textures, EOutputVerbosity.Normal, debugOnly: false, $"[{eventName}] {message}");

    private static void LogPooled(string eventName, StringBuilder builder)
    {
        Log(eventName, builder.ToString());
        ReturnBuilder(builder);
    }

    private static StringBuilder RentBuilder(int capacity = 256)
    {
        StringBuilder? builder = t_builder;
        if (builder is null)
            return new StringBuilder(capacity);

        t_builder = null;
        builder.EnsureCapacity(capacity);
        return builder;
    }

    private static void ReturnBuilder(StringBuilder builder)
    {
        if (builder.Capacity > 4096)
            return;

        builder.Clear();
        t_builder = builder;
    }

    private static string Label(string? value)
        => string.IsNullOrWhiteSpace(value) ? Unknown : value.Replace('\'', '"');

    private static StringBuilder AppendLabel(this StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return builder.Append(Unknown);

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            builder.Append(c == '\'' ? '"' : c);
        }

        return builder;
    }

    private static void AppendTopResident(StringBuilder builder, string name, long bytes)
    {
        if (bytes <= 0 || string.IsNullOrWhiteSpace(name))
            return;

        if (builder[^1] != '=')
            builder.Append(',');

        builder.Append(Label(name)).Append(':').Append(bytes);
    }

    private static void RecordMax(ref double target, double value)
    {
        if (value <= 0.0)
            return;

        double current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
