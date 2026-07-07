using ImageMagick;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

internal sealed class ImportedTextureStreamingManager
{
    private const int MaxResidentTransitionsPerFrame = 2;
    private const int MaxPromotionTransitionsPerFrame = 1;
    private const int MaxSparseFinalizationsPerFrame = 2;
    private const int MinTransitionCooldownFrames = 2;
    private const int PromotionCooldownFrames = 6;
    private const int FailedPromotionCooldownFrames = 180;
    private const int MaxFailedPromotionCooldownFrames = 1800;
    private const int DemotionCooldownFrames = 90;
    private const int VisiblePromotionPinFrames = 180;
    private const int PromotionFadeFrames = 90;
    private const int RecentlyBoundFallbackFrames = 3;
    private const long MaxPromotionBytesPerFrame = 32L * 1024L * 1024L;
    private const long MaxImportEraPromotionBytesPerFrame = 4L * 1024L * 1024L;
    private const uint BoundMaterialRelatedMinResidentMaxDimension = 512u;
    private const float BoundMaterialFallbackProjectedPixelSpan = 384.0f;
    private const float BoundMaterialFallbackScreenCoverage = 0.12f;
    private const float UrgentVisibleProjectedPixelSpan = 512.0f;
    private const float UrgentVisibleScreenCoverage = 0.08f;
    private const int RecordRefCompactionIntervalFrames = 600;
    private const int TextureSummaryIntervalFrames = 60;
    private const float PageSelectionFullCoverageThreshold = 0.85f;
    private const double VulkanDenseNonPressureDemotionPreserveBudgetFillRatio = 0.75;
    private const double VulkanAllocatorStreamingBudgetRatio = 0.84;
    private const long VulkanAllocatorStreamingReserveBytes = 768L * 1024L * 1024L;
    private const string VulkanImportedTextureStreamingTodoPath = "docs/work/todo/rendering/vulkan-imported-texture-streaming-todo.md";
    private const string VulkanImportedTexturePreviewFreezeEnvVar = XREngineEnvironmentVariables.VulkanImportedTexturePreviewFreeze;
    private const string VulkanPreviewFreezeReason = "explicit Vulkan imported-texture preview freeze requested";

    /// <summary>
    /// If a pending transition has been stuck for this many frames with no active
    /// decodes on either backend, force-clear it so the next Evaluate cycle can
    /// re-queue a fresh transition.
    /// </summary>
    private const int StuckPendingTransitionFrameThreshold = 300;

    public static ImportedTextureStreamingManager Instance { get; } = new();

    private readonly ITextureResidencyBackend _tieredBackend = new GLTieredTextureResidencyBackend();
    private readonly ITextureResidencyBackend _sparseBackend = new GLSparseTextureResidencyBackend();
    private readonly ITextureResidencyBackend _vulkanDenseBackend = new VulkanDenseTextureResidencyBackend();
    private readonly TextureStreamingRegistry _registry = new();
    private readonly TextureTransitionQueue _transitionQueue = new();
    private readonly List<ImportedTextureStreamingSnapshot> _evaluateSnapshotScratch = new();
    private readonly Dictionary<string, int> _promotionCountsByGroupScratch = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DeferredPromotionCandidate> _deferredPromotionScratch = new();

    private int _callbacksSubscribed;
    private int _activeImportedModelImports;
    private long _collectFrameId;
    private long _lastEvaluatedFrameId;
    private int _queuedTransitionsThisFrame;
    private int _queuedPromotionTransitionsThisFrame;
    private int _queuedDemotionTransitionsThisFrame;
    private int _sparseFinalizeScheduled;
    private long _lastTelemetryFrameId;
    private long _lastCurrentManagedBytes;
    private long _lastAvailableManagedBytes;
    private long _lastAssignedManagedBytes;
    private long _lastCompactionFrameId = long.MinValue;

    private ImportedTextureStreamingManager()
    {
    }

    private readonly record struct DeferredPromotionCandidate(
        ImportedTextureStreamingSnapshot Snapshot,
        uint AssignedResidentSize,
        SparseTextureStreamingPageSelection DesiredPageSelection,
        long TargetCommittedBytes,
        bool PressureDemotion);

    internal bool HasActiveImportedModelImports
        => Volatile.Read(ref _activeImportedModelImports) > 0;

    internal bool TryDescribeActiveStartupTextureWork(out string reason)
    {
        int activeImportScopes = Volatile.Read(ref _activeImportedModelImports);
        int pendingTransitions = _registry.CountPendingTransitions();
        int activeDecodes = _tieredBackend.ActiveDecodeCount + _sparseBackend.ActiveDecodeCount + _vulkanDenseBackend.ActiveDecodeCount;
        int queuedDecodes = _tieredBackend.QueuedDecodeCount + _sparseBackend.QueuedDecodeCount + _vulkanDenseBackend.QueuedDecodeCount;
        int activeGpuUploads = _tieredBackend.ActiveGpuUploadCount + _sparseBackend.ActiveGpuUploadCount + _vulkanDenseBackend.ActiveGpuUploadCount;
        bool hasVulkanUploadWork = VulkanTextureUploadService.TryDescribeActiveUploadWork(out string vulkanUploadReason);

        if (activeImportScopes <= 0 &&
            pendingTransitions <= 0 &&
            activeDecodes <= 0 &&
            queuedDecodes <= 0 &&
            activeGpuUploads <= 0 &&
            !hasVulkanUploadWork)
        {
            reason = string.Empty;
            return false;
        }

        reason =
            $"imported texture streaming is still active (imports={activeImportScopes}, pendingTransitions={pendingTransitions}, activeGpuUploads={activeGpuUploads}, activeDecodes={activeDecodes}, queuedDecodes={queuedDecodes}";
        if (hasVulkanUploadWork)
            reason += $"; {vulkanUploadReason}";
        reason += ")";
        return true;
    }

    internal bool TryDescribeBlockingOpenXrEyeTextureWork(out string reason)
    {
        int activeGpuUploads = _tieredBackend.ActiveGpuUploadCount + _sparseBackend.ActiveGpuUploadCount;
        bool hasVulkanBlockingWork = VulkanTextureUploadService.TryDescribeBlockingOpenXrEyeUploadWork(out string vulkanUploadReason);

        if (activeGpuUploads <= 0 && !hasVulkanBlockingWork)
        {
            reason = string.Empty;
            return false;
        }

        reason = $"imported texture streaming has render-blocking work (activeGpuUploads={activeGpuUploads}";
        if (hasVulkanBlockingWork)
            reason += $"; {vulkanUploadReason}";
        reason += ")";
        return true;
    }

    private sealed class ImportedTextureStreamingScope(ImportedTextureStreamingManager owner) : IDisposable
    {
        private readonly ImportedTextureStreamingManager _owner = owner;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            int remaining = Interlocked.Decrement(ref _owner._activeImportedModelImports);
            if (remaining < 0)
                Interlocked.Exchange(ref _owner._activeImportedModelImports, 0);
        }
    }

    public IDisposable EnterScope()
    {
        EnsureCallbacksSubscribed();
        Interlocked.Increment(ref _activeImportedModelImports);
        return new ImportedTextureStreamingScope(this);
    }

    public void RegisterTexture(string filePath, XRTexture2D texture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(texture);

        EnsureCallbacksSubscribed();

        string normalizedPath = Path.GetFullPath(filePath);
        if (string.IsNullOrWhiteSpace(texture.FilePath))
            texture.FilePath = normalizedPath;

        if (string.IsNullOrWhiteSpace(texture.Name))
            texture.Name = Path.GetFileNameWithoutExtension(normalizedPath);

        _ = GetOrCreateRecord(texture, normalizedPath);
    }

    public EnumeratorJob SchedulePreviewJob(
        string filePath,
        XRTexture2D? texture = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be provided.", nameof(filePath));

        XRTexture2D target = texture ?? new XRTexture2D();
        XRTexture2D.ApplyTextureStreamingAuthorityPath(target, filePath);
        if (string.IsNullOrWhiteSpace(target.Name))
            target.Name = Path.GetFileNameWithoutExtension(filePath);

        string authorityPath = target.FilePath ?? filePath;
        ImportedTextureStreamingRecord record = GetOrCreateRecord(target, filePath);
        lock (record.Sync)
        {
            record.Format = ESizedInternalFormat.Rgba8;
            record.Backend = ResolvePreviewBackendCandidate(record.Format);
        }

        ITextureResidencyBackend previewBackend = record.Backend;
        TextureRuntimeDiagnostics.LogImportPreviewQueued(
            Volatile.Read(ref _collectFrameId),
            target.Name,
            authorityPath,
            previewBackend.PreviewMaxDimension,
            previewBackend.Name);

        return previewBackend.SchedulePreviewLoad(
            record.Source ?? TextureStreamingSourceFactory.Create(filePath),
            target,
            residentData =>
            {
                ITextureResidencyBackend readyBackend;
                lock (record.Sync)
                {
                    record.Format = ESizedInternalFormat.Rgba8;
                    record.SourceWidth = residentData.SourceWidth;
                    record.SourceHeight = residentData.SourceHeight;
                    record.ResidentMaxDimension = residentData.ResidentMaxDimension;
                    record.DesiredMaxDimension = residentData.ResidentMaxDimension;
                    record.PendingMaxDimension = 0;
                    record.PreviewReady = true;
                    record.Backend = ResolveBackendForTexture(residentData.SourceWidth, residentData.SourceHeight, record.Format);
                    long generation = Math.Max(record.ResidentGeneration, record.PublishedGeneration) + 1L;
                    record.ResidentGeneration = generation;
                    record.UploadGeneration = generation;
                    readyBackend = record.Backend;
                }

                long committedBytes = readyBackend.EstimateCommittedBytes(
                    residentData.SourceWidth,
                    residentData.SourceHeight,
                    residentData.ResidentMaxDimension,
                    ESizedInternalFormat.Rgba8,
                    sparseNumLevels: 0,
                    SparseTextureStreamingPageSelection.Full);
                TextureRuntimeDiagnostics.LogImportPreviewReady(
                    Volatile.Read(ref _collectFrameId),
                    target.Name,
                    authorityPath,
                    residentData.SourceWidth,
                    residentData.SourceHeight,
                    residentData.ResidentMaxDimension,
                    committedBytes,
                    readyBackend.Name);
            },
            onDeferred: null,
            tex =>
            {
                lock (record.Sync)
                {
                    record.SparseNumLevels = tex.SparseTextureStreamingNumSparseLevels;
                    record.Backend ??= ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format);
                    if (record.UploadGeneration > record.PublishedGeneration)
                        record.PublishedGeneration = record.UploadGeneration;
                }

                onFinished?.Invoke(tex);
            },
            onError,
            onCanceled,
            onProgress,
                shouldAcceptResult: null,
            cancellationToken,
            priority);
    }

    public void RecordUsage(XRMaterial? material, float distanceFromCamera)
        => RecordUsage(material, new ImportedTextureStreamingUsage(distanceFromCamera));

    public void RecordUsage(XRMaterial? material, ImportedTextureStreamingUsage usage)
    {
        _registry.RecordUsage(material, usage, Volatile.Read(ref _collectFrameId));
    }

    public void RecordMaterialBinding(XRMaterialBase? material)
    {
        _registry.RecordMaterialBinding(material, Volatile.Read(ref _collectFrameId));
    }

    public ImportedTextureStreamingTelemetry GetTelemetry()
    {
        List<ImportedTextureStreamingSnapshot> snapshots = CollectSnapshots();
        int pendingTransitions = 0;
        string backendName = "None";
        string displayBackendName = "None";
        int vulkanFrozenCount = 0;
        string freezeReason = string.Empty;
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].PendingMaxDimension != 0)
                pendingTransitions++;

            if (ShouldFreezeVulkanImportedTextureResidency(snapshots[i]))
            {
                vulkanFrozenCount++;
                if (string.IsNullOrEmpty(freezeReason))
                    freezeReason = ResolveVulkanFreezeReason(snapshots[i]);
            }
        }

        if (snapshots.Count > 0)
        {
            backendName = snapshots[0].Backend.Name;
            displayBackendName = ResolveTelemetryBackendName(snapshots[0].Backend);
            for (int i = 1; i < snapshots.Count; i++)
            {
                if (!string.Equals(backendName, snapshots[i].Backend.Name, StringComparison.Ordinal))
                {
                    backendName = "Mixed";
                    displayBackendName = "Mixed";
                    break;
                }
            }
        }

        return new ImportedTextureStreamingTelemetry(
            backendName,
            displayBackendName,
            Volatile.Read(ref _activeImportedModelImports),
            snapshots.Count,
            pendingTransitions,
            _tieredBackend.ActiveDecodeCount + _sparseBackend.ActiveDecodeCount + _vulkanDenseBackend.ActiveDecodeCount,
            _tieredBackend.QueuedDecodeCount + _sparseBackend.QueuedDecodeCount + _vulkanDenseBackend.QueuedDecodeCount,
            _tieredBackend.ActiveGpuUploadCount + _sparseBackend.ActiveGpuUploadCount + _vulkanDenseBackend.ActiveGpuUploadCount,
            Volatile.Read(ref _queuedTransitionsThisFrame),
            Volatile.Read(ref _queuedPromotionTransitionsThisFrame),
            Volatile.Read(ref _queuedDemotionTransitionsThisFrame),
            Volatile.Read(ref _lastTelemetryFrameId),
            Volatile.Read(ref _lastCurrentManagedBytes),
            Volatile.Read(ref _lastAvailableManagedBytes),
            Volatile.Read(ref _lastAssignedManagedBytes),
            _tieredBackend.UploadBytesScheduledThisFrame + _sparseBackend.UploadBytesScheduledThisFrame + _vulkanDenseBackend.UploadBytesScheduledThisFrame,
            Volatile.Read(ref _activeImportedModelImports) > 0,
            vulkanFrozenCount > 0,
            freezeReason);
    }

    public IReadOnlyList<ImportedTextureStreamingTextureTelemetry> GetTrackedTextureTelemetry()
    {
        List<ImportedTextureStreamingSnapshot> snapshots = CollectSnapshots();
        List<ImportedTextureStreamingTextureTelemetry> telemetry = new(snapshots.Count);
        long currentFrameId = Volatile.Read(ref _lastTelemetryFrameId);
        double slowQueueThreshold = RuntimeRenderingHostServices.Current.TextureSlowQueueWaitMilliseconds;
        double slowUploadThreshold = RuntimeRenderingHostServices.Current.TextureSlowTransitionMilliseconds;
        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
            uint desiredResidentSize = snapshot.PendingMaxDimension != 0
                ? snapshot.PendingMaxDimension
                : snapshot.Record.DesiredMaxDimension;
            uint minimumResidentSize = XRTexture2D.GetMinimumResidentSize(sourceMaxDimension);
            SparseTextureStreamingPageSelection desiredPageSelection = snapshot.PendingMaxDimension != 0
                ? snapshot.Record.PendingPageSelection.Normalize(PageSelectionFullCoverageThreshold)
                : DetermineDesiredPageSelection(snapshot, Math.Max(minimumResidentSize, desiredResidentSize), Volatile.Read(ref _lastTelemetryFrameId));
            long desiredCommittedBytes = snapshot.Backend.EstimateCommittedBytes(
                snapshot.SourceWidth,
                snapshot.SourceHeight,
                Math.Max(minimumResidentSize, desiredResidentSize),
                snapshot.Format,
                snapshot.SparseNumLevels,
                desiredPageSelection);
            bool hasPendingTransition = snapshot.PendingMaxDimension != 0;
            double oldestQueueWaitMilliseconds = hasPendingTransition && snapshot.PendingTransitionQueuedTimestamp != 0L
                ? TextureRuntimeDiagnostics.ElapsedMilliseconds(snapshot.PendingTransitionQueuedTimestamp)
                : Math.Max(snapshot.LastTransitionQueueWaitMilliseconds, snapshot.LastTextureQueueWaitMilliseconds);
            double lastUploadMilliseconds = Math.Max(snapshot.LastTransitionExecutionMilliseconds, snapshot.LastTextureUploadMilliseconds);
            bool isSlow = oldestQueueWaitMilliseconds >= slowQueueThreshold
                || lastUploadMilliseconds >= slowUploadThreshold;
            bool vulkanFrozen = ShouldFreezeVulkanImportedTextureResidency(snapshot);

            telemetry.Add(new ImportedTextureStreamingTextureTelemetry(
                snapshot.TextureName,
                snapshot.FilePath,
                snapshot.SamplerName,
                snapshot.SourceWidth,
                snapshot.SourceHeight,
                snapshot.ResidentMaxDimension,
                desiredResidentSize,
                snapshot.PendingMaxDimension,
                snapshot.LastVisibleFrameId,
                snapshot.MinVisibleDistance,
                snapshot.MaxProjectedPixelSpan,
                snapshot.MaxScreenCoverage,
                snapshot.UvDensityHint,
                snapshot.CurrentPageSelection.Normalize(PageSelectionFullCoverageThreshold).CoverageFraction,
                desiredPageSelection.CoverageFraction,
                snapshot.PreviewReady,
                snapshot.CurrentCommittedBytes,
                desiredCommittedBytes,
                hasPendingTransition,
                snapshot.LastVisibleFrameId == currentFrameId,
                isSlow,
                snapshot.LastPressureDemotionFrameId != long.MinValue,
                snapshot.ValidationFailureCount > 0,
                snapshot.ValidationFailureCount,
                oldestQueueWaitMilliseconds,
                lastUploadMilliseconds,
                CalculatePriorityScore(snapshot),
                snapshot.Backend.Name,
                ResolveTelemetryBackendName(snapshot.Backend),
                vulkanFrozen,
                vulkanFrozen ? ResolveVulkanFreezeReason(snapshot) : string.Empty,
                snapshot.ResidentGeneration,
                snapshot.PublishedGeneration,
                snapshot.UploadGeneration,
                snapshot.RetirementGeneration));
        }

        return telemetry;
    }

    public void DumpSummary()
    {
        long frameId = Volatile.Read(ref _lastTelemetryFrameId);
        TextureRuntimeDiagnostics.LogSummary(frameId, GetTelemetry(), GetTrackedTextureTelemetry(), force: true);
    }

    internal static uint DetermineDesiredResidentSize(
        ImportedTextureStreamingPolicyInput input,
        long frameId,
        bool allowPromotions,
        uint previewMaxDimension)
        => TextureResidencyPolicy.DetermineDesiredResidentSize(input, frameId, allowPromotions, previewMaxDimension);

    internal static float CalculatePromotionFadeBias(uint sourceWidth, uint sourceHeight, uint previousResidentSize, uint nextResidentSize)
        => TextureResidencyPolicy.CalculatePromotionFadeBias(sourceWidth, sourceHeight, previousResidentSize, nextResidentSize);

    internal static float SmoothPromotionFadeProgress(float t)
        => TextureResidencyPolicy.SmoothPromotionFadeProgress(t);

    internal uint FitResidentSizeToBudget(
        ImportedTextureStreamingBudgetInput input,
        uint desiredResidentSize,
        long availableManagedBytes,
        ITextureResidencyBackend? backend = null,
        ESizedInternalFormat format = ESizedInternalFormat.Rgba8,
        int sparseNumLevels = 0)
    {
        backend ??= RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan
            ? _vulkanDenseBackend
            : _tieredBackend;
        return TextureResidencyPolicy.FitResidentSizeToBudget(input, desiredResidentSize, availableManagedBytes, backend, format, sparseNumLevels);
    }

    private static float NormalizeUvDensityHint(float value)
        => TextureResidencyPolicy.NormalizeUvDensityHint(value);

    private static uint QuantizeResidentSize(uint sourceMaxDimension, uint minimumResidentSize, float targetPixelSpan)
        => throw new NotSupportedException("Use TextureResidencyPolicy for resident-size quantization.");

    private static uint GetNextLowerResidentCandidate(uint sourceMaxDimension, uint currentResidentSize, uint minimumResidentSize)
        => throw new NotSupportedException("Use TextureResidencyPolicy for resident-size demotion candidates.");

    private static float ResolveTextureRoleMultiplier(string? samplerName)
        => TextureResidencyPolicy.ResolveTextureRoleMultiplier(samplerName);

    private static SparseTextureStreamingPageSelection DetermineDesiredPageSelection(ImportedTextureStreamingSnapshot snapshot, uint residentSize, long frameId)
        => TextureResidencyPolicy.DetermineDesiredPageSelection(snapshot, residentSize, frameId);

    private ITextureResidencyBackend ResolvePreviewBackendCandidate(ESizedInternalFormat format)
    {
        RuntimeGraphicsApiKind backend = RuntimeRenderingHostServices.Current.CurrentRenderBackend;
        if (backend == RuntimeGraphicsApiKind.Vulkan)
            return _vulkanDenseBackend;

        return backend == RuntimeGraphicsApiKind.OpenGL
            && RuntimeRenderingHostServices.Current.GetSparseTextureStreamingSupport(format).IsAvailable
                ? _sparseBackend
                : _tieredBackend;
    }

    private ITextureResidencyBackend ResolveBackendForTexture(uint sourceWidth, uint sourceHeight, ESizedInternalFormat format)
    {
        RuntimeGraphicsApiKind backend = RuntimeRenderingHostServices.Current.CurrentRenderBackend;
        if (backend == RuntimeGraphicsApiKind.Vulkan)
            return _vulkanDenseBackend;

        if (backend != RuntimeGraphicsApiKind.OpenGL)
            return _tieredBackend;

        SparseTextureStreamingSupport support = RuntimeRenderingHostServices.Current.GetSparseTextureStreamingSupport(format);
        if (!support.IsAvailable || !support.IsPageAligned(sourceWidth, sourceHeight))
            return _tieredBackend;

        return _sparseBackend;
    }

    private void EnsureCallbacksSubscribed()
    {
        if (Interlocked.Exchange(ref _callbacksSubscribed, 1) != 0)
            return;

        // Initialise to 1 so RecordUsage (which gates on frameId > 0) works
        // from the very first collection phase.
        Interlocked.Exchange(ref _collectFrameId, 1);

        // NOTE: We intentionally do NOT subscribe to CollectVisible.
        // Incrementing _collectFrameId inside a CollectVisible handler races
        // with viewport CollectVisibleAutomatic handlers (parallel multicast
        // ordering is intentionally not deterministic).  PostCollectVisible
        // runs after all viewport collection has completed, so residency policy
        // can consume complete frame data before SwapBuffers publishes command
        // buffers to the render thread.
        RuntimeRenderingHostServices.Current.SubscribeViewportPostCollectVisible(OnPostCollectVisible);
        RuntimeRenderingHostServices.Current.SubscribeViewportSwapBuffers(OnSwapBuffers);
    }

    private void OnPostCollectVisible()
    {
        using var sample = RuntimeRenderingHostServices.Current.StartProfileScope("TextureStreaming.PostCollectVisible");

        long frameId = Volatile.Read(ref _collectFrameId);
        if (frameId <= 0)
            return;

        UpdatePromotionFades(frameId);
        Evaluate(frameId);
        Volatile.Write(ref _lastEvaluatedFrameId, frameId);

        // Advance for the next collection phase.  Every viewport that runs
        // during the upcoming CollectVisible tick will tag usages with the new
        // value, and the following PostCollectVisible will evaluate against it.
        Interlocked.Increment(ref _collectFrameId);
    }

    private void OnSwapBuffers()
    {
        long frameId = Volatile.Read(ref _lastEvaluatedFrameId);
        if (frameId <= 0)
            frameId = Volatile.Read(ref _collectFrameId);
        if (frameId <= 0)
            return;

        FinalizePendingSparseTransitions(frameId);
    }

    private void FinalizePendingSparseTransitions(long frameId)
    {
        if (!RuntimeRenderingHostServices.Current.IsRenderThread)
        {
            if (Interlocked.Exchange(ref _sparseFinalizeScheduled, 1) == 0)
            {
                RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
                    () =>
                    {
                        try
                        {
                            FinalizePendingSparseTransitions(Volatile.Read(ref _collectFrameId));
                        }
                        finally
                        {
                            Volatile.Write(ref _sparseFinalizeScheduled, 0);
                        }
                    },
                    "TextureStreaming.FinalizeSparseTransitions",
                    RenderThreadJobKind.TextureUpload);
            }

            return;
        }

        int completedFinalizations = 0;
        WeakReference<ImportedTextureStreamingRecord>[] refs = _registry.GetRecordReferencesSnapshot();
        for (int i = 0; i < refs.Length; i++)
        {
            if (completedFinalizations >= MaxSparseFinalizationsPerFrame)
                break;

            if (!refs[i].TryGetTarget(out ImportedTextureStreamingRecord? record)
                || !record.Texture.TryGetTarget(out XRTexture2D? texture))
            {
                continue;
            }

            CancellationTokenSource? cts = null;
            SparseTextureStreamingTransitionRequest request = default;
            SparseTextureStreamingTransitionResult transitionResult = default;
            uint pendingResidentSize = 0;
            bool hasDeferredTransition = false;
            if (!Monitor.TryEnter(record.Sync))
                continue;

            try
            {
                if (record.PendingLoadCts is null
                    || record.PendingSparseTransitionResult is not { ExposureDeferred: true } deferredResult
                    || record.PendingMaxDimension == 0)
                {
                    continue;
                }

                cts = record.PendingLoadCts;
                request = record.PendingSparseTransitionRequest;
                transitionResult = deferredResult;
                pendingResidentSize = record.PendingMaxDimension;
                hasDeferredTransition = true;
            }
            finally
            {
                Monitor.Exit(record.Sync);
            }

            if (!hasDeferredTransition || cts is null)
                continue;

            if (FinalizePendingSparseTransitionOnRenderThread(record, texture, cts, request, transitionResult, pendingResidentSize, frameId))
                completedFinalizations++;
        }
    }

    private bool FinalizePendingSparseTransitionOnRenderThread(
        ImportedTextureStreamingRecord record,
        XRTexture2D texture,
        CancellationTokenSource cts,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult,
        uint pendingResidentSize,
        long frameId)
    {
        if (!Monitor.TryEnter(record.Sync))
            return false;

        try
        {
            if (!IsCurrentDeferredSparseTransition(record, cts, transitionResult))
                return false;
        }
        finally
        {
            Monitor.Exit(record.Sync);
        }

        SparseTextureStreamingFinalizeResult finalizeResult = RuntimeRenderingHostServices.Current.FinalizeSparseTextureStreamingTransition(
            texture,
            request,
            transitionResult);
        if (!finalizeResult.Completed)
            return false;

        if (!finalizeResult.Succeeded)
        {
            string textureLabel = texture.Name ?? record.FilePath ?? "(unnamed texture)";
            Debug.TexturesWarning(
                $"Deferred sparse texture transition failed for '{textureLabel}': {finalizeResult.FailureReason ?? "unknown reason"}.");
            ClearPendingTransition(record, cts, texture: null, completedResidentSize: 0, frameId);
            return true;
        }

        ClearPendingTransition(record, cts, texture, pendingResidentSize, frameId);
        return true;
    }

    private static bool IsCurrentDeferredSparseTransition(
        ImportedTextureStreamingRecord record,
        CancellationTokenSource cts,
        SparseTextureStreamingTransitionResult transitionResult)
    {
        return ReferenceEquals(record.PendingLoadCts, cts)
            && record.PendingSparseTransitionResult is { ExposureDeferred: true } current
            && current.FenceSync == transitionResult.FenceSync;
    }

    private ImportedTextureStreamingRecord GetOrCreateRecord(XRTexture2D texture, string? filePath = null)
        => _registry.GetOrCreateRecord(texture, filePath, ResolvePreviewBackendCandidate);

    private void UpdatePromotionFades(long frameId)
    {
        if (!ShouldApplySamplerLodBiasPromotionFade())
            return;

        WeakReference<ImportedTextureStreamingRecord>[] refs = _registry.GetRecordReferencesSnapshot();
        for (int i = 0; i < refs.Length; i++)
        {
            if (!refs[i].TryGetTarget(out ImportedTextureStreamingRecord? record)
                || !record.Texture.TryGetTarget(out XRTexture2D? texture))
            {
                continue;
            }

            if (Volatile.Read(ref record.PromotionFadeEndFrameId) == long.MinValue)
                continue;

            float targetLodBias = 0.0f;
            bool shouldApply = false;
            if (!Monitor.TryEnter(record.Sync))
                continue;

            try
            {
                if (record.PromotionFadeEndFrameId == long.MinValue)
                    continue;

                if (frameId >= record.PromotionFadeEndFrameId)
                {
                    ClearPromotionFadeState(record);
                    targetLodBias = record.BaseLodBias;
                    shouldApply = true;
                }
                else
                {
                    long fadeDurationFrames = Math.Max(1L, record.PromotionFadeEndFrameId - record.PromotionFadeStartFrameId);
                    float t = Math.Clamp((frameId - record.PromotionFadeStartFrameId) / (float)fadeDurationFrames, 0.0f, 1.0f);
                    float streamingLodBias = record.PromotionFadeStartBias * (1.0f - SmoothPromotionFadeProgress(t));
                    record.CurrentStreamingLodBias = streamingLodBias;
                    targetLodBias = record.BaseLodBias + streamingLodBias;
                    shouldApply = true;
                }
            }
            finally
            {
                Monitor.Exit(record.Sync);
            }

            if (shouldApply && !NearlyEquals(texture.LodBias, targetLodBias))
                texture.LodBias = targetLodBias;
        }
    }

    private void CompactRecordRefs(long frameId)
    {
        _lastCompactionFrameId = frameId;

        _registry.CompactRecordRefs();
    }

    private void Evaluate(long frameId)
    {
        if (_lastCompactionFrameId == long.MinValue
            || frameId - _lastCompactionFrameId >= RecordRefCompactionIntervalFrames)
        {
            CompactRecordRefs(frameId);
        }

        List<ImportedTextureStreamingSnapshot> snapshots = _evaluateSnapshotScratch;
        CollectSnapshots(snapshots);
        if (snapshots.Count == 0)
        {
            // Log once when first called with no snapshots, to confirm Evaluate is running
            if (frameId <= 10 && frameId % 5 == 0)
                Debug.Textures(
                    $"[TextureStreaming] frame={frameId} Evaluate called but 0 snapshots (recordRefs={_registry.RecordReferenceCount})");
            return;
        }

        bool importsActive = Volatile.Read(ref _activeImportedModelImports) > 0;
        bool allowPromotions = true;
        long currentManagedBytes = 0L;
        for (int i = 0; i < snapshots.Count; i++)
            currentManagedBytes += snapshots[i].CurrentCommittedBytes;

        // Recovery pass: detect and force-clear stuck pending transitions.
        // A pending transition is "stuck" when its async load / upload should have
        // completed but ClearPendingTransition was never called (e.g., a callback
        // dropped after decode/upload work finished). Visible or recently bound
        // textures get per-record recovery even while unrelated uploads are still
        // active, otherwise one stale upload backlog can pin everything at preview
        // resolution indefinitely.
        bool anyDecodeActive = _tieredBackend.ActiveDecodeCount > 0
            || _tieredBackend.QueuedDecodeCount > 0
            || _sparseBackend.ActiveDecodeCount > 0
            || _sparseBackend.QueuedDecodeCount > 0;
        bool anyGpuUploadActive = _tieredBackend.ActiveGpuUploadCount > 0
            || _sparseBackend.ActiveGpuUploadCount > 0;
        bool globalStuckRecoveryAllowed = !anyDecodeActive && !anyGpuUploadActive;
        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            if (snapshot.PendingMaxDimension == 0)
                continue;

            long framesSincePending = snapshot.LastTransitionFrameId > 0
                ? frameId - snapshot.LastTransitionFrameId
                : frameId;
            if (framesSincePending < StuckPendingTransitionFrameThreshold)
                continue;

            bool importantNow = snapshot.LastVisibleFrameId == frameId
                || IsRecentlyBound(snapshot, frameId)
                || snapshot.PendingMaxDimension > snapshot.ResidentMaxDimension;
            if (!globalStuckRecoveryAllowed && !importantNow)
                continue;

            Debug.TexturesWarning(
                $"[TextureStreaming] Clearing stuck pending transition for '{snapshot.Record.FilePath}' "
                + $"(pending={snapshot.Record.PendingMaxDimension}, resident={snapshot.Record.ResidentMaxDimension}, "
                + $"staleFrames={framesSincePending}, globalIdle={globalStuckRecoveryAllowed})");

            if (!_transitionQueue.TryForceClearStuckTransition(snapshot.Record, frameId, out CancellationTokenSource? pendingLoadCts))
                continue;

            CancelPendingLoad(pendingLoadCts);
        }

        long trackedBudgetBytes = RuntimeRenderingHostServices.Current.TrackedVramBudgetBytes;
        long trackedVramBytes = RuntimeRenderingHostServices.Current.TrackedVramBytes;
        long nonManagedBytes = Math.Max(0L, trackedVramBytes - currentManagedBytes);
        bool usingVulkanAllocatorBudget = TryApplyVulkanAllocatorStreamingBudget(
            currentManagedBytes,
            ref trackedBudgetBytes,
            ref nonManagedBytes,
            out string vulkanAllocatorBudgetReason);
        long availableManagedBytes = trackedBudgetBytes == long.MaxValue
            ? long.MaxValue
            : Math.Max(0L, trackedBudgetBytes - nonManagedBytes);
        if (usingVulkanAllocatorBudget
            && availableManagedBytes < currentManagedBytes)
        {
            allowPromotions = false;
        }

        snapshots.Sort((left, right) => ComparePriority(left, right, frameId));
        int visibleWithoutPreviewCount = 0;
        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            if (snapshot.LastVisibleFrameId == frameId && !snapshot.PreviewReady)
                visibleWithoutPreviewCount++;
        }

        long assignedManagedBytes = 0L;
        int queuedTransitions = 0;
        int queuedPromotions = 0;
        int queuedDemotions = 0;
        long queuedPromotionBytes = 0L;
        Dictionary<string, int> promotionCountsByGroup = _promotionCountsByGroupScratch;
        promotionCountsByGroup.Clear();
        List<DeferredPromotionCandidate> deferredPromotions = _deferredPromotionScratch;
        deferredPromotions.Clear();

        bool TryQueueCandidate(
            ImportedTextureStreamingSnapshot snapshot,
            uint assignedResidentSize,
            SparseTextureStreamingPageSelection desiredPageSelection,
            long targetCommittedBytes,
            bool pressureDemotion,
            bool enforceFairness)
        {
            SparseTextureStreamingPageSelection currentPageSelection = snapshot.PendingMaxDimension != 0
                ? snapshot.Record.PendingPageSelection
                : snapshot.CurrentPageSelection;
            uint currentResidentSize = snapshot.PendingMaxDimension != 0
                ? snapshot.PendingMaxDimension
                : snapshot.ResidentMaxDimension;
            long currentCommittedBytes = snapshot.PendingMaxDimension != 0
                ? snapshot.Backend.EstimateCommittedBytes(snapshot.SourceWidth, snapshot.SourceHeight, currentResidentSize, snapshot.Format, snapshot.SparseNumLevels, currentPageSelection)
                : snapshot.CurrentCommittedBytes;

            bool needsTransition = assignedResidentSize != currentResidentSize
                || !currentPageSelection.NearlyEquals(desiredPageSelection);
            if (!needsTransition)
                return false;

            if (snapshot.LastTransitionFrameId != long.MinValue
                && frameId - snapshot.LastTransitionFrameId < MinTransitionCooldownFrames
                && snapshot.PendingMaxDimension == 0)
            {
                return false;
            }

            bool isPromotion = assignedResidentSize > currentResidentSize
                || (assignedResidentSize == currentResidentSize && desiredPageSelection.CoverageFraction > currentPageSelection.CoverageFraction)
                || targetCommittedBytes > currentCommittedBytes;
            long deltaBytes = Math.Max(0L, targetCommittedBytes - currentCommittedBytes);
            bool visibleThisFrame = snapshot.LastVisibleFrameId == frameId || IsRecentlyBound(snapshot, frameId);
            if (isPromotion)
            {
                if (IsFailedPromotionCoolingDown(snapshot, assignedResidentSize, frameId))
                    return false;

                bool isVisiblePreviewReadyPromotion = snapshot.LastVisibleFrameId == frameId
                    && snapshot.PreviewReady
                    && assignedResidentSize > snapshot.Backend.PreviewMaxDimension;
                if (visibleWithoutPreviewCount > 0
                    && assignedResidentSize > snapshot.Backend.PreviewMaxDimension
                    && !isVisiblePreviewReadyPromotion)
                {
                    return false;
                }

                if (frameId < snapshot.Record.PromotionCooldownUntilFrameId && !visibleThisFrame)
                    return false;

                if (importsActive)
                {
                    if (!visibleThisFrame)
                        return false;

                    if (queuedPromotionBytes + deltaBytes > MaxImportEraPromotionBytesPerFrame)
                        return false;
                }

                if (queuedTransitions >= MaxResidentTransitionsPerFrame
                    || queuedPromotions >= MaxPromotionTransitionsPerFrame
                    || (queuedPromotions > 0 && queuedPromotionBytes + deltaBytes > MaxPromotionBytesPerFrame))
                {
                    return false;
                }

                if (enforceFairness
                    && !string.IsNullOrWhiteSpace(snapshot.FairnessGroupKey)
                    && promotionCountsByGroup.GetValueOrDefault(snapshot.FairnessGroupKey) > 0)
                {
                    deferredPromotions.Add(new DeferredPromotionCandidate(snapshot, assignedResidentSize, desiredPageSelection, targetCommittedBytes, pressureDemotion));
                    return false;
                }
            }
            else
            {
                if (!pressureDemotion)
                {
                    if (ShouldPreserveVulkanDenseResidentTargetWithoutPressure(
                        snapshot,
                        currentResidentSize,
                        assignedResidentSize,
                        currentCommittedBytes,
                        targetCommittedBytes,
                        currentManagedBytes,
                        availableManagedBytes))
                    {
                        return false;
                    }

                    if (importsActive)
                        return false;

                    if (frameId < snapshot.Record.DemotionCooldownUntilFrameId
                        || frameId < snapshot.Record.PinUntilFrameId)
                    {
                        return false;
                    }
                }

                if (queuedTransitions >= MaxResidentTransitionsPerFrame)
                    return false;
            }

            string reason = ResolveTransitionReason(snapshot, frameId, isPromotion, pressureDemotion, importsActive);
            if (!QueueResidentTransition(
                snapshot,
                assignedResidentSize,
                desiredPageSelection,
                frameId,
                currentCommittedBytes,
                targetCommittedBytes,
                pressureDemotion,
                reason))
            {
                return false;
            }

            queuedTransitions++;
            if (isPromotion)
            {
                queuedPromotions++;
                queuedPromotionBytes += deltaBytes;
                if (!string.IsNullOrWhiteSpace(snapshot.FairnessGroupKey))
                    promotionCountsByGroup[snapshot.FairnessGroupKey] = promotionCountsByGroup.GetValueOrDefault(snapshot.FairnessGroupKey) + 1;
            }
            else
            {
                queuedDemotions++;
            }

            return true;
        }

        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            bool visibleThisFrame = snapshot.LastVisibleFrameId == frameId;
            bool recentlyBound = IsRecentlyBound(snapshot, frameId);
            bool visibleMetricsMissing = visibleThisFrame
                && snapshot.MaxProjectedPixelSpan <= 0.0f
                && snapshot.MaxScreenCoverage <= 0.0f;
            bool useBoundFallbackMetrics = recentlyBound && (!visibleThisFrame || visibleMetricsMissing);
            long policyVisibleFrameId = visibleThisFrame || useBoundFallbackMetrics
                ? frameId
                : snapshot.LastVisibleFrameId;
            float policyProjectedPixelSpan = useBoundFallbackMetrics
                ? Math.Max(snapshot.MaxProjectedPixelSpan, BoundMaterialFallbackProjectedPixelSpan)
                : snapshot.MaxProjectedPixelSpan;
            float policyScreenCoverage = useBoundFallbackMetrics
                ? Math.Max(snapshot.MaxScreenCoverage, BoundMaterialFallbackScreenCoverage)
                : snapshot.MaxScreenCoverage;
            uint desiredResidentSize = DetermineDesiredResidentSize(
                new ImportedTextureStreamingPolicyInput(
                    snapshot.SourceWidth,
                    snapshot.SourceHeight,
                    snapshot.ResidentMaxDimension,
                    snapshot.PreviewReady,
                    policyVisibleFrameId,
                    snapshot.MinVisibleDistance,
                    policyProjectedPixelSpan,
                    policyScreenCoverage,
                    snapshot.UvDensityHint,
                    snapshot.SamplerName,
                    snapshot.LastBoundFrameId),
                frameId,
                allowPromotions,
                snapshot.Backend.PreviewMaxDimension);
            bool freezeResidentSizeForVulkan = ShouldFreezeVulkanImportedTextureResidency(snapshot);
            desiredResidentSize = ResolveVulkanSafeResidentSize(snapshot, desiredResidentSize);
            if (IsFailedPromotionCoolingDown(snapshot, desiredResidentSize, frameId))
                desiredResidentSize = snapshot.ResidentMaxDimension;

            if (allowPromotions
                && !freezeResidentSizeForVulkan
                && (visibleThisFrame || useBoundFallbackMetrics))
            {
                uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
                if (sourceMaxDimension > snapshot.Backend.PreviewMaxDimension
                    && snapshot.LastBoundMaterialTextureCount > 1)
                {
                    desiredResidentSize = Math.Max(
                        desiredResidentSize,
                        Math.Min(sourceMaxDimension, BoundMaterialRelatedMinResidentMaxDimension));
                }
            }

            SparseTextureStreamingPageSelection budgetPageSelection = DetermineDesiredPageSelection(snapshot, desiredResidentSize, frameId);

            long remainingBudget = availableManagedBytes == long.MaxValue
                ? long.MaxValue
                : Math.Max(0L, availableManagedBytes - assignedManagedBytes);
            uint assignedResidentSize = FitResidentSizeToBudget(
                new ImportedTextureStreamingBudgetInput(
                    snapshot.SourceWidth,
                    snapshot.SourceHeight,
                    snapshot.ResidentMaxDimension,
                    snapshot.Backend.PreviewMaxDimension,
                    budgetPageSelection),
                desiredResidentSize,
                remainingBudget,
                snapshot.Backend,
                snapshot.Format,
                snapshot.SparseNumLevels);
            SparseTextureStreamingPageSelection desiredPageSelection = DetermineDesiredPageSelection(snapshot, assignedResidentSize, frameId);
            long targetCommittedBytes = snapshot.Backend.EstimateCommittedBytes(
                snapshot.SourceWidth,
                snapshot.SourceHeight,
                assignedResidentSize,
                snapshot.Format,
                snapshot.SparseNumLevels,
                desiredPageSelection);
            bool pressureDemotion = assignedResidentSize < desiredResidentSize
                && targetCommittedBytes < snapshot.CurrentCommittedBytes
                && availableManagedBytes != long.MaxValue;
            if (ShouldPreserveVulkanDenseResidentSizeWithoutPressure(
                    snapshot,
                    assignedResidentSize,
                    targetCommittedBytes,
                    currentManagedBytes,
                    availableManagedBytes))
            {
                assignedResidentSize = snapshot.ResidentMaxDimension;
                desiredPageSelection = snapshot.CurrentPageSelection.Normalize(PageSelectionFullCoverageThreshold);
                targetCommittedBytes = snapshot.CurrentCommittedBytes;
                pressureDemotion = false;
            }

            TextureRuntimeDiagnostics.LogResidencyDesired(
                frameId,
                snapshot.TextureName,
                snapshot.FilePath,
                snapshot.ResidentMaxDimension,
                assignedResidentSize,
                snapshot.CurrentCommittedBytes,
                targetCommittedBytes,
                snapshot.Backend.Name,
                pressureDemotion ? "vram pressure fit" : importsActive ? "import-era policy" : "visibility policy");

            assignedManagedBytes += targetCommittedBytes;
            if (!Monitor.TryEnter(snapshot.Record.Sync))
                continue;

            try
            {
                snapshot.Record.DesiredMaxDimension = assignedResidentSize;
            }
            finally
            {
                Monitor.Exit(snapshot.Record.Sync);
            }

            _ = TryQueueCandidate(snapshot, assignedResidentSize, desiredPageSelection, targetCommittedBytes, pressureDemotion, enforceFairness: true);
        }

        for (int i = 0; i < deferredPromotions.Count; i++)
        {
            if (queuedTransitions >= MaxResidentTransitionsPerFrame || queuedPromotions >= MaxPromotionTransitionsPerFrame)
                break;

            DeferredPromotionCandidate deferred = deferredPromotions[i];
            _ = TryQueueCandidate(
                deferred.Snapshot,
                deferred.AssignedResidentSize,
                deferred.DesiredPageSelection,
                deferred.TargetCommittedBytes,
                deferred.PressureDemotion,
                enforceFairness: false);
        }

        Volatile.Write(ref _queuedTransitionsThisFrame, queuedTransitions);
        Volatile.Write(ref _queuedPromotionTransitionsThisFrame, queuedPromotions);
        Volatile.Write(ref _queuedDemotionTransitionsThisFrame, queuedDemotions);
        Volatile.Write(ref _lastTelemetryFrameId, frameId);
        Volatile.Write(ref _lastCurrentManagedBytes, currentManagedBytes);
        Volatile.Write(ref _lastAvailableManagedBytes, availableManagedBytes);
        Volatile.Write(ref _lastAssignedManagedBytes, assignedManagedBytes);

        int pendingTextureQueueDepth = XRTexture2D.QueuedProgressiveUploadCount;
        int urgentTextureRepairDepth = 0;
        double oldestTextureQueueWaitMilliseconds = 0.0;
        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            if (snapshot.PendingMaxDimension == 0)
                continue;

            pendingTextureQueueDepth++;
            if (snapshot.PendingTransitionQueuedTimestamp != 0L)
                oldestTextureQueueWaitMilliseconds = Math.Max(oldestTextureQueueWaitMilliseconds, TextureRuntimeDiagnostics.ElapsedMilliseconds(snapshot.PendingTransitionQueuedTimestamp));

            bool importantNow = snapshot.LastVisibleFrameId == frameId || IsRecentlyBound(snapshot, frameId);
            if (importantNow && (snapshot.PendingMaxDimension > snapshot.ResidentMaxDimension || snapshot.ValidationFailureCount > 0))
                urgentTextureRepairDepth++;
        }

        RenderWorkBudgetCoordinator.RecordTextureQueue(
            pendingTextureQueueDepth,
            Math.Max(oldestTextureQueueWaitMilliseconds, TextureRuntimeDiagnostics.MaxQueueWaitMilliseconds));
        RenderWorkBudgetCoordinator.RecordUrgentTextureRepairQueue(urgentTextureRepairDepth);

        if (frameId % TextureSummaryIntervalFrames == 0)
            TextureRuntimeDiagnostics.LogSummary(frameId, GetTelemetry(), GetTrackedTextureTelemetry());

        // Periodic legacy streaming diagnostics (kept in general log for early startup correlation).
        bool shouldLog = frameId % 600 == 0
            || (frameId <= 60 && frameId % 10 == 0)
            || (frameId > 60 && frameId <= 300 && frameId % 60 == 0);
        if (shouldLog)
        {
            int visibleCount = 0;
            int visibleNoPreviewCount = 0;
            int previewReadyCount = 0;
            int atPreviewCount = 0;
            int promotedCount = 0;
            int pendingCount = 0;
            int vulkanFrozenCount = 0;
            uint maxResident = 0;
            float maxPixelSpan = 0;
            for (int i = 0; i < snapshots.Count; i++)
            {
                ImportedTextureStreamingSnapshot s = snapshots[i];
                if (s.LastVisibleFrameId == frameId) visibleCount++;
                if (s.LastVisibleFrameId == frameId && !s.PreviewReady) visibleNoPreviewCount++;
                if (s.PreviewReady) previewReadyCount++;
                uint previewSize = s.Backend.PreviewMaxDimension;
                if (s.ResidentMaxDimension <= previewSize) atPreviewCount++;
                else promotedCount++;
                if (s.PendingMaxDimension != 0) pendingCount++;
                if (ShouldFreezeVulkanImportedTextureResidency(s)) vulkanFrozenCount++;
                if (s.ResidentMaxDimension > maxResident) maxResident = s.ResidentMaxDimension;
                if (s.MaxProjectedPixelSpan > maxPixelSpan) maxPixelSpan = s.MaxProjectedPixelSpan;
            }

            Debug.Textures(
                $"[TextureStreaming] frame={frameId} tracked={snapshots.Count} visible={visibleCount} " +
                $"visibleNoPreview={visibleNoPreviewCount} previewReady={previewReadyCount} atPreview={atPreviewCount} promoted={promotedCount} " +
                $"pending={pendingCount} maxResident={maxResident} maxPixelSpan={maxPixelSpan:F0} " +
                $"vulkanFrozen={vulkanFrozenCount} freezeReason='{(vulkanFrozenCount > 0 ? VulkanPreviewFreezeReason : string.Empty)}' " +
                $"allowPromotions={allowPromotions} importsActive={importsActive} activeImports={Volatile.Read(ref _activeImportedModelImports)} " +
                $"queuedTransitions={queuedTransitions} queuedPromotions={queuedPromotions} queuedDemotions={queuedDemotions} " +
                $"budget={(availableManagedBytes == long.MaxValue ? "unlimited" : $"{availableManagedBytes / (1024 * 1024)}MB")} " +
                $"vulkanAllocatorBudget='{(usingVulkanAllocatorBudget ? vulkanAllocatorBudgetReason : string.Empty)}'");
        }

        deferredPromotions.Clear();
        promotionCountsByGroup.Clear();
        snapshots.Clear();
    }

    private List<ImportedTextureStreamingSnapshot> CollectSnapshots()
        => _registry.CollectSnapshots(ResolveBackendForTexture);

    private void CollectSnapshots(List<ImportedTextureStreamingSnapshot> snapshots)
        => _registry.CollectSnapshots(ResolveBackendForTexture, snapshots);

    private static int ComparePriority(
        ImportedTextureStreamingSnapshot left,
        ImportedTextureStreamingSnapshot right,
        long frameId)
        => TextureResidencyPolicy.ComparePriority(left, right, frameId);

    private static float CalculatePriorityScore(ImportedTextureStreamingSnapshot snapshot)
        => TextureResidencyPolicy.CalculatePriorityScore(snapshot);

    internal static TextureUploadPriorityClass ResolveUploadPriorityClass(JobPriority priority)
        => TextureResidencyPolicy.ResolveUploadPriorityClass(priority);

    private static JobPriority ResolveTransitionJobPriority(
        ImportedTextureStreamingSnapshot snapshot,
        long frameId,
        uint targetResidentSize,
        uint currentResidentSize,
        long currentCommittedBytes,
        long targetCommittedBytes,
        bool pressureDemotion)
        => TextureResidencyPolicy.ResolveTransitionJobPriority(snapshot, frameId, targetResidentSize, currentResidentSize, currentCommittedBytes, targetCommittedBytes, pressureDemotion);

    private static bool IsRecentlyBound(ImportedTextureStreamingSnapshot snapshot, long frameId)
        => TextureResidencyPolicy.IsRecentlyBound(snapshot, frameId);

    private static string ResolveTransitionReason(
        ImportedTextureStreamingSnapshot snapshot,
        long frameId,
        bool isPromotion,
        bool pressureDemotion,
        bool importsActive)
        => TextureResidencyPolicy.ResolveTransitionReason(snapshot, frameId, isPromotion, pressureDemotion, importsActive);

    private static string ResolveFairnessGroupKey(string? filePath)
        => TextureResidencyPolicy.ResolveFairnessGroupKey(filePath);

    private static bool IsFailedPromotionCoolingDown(
        ImportedTextureStreamingSnapshot snapshot,
        uint targetResidentSize,
        long frameId)
    {
        if (targetResidentSize <= snapshot.ResidentMaxDimension)
            return false;

        if (snapshot.FailedTransitionCooldownUntilFrameId <= frameId)
            return false;

        uint failedTarget = snapshot.FailedTransitionTargetMaxDimension;
        return failedTarget == 0 || targetResidentSize >= failedTarget;
    }

    private static bool TryApplyVulkanAllocatorStreamingBudget(
        long currentManagedBytes,
        ref long trackedBudgetBytes,
        ref long nonManagedBytes,
        out string reason)
    {
        reason = string.Empty;
        IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
        if (host.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan)
            return false;

        VulkanRenderer? renderer = host.CurrentRenderer as VulkanRenderer
            ?? AbstractRenderer.Current as VulkanRenderer;
        if (renderer is null)
            return false;

        if (!renderer.TryGetVulkanAllocatorBudgetSnapshot(
                VulkanAllocatorStreamingBudgetRatio,
                VulkanAllocatorStreamingReserveBytes,
                out long allocatorBytes,
                out long allocatorBudgetBytes,
                out long largestHeapBytes,
                out int activeAllocationCount))
        {
            return false;
        }

        if (trackedBudgetBytes == long.MaxValue || allocatorBudgetBytes < trackedBudgetBytes)
            trackedBudgetBytes = allocatorBudgetBytes;

        long allocatorNonManagedBytes = Math.Max(0L, allocatorBytes - Math.Max(0L, currentManagedBytes));
        nonManagedBytes = Math.Max(nonManagedBytes, allocatorNonManagedBytes);
        reason =
            $"allocated={allocatorBytes}, budget={allocatorBudgetBytes}, largestHeap={largestHeapBytes}, activeVkAllocations={activeAllocationCount}";
        return true;
    }

    private static void CancelPendingLoad(CancellationTokenSource? cts)
        => TextureTransitionQueue.CancelPendingLoad(cts);

    private bool QueueResidentTransition(
        ImportedTextureStreamingSnapshot snapshot,
        uint targetResidentSize,
        SparseTextureStreamingPageSelection pageSelection,
        long frameId,
        long currentCommittedBytes,
        long targetCommittedBytes,
        bool pressureDemotion,
        string reason)
    {
        ImportedTextureStreamingRecord record = snapshot.Record;
        uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
        uint minimumResidentSize = XRTexture2D.GetMinimumResidentSize(sourceMaxDimension);
        uint normalizedTarget = Math.Max(minimumResidentSize, targetResidentSize);
        SparseTextureStreamingPageSelection normalizedPageSelection = pageSelection.Normalize(PageSelectionFullCoverageThreshold);

        if (!Monitor.TryEnter(record.Sync))
            return false;

        try
        {
            if (record.PendingSparseTransitionResult is { ExposureDeferred: true })
                return false;

            if (normalizedTarget == record.PendingMaxDimension
                && record.PendingPageSelection.NearlyEquals(normalizedPageSelection))
            {
                TextureRuntimeDiagnostics.LogTransitionCoalesced(
                    frameId,
                    snapshot.TextureName,
                    snapshot.FilePath,
                    normalizedTarget,
                    snapshot.Backend.Name,
                    "identical pending transition");
                return false;
            }

            if (record.PendingMaxDimension == 0
                && normalizedTarget == record.ResidentMaxDimension
                && snapshot.CurrentPageSelection.NearlyEquals(normalizedPageSelection))
            {
                TextureRuntimeDiagnostics.LogTransitionCoalesced(
                    frameId,
                    snapshot.TextureName,
                    snapshot.FilePath,
                    normalizedTarget,
                    snapshot.Backend.Name,
                    "already resident");
                return false;
            }
        }
        finally
        {
            Monitor.Exit(record.Sync);
        }

        if (!record.Texture.TryGetTarget(out XRTexture2D? texture))
            return false;

        string? filePath;
        ITextureStreamingSource? source;
        ITextureResidencyBackend backend;
        CancellationTokenSource cts = new();
        CancellationTokenSource? previousPendingLoadCts = null;
        bool includeMipChain;
        uint previousResidentSize;
        JobPriority transitionPriority = JobPriority.Low;
        TextureUploadPriorityClass uploadPriorityClass = TextureUploadPriorityClass.Background;
        lock (record.Sync)
        {
            filePath = record.FilePath;
            source = record.Source;
            backend = record.Backend ?? ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format);
            includeMipChain = ShouldIncludeResidentMipChain(backend, normalizedTarget);
            previousResidentSize = record.PendingMaxDimension != 0
                ? record.PendingMaxDimension
                : record.ResidentMaxDimension;
            transitionPriority = ResolveTransitionJobPriority(
                snapshot,
                frameId,
                normalizedTarget,
                previousResidentSize,
                currentCommittedBytes,
                targetCommittedBytes,
                pressureDemotion);
            uploadPriorityClass = ResolveUploadPriorityClass(transitionPriority);
            if (record.PendingLoadCts is not null)
            {
                TextureRuntimeDiagnostics.LogTransitionCoalesced(
                    frameId,
                    texture.Name,
                    filePath,
                    normalizedTarget,
                    backend.Name,
                    "superseded pending transition");
            }
        }

        if (!_transitionQueue.TryBeginTransition(
                record,
                cts,
                normalizedTarget,
                normalizedPageSelection,
                frameId,
                pressureDemotion,
                previousResidentSize,
                currentCommittedBytes,
                targetCommittedBytes,
                backend.Name,
                reason,
                transitionPriority,
                uploadPriorityClass,
                out previousPendingLoadCts))
        {
            cts.Dispose();
            return false;
        }

        CancelPendingLoad(previousPendingLoadCts);

        bool IsCurrentTransition()
        {
            lock (record.Sync)
                return ReferenceEquals(record.PendingLoadCts, cts) && !cts.IsCancellationRequested;
        }

        long streamingGeneration;
        lock (record.Sync)
            streamingGeneration = record.UploadGeneration;

        if (string.IsNullOrWhiteSpace(filePath) || source is null)
        {
            TextureRuntimeDiagnostics.LogTransitionCanceled(
                frameId,
                texture.Name,
                filePath,
                normalizedTarget,
                backend.Name,
                "missing streaming source");
            ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId);
            return false;
        }

        TextureRuntimeDiagnostics.LogTransitionQueued(
            frameId,
            texture.Name,
            filePath,
            previousResidentSize,
            normalizedTarget,
            currentCommittedBytes,
            targetCommittedBytes,
            backend.Name,
            reason,
            transitionPriority,
            uploadPriorityClass,
            snapshot.MaxProjectedPixelSpan,
            snapshot.MaxScreenCoverage,
            snapshot.LastVisibleFrameId == frameId,
            IsRecentlyBound(snapshot, frameId));

        backend.ScheduleResidentLoad(
            source,
            texture,
            normalizedTarget,
            includeMipChain,
            normalizedPageSelection,
            residentData =>
            {
                lock (record.Sync)
                {
                    if (!ReferenceEquals(record.PendingLoadCts, cts))
                        return;

                    record.Format = ESizedInternalFormat.Rgba8;
                    record.SourceWidth = residentData.SourceWidth;
                    record.SourceHeight = residentData.SourceHeight;
                    record.Backend = ResolveBackendForTexture(residentData.SourceWidth, residentData.SourceHeight, record.Format);
                    record.PendingTransitionTargetCommittedBytes = record.Backend.EstimateCommittedBytes(
                        residentData.SourceWidth,
                        residentData.SourceHeight,
                        normalizedTarget,
                        record.Format,
                        record.SparseNumLevels,
                        normalizedPageSelection);
                }
            },
            (request, transitionResult) =>
            {
                lock (record.Sync)
                {
                    if (!ReferenceEquals(record.PendingLoadCts, cts))
                        return;

                    record.PendingSparseTransitionRequest = request;
                    record.PendingSparseTransitionResult = transitionResult;
                }
            },
            tex => ClearPendingTransition(record, cts, tex, normalizedTarget, frameId),
            ex =>
            {
                if (VulkanRenderer.IsExpectedVulkanImageAllocationDeferral(ex))
                {
                    ClearPendingTransition(
                        record,
                        cts,
                        null,
                        completedResidentSize: 0,
                        frameId,
                        cancelReason: $"Vulkan allocator pressure deferred retry: {ex.Message}");
                    return;
                }

                ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId, failed: true);
                RuntimeRenderingHostServices.Current.LogException(ex, $"Failed to stream imported texture '{filePath}' to resident size {normalizedTarget}.");
            },
            () => ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId),
            shouldAcceptResult: IsCurrentTransition,
            cancellationToken: cts.Token,
            priority: transitionPriority,
            streamingGeneration: streamingGeneration);

        return true;
    }

    private static uint ResolveVulkanSafeResidentSize(
        ImportedTextureStreamingSnapshot snapshot,
        uint desiredResidentSize)
    {
        if (RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan)
            return desiredResidentSize;

        // See docs/work/todo/rendering/vulkan-imported-texture-streaming-todo.md.
        // The preview freeze remains available as an explicit emergency kill
        // switch for device-loss isolation.
        if (!ShouldFreezeVulkanImportedTextureResidency(snapshot))
            return desiredResidentSize;

        uint currentResidentSize = snapshot.PendingMaxDimension != 0
            ? snapshot.PendingMaxDimension
            : snapshot.ResidentMaxDimension;
        if (currentResidentSize != 0)
            return currentResidentSize;

        uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
        return sourceMaxDimension == 0
            ? snapshot.Backend.PreviewMaxDimension
            : Math.Min(sourceMaxDimension, snapshot.Backend.PreviewMaxDimension);
    }

    private static bool ShouldFreezeVulkanImportedTextureResidency(ImportedTextureStreamingSnapshot snapshot)
        => RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan
            && snapshot.PreviewReady
            && IsVulkanImportedTexturePreviewFreezeForced();

    private static bool IsVulkanImportedTexturePreviewFreezeForced()
        => RenderDiagnosticsFlags.VkImportedTexturePreviewFreeze;

    private static string ResolveVulkanFreezeReason(ImportedTextureStreamingSnapshot snapshot)
    {
        if (RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan)
            return string.Empty;

        if (!snapshot.PreviewReady)
            return "preview not ready";

        if (IsVulkanImportedTexturePreviewFreezeForced())
            return $"{VulkanImportedTexturePreviewFreezeEnvVar}=1";

        return string.Empty;
    }

    private static string ResolveTelemetryBackendName(ITextureResidencyBackend backend)
    {
        if (string.Equals(backend.Name, nameof(VulkanDenseTextureResidencyBackend), StringComparison.Ordinal))
            return "Vulkan dense residency (compat, synchronized upload)";

        if (RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan
            && string.Equals(backend.Name, nameof(GLTieredTextureResidencyBackend), StringComparison.Ordinal))
        {
            return "Vulkan dense tiered (legacy GL compat backend)";
        }

        return backend.Name;
    }

    private static bool ShouldPreserveVulkanDenseResidentSizeWithoutPressure(
        ImportedTextureStreamingSnapshot snapshot,
        uint assignedResidentSize,
        long targetCommittedBytes,
        long currentManagedBytes,
        long availableManagedBytes)
        => RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan
            && string.Equals(snapshot.Backend.Name, nameof(VulkanDenseTextureResidencyBackend), StringComparison.Ordinal)
            && ShouldPreserveDenseResidentSizeWithoutPressure(
                snapshot.ResidentMaxDimension,
                assignedResidentSize,
                snapshot.CurrentCommittedBytes,
                targetCommittedBytes,
                currentManagedBytes,
                availableManagedBytes,
                snapshot.PendingMaxDimension != 0);

    private static bool ShouldPreserveVulkanDenseResidentTargetWithoutPressure(
        ImportedTextureStreamingSnapshot snapshot,
        uint currentResidentSize,
        uint assignedResidentSize,
        long currentCommittedBytes,
        long targetCommittedBytes,
        long currentManagedBytes,
        long availableManagedBytes)
        => RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan
            && string.Equals(snapshot.Backend.Name, nameof(VulkanDenseTextureResidencyBackend), StringComparison.Ordinal)
            && ShouldPreserveDenseResidentTargetWithoutPressure(
                currentResidentSize,
                assignedResidentSize,
                currentCommittedBytes,
                targetCommittedBytes,
                currentManagedBytes,
                availableManagedBytes);

    internal static bool ShouldPreserveDenseResidentTargetWithoutPressure(
        uint currentResidentSize,
        uint assignedResidentSize,
        long currentCommittedBytes,
        long targetCommittedBytes,
        long currentManagedBytes,
        long availableManagedBytes)
        => ShouldPreserveDenseResidentSizeWithoutPressure(
            currentResidentSize,
            assignedResidentSize,
            currentCommittedBytes,
            targetCommittedBytes,
            currentManagedBytes,
            availableManagedBytes,
            hasPendingTransition: false);

    internal static bool ShouldPreserveDenseResidentSizeWithoutPressure(
        uint currentResidentSize,
        uint assignedResidentSize,
        long currentCommittedBytes,
        long targetCommittedBytes,
        long currentManagedBytes,
        long availableManagedBytes,
        bool hasPendingTransition)
    {
        if (hasPendingTransition || currentResidentSize == 0 || currentCommittedBytes <= targetCommittedBytes)
            return false;

        if (assignedResidentSize >= currentResidentSize)
            return false;

        if (availableManagedBytes == long.MaxValue)
            return true;

        if (availableManagedBytes <= 0L || currentManagedBytes < 0L)
            return false;

        return currentManagedBytes <= availableManagedBytes * VulkanDenseNonPressureDemotionPreserveBudgetFillRatio;
    }

    private static bool ShouldIncludeResidentMipChain(ITextureResidencyBackend backend, uint normalizedTarget)
    {
        if (normalizedTarget <= backend.PreviewMaxDimension)
            return false;

        // Vulkan dense imported-texture uploads include the resident mip chain
        // only after the synchronized service owns frame-timeline upload and
        // publication; XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1 stays
        // experimental and does not bypass this service boundary.
        return RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan
            || VulkanTextureUploadService.IsSynchronizedImportedTextureStreamingAvailable;
    }

    private static void ClearPendingTransition(
        ImportedTextureStreamingRecord record,
        CancellationTokenSource cts,
        XRTexture2D? texture,
        uint completedResidentSize,
        long frameId,
        bool failed = false,
        string? cancelReason = null)
    {
        float targetLodBias = 0.0f;
        bool shouldApplyLodBias = false;
        float textureLodBias = texture?.LodBias ?? 0.0f;
        int sparseNumLevels = texture?.SparseTextureStreamingNumSparseLevels ?? 0;
        uint previousResidentSize = 0;
        uint pendingResidentSize = 0;
        long pendingPreviousBytes = 0L;
        long pendingTargetBytes = 0L;
        bool pressureDemotion = false;
        string backendName = string.Empty;
        string reason = string.Empty;
        JobPriority transitionPriority = JobPriority.Low;
        TextureUploadPriorityClass uploadPriorityClass = TextureUploadPriorityClass.Background;
        double queueWaitMilliseconds = 0.0;
        double lifecycleMilliseconds = 0.0;
        string? textureName = texture?.Name;
        string? filePath;
        lock (record.Sync)
        {
            if (!ReferenceEquals(record.PendingLoadCts, cts))
            {
                cts.Dispose();
                return;
            }

            filePath = record.FilePath;
            previousResidentSize = record.ResidentMaxDimension;
            pendingResidentSize = record.PendingMaxDimension;
            pendingPreviousBytes = record.PendingTransitionPreviousCommittedBytes;
            pendingTargetBytes = record.PendingTransitionTargetCommittedBytes;
            pressureDemotion = record.PendingTransitionWasPressureDemotion;
            backendName = string.IsNullOrWhiteSpace(record.PendingTransitionBackendName)
                ? record.Backend?.Name ?? "Unknown"
                : record.PendingTransitionBackendName;
            reason = record.PendingTransitionReason;
            transitionPriority = record.PendingTransitionPriority;
            uploadPriorityClass = record.PendingTransitionUploadPriorityClass;
            if (record.PendingTransitionQueuedTimestamp != 0L)
            {
                queueWaitMilliseconds = TextureRuntimeDiagnostics.ElapsedMilliseconds(record.PendingTransitionQueuedTimestamp);
                lifecycleMilliseconds = queueWaitMilliseconds;
                record.LastTransitionQueueWaitMilliseconds = queueWaitMilliseconds;
                record.LastTransitionExecutionMilliseconds = lifecycleMilliseconds;
            }

            if (completedResidentSize > 0)
            {
                long previousPublishedGeneration = record.PublishedGeneration;
                record.ResidentMaxDimension = completedResidentSize;
                record.ResidentGeneration = Math.Max(record.ResidentGeneration + 1L, record.UploadGeneration);
                record.PublishedGeneration = record.ResidentGeneration;
                record.FailedTransitionCooldownUntilFrameId = long.MinValue;
                record.FailedTransitionTargetMaxDimension = 0;
                record.ConsecutiveTransitionFailureCount = 0;
                if (previousPublishedGeneration > 0 && previousPublishedGeneration != record.PublishedGeneration)
                    record.RetirementGeneration = previousPublishedGeneration;
                record.PreviewReady = true;
                if (completedResidentSize > previousResidentSize)
                {
                    record.PromotionCooldownUntilFrameId = frameId + PromotionCooldownFrames;
                    record.PinUntilFrameId = frameId + VisiblePromotionPinFrames;
                }
                else if (completedResidentSize < previousResidentSize)
                {
                    record.DemotionCooldownUntilFrameId = frameId + DemotionCooldownFrames;
                    if (pressureDemotion)
                        record.LastPressureDemotionFrameId = frameId;
                }

                if (texture is not null && ShouldApplySamplerLodBiasPromotionFade())
                {
                    float currentStreamingLodBias = Math.Max(0.0f, record.CurrentStreamingLodBias);
                    record.BaseLodBias = textureLodBias - currentStreamingLodBias;
                    if (completedResidentSize > previousResidentSize)
                    {
                        float promotionFadeBias = CalculatePromotionFadeBias(
                            record.SourceWidth,
                            record.SourceHeight,
                            previousResidentSize,
                            completedResidentSize);
                        float startBias = currentStreamingLodBias + promotionFadeBias;
                        if (startBias > 0.0f)
                        {
                            record.CurrentStreamingLodBias = startBias;
                            record.PromotionFadeStartBias = startBias;
                            record.PromotionFadeStartFrameId = frameId;
                            Volatile.Write(ref record.PromotionFadeEndFrameId, frameId + PromotionFadeFrames);
                            targetLodBias = record.BaseLodBias + startBias;
                            shouldApplyLodBias = true;
                        }
                        else
                        {
                            ClearPromotionFadeState(record);
                            targetLodBias = record.BaseLodBias;
                            shouldApplyLodBias = true;
                        }
                    }
                    else
                    {
                        ClearPromotionFadeState(record);
                        targetLodBias = record.BaseLodBias;
                        shouldApplyLodBias = true;
                    }
                }
                else
                {
                    ClearPromotionFadeState(record);
                }
            }
            else if (failed)
            {
                RecordFailedTransitionAfterLock(record, frameId, previousResidentSize, pendingResidentSize);
            }

            if (texture is not null)
                record.SparseNumLevels = sparseNumLevels;

            TextureTransitionQueue.ClearPendingStateAfterLock(record);
            record.LastTransitionFrameId = frameId;
        }

        if (texture is not null && shouldApplyLodBias && !NearlyEquals(texture.LodBias, targetLodBias))
            texture.LodBias = targetLodBias;

        if (texture is not null)
        {
            double activeUploadMilliseconds = Math.Max(0.0, texture.LastTextureUploadDurationMilliseconds);
            texture.RecordTextureQueueWait(queueWaitMilliseconds);
            texture.RecordTextureUploadDuration(activeUploadMilliseconds);
        }

        if (completedResidentSize > 0)
        {
            TextureRuntimeDiagnostics.LogTransitionApplied(
                frameId,
                textureName,
                filePath,
                previousResidentSize,
                completedResidentSize,
                pendingTargetBytes,
                queueWaitMilliseconds,
                texture is null ? 0.0 : Math.Max(0.0, texture.LastTextureUploadDurationMilliseconds),
                lifecycleMilliseconds,
                backendName,
                transitionPriority,
                uploadPriorityClass);

            if (pressureDemotion && completedResidentSize < previousResidentSize)
            {
                TextureRuntimeDiagnostics.LogVramPressure(
                    frameId,
                    textureName,
                    filePath,
                    Math.Max(0L, pendingPreviousBytes - pendingTargetBytes),
                    RuntimeRenderingHostServices.Current.TrackedVramBytes,
                    RuntimeRenderingHostServices.Current.TrackedVramBudgetBytes,
                    string.IsNullOrWhiteSpace(reason) ? "vram pressure demotion" : reason);
            }
        }
        else
        {
            TextureRuntimeDiagnostics.LogTransitionCanceled(
                frameId,
                textureName,
                filePath,
                pendingResidentSize,
                backendName,
                cancelReason ?? (failed ? "transition failed before completion" : "transition canceled before completion"));
        }

        cts.Dispose();
    }

    private static void RecordFailedTransitionAfterLock(
        ImportedTextureStreamingRecord record,
        long frameId,
        uint previousResidentSize,
        uint pendingResidentSize)
    {
        if (pendingResidentSize == 0 || pendingResidentSize <= previousResidentSize)
            return;

        int failureCount = Math.Clamp(record.ConsecutiveTransitionFailureCount + 1, 1, 16);
        int backoffMultiplier = 1 << Math.Min(failureCount - 1, 3);
        int cooldownFrames = Math.Min(MaxFailedPromotionCooldownFrames, FailedPromotionCooldownFrames * backoffMultiplier);

        record.ConsecutiveTransitionFailureCount = failureCount;
        record.FailedTransitionTargetMaxDimension = pendingResidentSize;
        record.FailedTransitionCooldownUntilFrameId = frameId + cooldownFrames;
        record.PromotionCooldownUntilFrameId = Math.Max(record.PromotionCooldownUntilFrameId, frameId + cooldownFrames);
        record.DesiredMaxDimension = previousResidentSize;
    }

    private static void ClearPromotionFadeState(ImportedTextureStreamingRecord record)
    {
        record.CurrentStreamingLodBias = 0.0f;
        record.PromotionFadeStartBias = 0.0f;
        record.PromotionFadeStartFrameId = long.MinValue;
        Volatile.Write(ref record.PromotionFadeEndFrameId, long.MinValue);
    }

    private static bool ShouldApplySamplerLodBiasPromotionFade()
        => RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan;

    private static bool NearlyEquals(float left, float right, float epsilon = 0.001f)
        => MathF.Abs(left - right) <= epsilon;
}
