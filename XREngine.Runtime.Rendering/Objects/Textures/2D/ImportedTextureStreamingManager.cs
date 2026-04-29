using ImageMagick;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

internal readonly record struct TextureStreamingResidentData(
    Mipmap2D[] Mipmaps,
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension);

public readonly record struct ImportedTextureStreamingUsage(
    float DistanceFromCamera,
    float ProjectedPixelSpan = 0.0f,
    float ScreenCoverage = 0.0f,
    float UvDensityHint = 1.0f,
    SparseTextureStreamingPageSelection PageSelection = default)
{
    public float ClampedDistance => MathF.Max(0.0f, DistanceFromCamera);
    public float ClampedProjectedPixelSpan => MathF.Max(0.0f, ProjectedPixelSpan);
    public float ClampedScreenCoverage => Math.Clamp(ScreenCoverage, 0.0f, 1.0f);
    public float NormalizedUvDensityHint
        => float.IsFinite(UvDensityHint)
            ? Math.Clamp(UvDensityHint, 0.5f, 2.0f)
            : 1.0f;

    public SparseTextureStreamingPageSelection NormalizedPageSelection => PageSelection.Normalize();
}

internal readonly record struct ImportedTextureStreamingPolicyInput(
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    bool PreviewReady,
    long LastVisibleFrameId,
    float MinVisibleDistance,
    float MaxProjectedPixelSpan,
    float MaxScreenCoverage,
    float UvDensityHint,
    string? SamplerName);

internal readonly record struct ImportedTextureStreamingBudgetInput(
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    uint PreviewMaxDimension,
    SparseTextureStreamingPageSelection PageSelection);

public readonly record struct ImportedTextureStreamingTelemetry(
    string BackendName,
    int ActiveImportScopes,
    int TrackedTextureCount,
    int PendingTransitionCount,
    int ActiveDecodeCount,
    int QueuedDecodeCount,
    int QueuedTransitionsThisFrame,
    long LastFrameId,
    long CurrentManagedBytes,
    long AvailableManagedBytes,
    long AssignedManagedBytes,
    long UploadBytesScheduledThisFrame,
    bool PromotionsBlocked);

public readonly record struct ImportedTextureStreamingTextureTelemetry(
    string? TextureName,
    string? FilePath,
    string? SamplerName,
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    uint DesiredResidentMaxDimension,
    uint PendingResidentMaxDimension,
    long LastVisibleFrameId,
    float MinVisibleDistance,
    float MaxProjectedPixelSpan,
    float MaxScreenCoverage,
    float UvDensityHint,
    float CurrentPageCoverage,
    float DesiredPageCoverage,
    bool PreviewReady,
    long CurrentCommittedBytes,
    long DesiredCommittedBytes);

internal interface ITextureStreamingSource
{
    string SourcePath { get; }
    TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain);
}

internal interface ITextureResidencyBackend
{
    string Name { get; }
    uint PreviewMaxDimension { get; }
    int ActiveDecodeCount { get; }
    int QueuedDecodeCount { get; }
    int ActiveGpuUploadCount { get; }
    long UploadBytesScheduledThisFrame { get; }
    long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0, SparseTextureStreamingPageSelection pageSelection = default);
    uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize);
    EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low);
    EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low);
}

internal static class TextureStreamingSourceFactory
{
    internal static ITextureStreamingSource Create(string filePath)
    {
        string authorityPath = XRTexture2D.ResolveTextureStreamingAuthorityPathInternal(filePath, out string? originalSourcePath);

        if (XRTexture2D.HasAssetExtensionInternal(authorityPath))
            return new AssetTextureStreamingSource(authorityPath, originalSourcePath);

        return new ThirdPartyTextureStreamingSource(authorityPath);
    }
}

internal sealed class AssetTextureStreamingSource(string assetPath, string? fallbackSourcePath = null) : ITextureStreamingSource
{
    private readonly ThirdPartyTextureStreamingSource? _fallbackSource = string.IsNullOrWhiteSpace(fallbackSourcePath)
        ? null
        : new ThirdPartyTextureStreamingSource(Path.GetFullPath(fallbackSourcePath));
    private int _preferFallback;

    public string SourcePath => assetPath;

    public TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain)
    {
        if (Volatile.Read(ref _preferFallback) != 0 && _fallbackSource is not null)
            return _fallbackSource.LoadResidentData(maxResidentDimension, includeMipChain);

        try
        {
            byte[] assetBytes = RuntimeRenderingHostServices.Current.ReadAllBytes(assetPath);
            if (XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(assetBytes, maxResidentDimension, includeMipChain, out TextureStreamingResidentData residentData))
                return residentData;

            XRTexture2D? scratch = RuntimeRenderingHostServices.Current.LoadAsset<XRTexture2D>(assetPath);
            if (scratch is not null)
                return XRTexture2D.BuildResidentDataFromLoadedTexture(scratch, maxResidentDimension, includeMipChain);
        }
        catch (Exception ex) when (_fallbackSource is not null)
        {
            return LoadFallbackResidentData(maxResidentDimension, includeMipChain, ex);
        }

        if (_fallbackSource is not null)
            return LoadFallbackResidentData(maxResidentDimension, includeMipChain, failure: null);

        throw new FileNotFoundException($"Failed to load texture asset '{assetPath}'.", assetPath);
    }

    private TextureStreamingResidentData LoadFallbackResidentData(uint maxResidentDimension, bool includeMipChain, Exception? failure)
    {
        if (_fallbackSource is null)
            throw failure ?? new FileNotFoundException($"Failed to load texture asset '{assetPath}'.", assetPath);

        if (Interlocked.Exchange(ref _preferFallback, 1) == 0)
        {
            string reason = failure is null
                ? "the cached texture asset was unreadable"
                : $"{failure.GetType().Name}: {failure.Message}";
            RuntimeRenderingHostServices.Current.LogWarning(
                $"Falling back to source texture '{_fallbackSource.SourcePath}' because cache asset '{assetPath}' could not be used ({reason}).");
        }

        return _fallbackSource.LoadResidentData(maxResidentDimension, includeMipChain);
    }
}

internal sealed class ThirdPartyTextureStreamingSource(string sourcePath) : ITextureStreamingSource
{
    public string SourcePath => sourcePath;

    public TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            using MagickImage filler = (MagickImage)XRTexture2D.FillerImage.Clone();
            return XRTexture2D.BuildResidentDataFromImage(filler, maxResidentDimension, includeMipChain);
        }

        long decodeStartTimestamp = XRTexture2D.StartImportedTextureTiming();
        using MagickImage sourceImage = new(sourcePath);
        double decodeMilliseconds = XRTexture2D.CompleteImportedTextureTiming(decodeStartTimestamp);
        return XRTexture2D.BuildResidentDataFromImage(sourceImage, maxResidentDimension, includeMipChain, sourcePath, decodeMilliseconds);
    }
}

internal sealed class GLTieredTextureResidencyBackend : ITextureResidencyBackend
{
    private const int MaxConcurrentStreamingDecodes = 2;
    private const int MaxRenderThreadApplyResidentDataPerFrame = 1;

    private static readonly uint[] ResidentCandidates =
    [
        64u,
        128u,
        256u,
        512u,
        1024u,
        2048u,
        4096u,
        8192u,
    ];

    private static readonly SemaphoreSlim DecodeGate = new(MaxConcurrentStreamingDecodes, MaxConcurrentStreamingDecodes);

    private static int s_activeDecodeCount;
    private static int s_queuedDecodeCount;
    private static long s_uploadBytesFrameTicks = -1;
    private static long s_uploadBytesScheduledThisFrame;
    private static long s_applyResidentDataFrameTicks = -1;
    private static int s_applyResidentDataCountThisFrame;

    public string Name => nameof(GLTieredTextureResidencyBackend);
    public uint PreviewMaxDimension => XRTexture2D.ImportedPreviewMaxDimensionInternal;
    public int ActiveDecodeCount => Volatile.Read(ref s_activeDecodeCount);
    public int QueuedDecodeCount => Volatile.Read(ref s_queuedDecodeCount);
    public int ActiveGpuUploadCount => XRTexture2D.QueuedProgressiveUploadCount;
    public long UploadBytesScheduledThisFrame => GetUploadBytesScheduledThisFrame();

    public long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0, SparseTextureStreamingPageSelection pageSelection = default)
        => XRTexture2D.EstimateResidentBytes(sourceWidth, sourceHeight, residentMaxDimension, format);

    public uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize)
    {
        uint minimumResidentSize = XRTexture2D.GetPreviewResidentSize(sourceMaxDimension);
        if (currentResidentSize <= minimumResidentSize)
            return minimumResidentSize;

        for (int index = ResidentCandidates.Length - 1; index >= 0; index--)
        {
            uint candidate = ResidentCandidates[index];
            if (candidate >= currentResidentSize)
                continue;
            if (sourceMaxDimension != 0 && candidate > sourceMaxDimension)
                continue;

            return Math.Max(candidate, minimumResidentSize);
        }

        return minimumResidentSize;
    }

    public EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            PreviewMaxDimension,
            includeMipChain: false,
            SparseTextureStreamingPageSelection.Full,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress);

    public EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            maxResidentDimension,
            includeMipChain,
            pageSelection,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress: null);

    private static EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled,
        Func<bool>? shouldAcceptResult,
        CancellationToken cancellationToken,
        JobPriority priority,
        Action<float>? onProgress)
    {
        Interlocked.Increment(ref s_queuedDecodeCount);

        Task loadTask = Task.Run(async () =>
        {
            bool decodeActivated = false;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onCanceled?.Invoke();
                    return;
                }

                await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Decrement(ref s_queuedDecodeCount);
                Interlocked.Increment(ref s_activeDecodeCount);
                decodeActivated = true;

                TextureStreamingResidentData residentData;
                try
                {
                    residentData = source.LoadResidentData(maxResidentDimension, includeMipChain);
                }
                finally
                {
                    if (decodeActivated)
                    {
                        Interlocked.Decrement(ref s_activeDecodeCount);
                        DecodeGate.Release();
                    }
                }

                void ApplyResidentDataOnRenderThreadCore()
                {
                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    onPrepared?.Invoke(residentData);

                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    XRTexture2D.ApplyResidentData(target, residentData, includeMipChain);
                    onProgress?.Invoke(0.5f);

                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    long uploadBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);
                    XRTexture2D.ScheduleGpuUploadInternal(target, cancellationToken, () =>
                    {
                        if (cancellationToken.IsCancellationRequested
                            || (shouldAcceptResult is not null && !shouldAcceptResult()))
                        {
                            onCanceled?.Invoke();
                            return;
                        }

                        onProgress?.Invoke(1.0f);
                        onFinished?.Invoke(target);
                    });
                }

                bool ApplyResidentDataOnRenderThreadBudgeted()
                {
                    if (!TryEnterRenderThreadApplyBudget())
                        return false;

                    ApplyResidentDataOnRenderThreadCore();
                    return true;
                }

                if (RuntimeRenderingHostServices.Current.IsRenderThread && TryEnterRenderThreadApplyBudget())
                    ApplyResidentDataOnRenderThreadCore();
                else
                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
                        ApplyResidentDataOnRenderThreadBudgeted,
                        $"TextureStreaming.ApplyResidentData[{target.Name}]");
            }
            catch (OperationCanceledException)
            {
                onCanceled?.Invoke();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                if (!decodeActivated)
                {
                    int remaining = Interlocked.Decrement(ref s_queuedDecodeCount);
                    if (remaining < 0)
                        Interlocked.Exchange(ref s_queuedDecodeCount, 0);
                }
            }
        }, cancellationToken);

        IEnumerable Routine()
        {
            yield return loadTask;
        }

        return RuntimeRenderingHostServices.Current.ScheduleEnumeratorJob(
            Routine,
            priority,
            cancellationToken: CancellationToken.None);
    }

    private static bool TryEnterRenderThreadApplyBudget()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_applyResidentDataFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_applyResidentDataCountThisFrame, 0);

        while (true)
        {
            int currentCount = Volatile.Read(ref s_applyResidentDataCountThisFrame);
            if (currentCount >= MaxRenderThreadApplyResidentDataPerFrame)
                return false;

            if (Interlocked.CompareExchange(ref s_applyResidentDataCountThisFrame, currentCount + 1, currentCount) == currentCount)
                return true;
        }
    }

    private static void RecordUploadBytes(long bytes)
    {
        if (bytes <= 0)
            return;

        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_uploadBytesFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_uploadBytesScheduledThisFrame, 0L);

        _ = Interlocked.Add(ref s_uploadBytesScheduledThisFrame, bytes);
    }

    private static long GetUploadBytesScheduledThisFrame()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Volatile.Read(ref s_uploadBytesFrameTicks);
        if (previousFrame != currentFrame)
            return 0L;

        return Volatile.Read(ref s_uploadBytesScheduledThisFrame);
    }
}

internal sealed class GLSparseTextureResidencyBackend : ITextureResidencyBackend
{
    private const int MaxConcurrentStreamingDecodes = 2;
    private const int MaxQueuedDecodeBacklog = 8;
    private const int MaxSharedUploadsInFlight = 2;
    private const int MaxSparseTransitionSchedulesPerFrame = 1;
    private static readonly uint[] ResidentCandidates =
    [
        64u,
        128u,
        256u,
        512u,
        1024u,
        2048u,
        4096u,
        8192u,
    ];

    private static readonly SemaphoreSlim DecodeGate = new(MaxConcurrentStreamingDecodes, MaxConcurrentStreamingDecodes);

    private static int s_activeDecodeCount;
    private static int s_queuedDecodeCount;
    private static int s_inFlightUploadCount;
    private static long s_uploadBytesFrameTicks = -1;
    private static long s_uploadBytesScheduledThisFrame;
    private static long s_sparseTransitionScheduleFrameTicks = -1;
    private static int s_sparseTransitionScheduleCountThisFrame;

    public string Name => nameof(GLSparseTextureResidencyBackend);
    public uint PreviewMaxDimension => XRTexture2D.ImportedPreviewMaxDimensionInternal;
    public int ActiveDecodeCount => Volatile.Read(ref s_activeDecodeCount);
    public int QueuedDecodeCount => Volatile.Read(ref s_queuedDecodeCount);
    public int ActiveGpuUploadCount => Volatile.Read(ref s_inFlightUploadCount);
    public long UploadBytesScheduledThisFrame => GetUploadBytesScheduledThisFrame();

    public long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0, SparseTextureStreamingPageSelection pageSelection = default)
    {
        if (residentMaxDimension == 0)
            return 0L;

        int logicalMipCount = XRTexture2D.GetLogicalMipCount(sourceWidth, sourceHeight);
        if (logicalMipCount <= 0)
            return 0L;

        int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(sourceWidth, sourceHeight, residentMaxDimension);
        SparseTextureStreamingSupport support = RuntimeRenderingHostServices.Current.GetSparseTextureStreamingSupport(format);
        return XRTexture2D.EstimateSparsePageSelectionBytes(
            sourceWidth,
            sourceHeight,
            requestedBaseMipLevel,
            logicalMipCount,
            sparseNumLevels,
            support,
            pageSelection,
            format);
    }

    public uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize)
    {
        uint minimumResidentSize = XRTexture2D.GetPreviewResidentSize(sourceMaxDimension);
        if (currentResidentSize <= minimumResidentSize)
            return minimumResidentSize;

        for (int index = ResidentCandidates.Length - 1; index >= 0; index--)
        {
            uint candidate = ResidentCandidates[index];
            if (candidate >= currentResidentSize)
                continue;
            if (sourceMaxDimension != 0 && candidate > sourceMaxDimension)
                continue;

            return Math.Max(candidate, minimumResidentSize);
        }

        return minimumResidentSize;
    }

    public EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            PreviewMaxDimension,
            includeMipChain: true,
            SparseTextureStreamingPageSelection.Full,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress);

    public EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            maxResidentDimension,
            includeMipChain: true,
            pageSelection,
            onPrepared,
            onDeferred,
            onFinished,
            onError,
            onCanceled,
            shouldAcceptResult,
            cancellationToken,
            priority,
            onProgress: null);

    private static EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled,
        Func<bool>? shouldAcceptResult,
        CancellationToken cancellationToken,
        JobPriority priority,
        Action<float>? onProgress)
    {
        Interlocked.Increment(ref s_queuedDecodeCount);

        Task loadTask = Task.Run(async () =>
        {
            bool decodeActivated = false;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onCanceled?.Invoke();
                    return;
                }

                if (Volatile.Read(ref s_queuedDecodeCount) > MaxQueuedDecodeBacklog)
                {
                    onCanceled?.Invoke();
                    return;
                }

                await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Decrement(ref s_queuedDecodeCount);
                Interlocked.Increment(ref s_activeDecodeCount);
                decodeActivated = true;

                TextureStreamingResidentData residentData;
                try
                {
                    residentData = source.LoadResidentData(maxResidentDimension, includeMipChain: true);
                }
                finally
                {
                    if (decodeActivated)
                    {
                        Interlocked.Decrement(ref s_activeDecodeCount);
                        DecodeGate.Release();
                    }
                }

                void ContinueOnRenderThreadCore()
                {
                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    onPrepared?.Invoke(residentData);
                    onProgress?.Invoke(0.5f);

                    if (cancellationToken.IsCancellationRequested
                        || (shouldAcceptResult is not null && !shouldAcceptResult()))
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    long uploadBytes = XRTexture2D.CalculateResidentUploadBytes(residentData);

                    SparseTextureStreamingTransitionRequest transitionRequest = BuildSparseTransitionRequest(residentData, pageSelection);

                    void CompleteSparseTransition(SparseTextureStreamingTransitionResult transitionResult)
                    {
                        if (cancellationToken.IsCancellationRequested
                            || (shouldAcceptResult is not null && !shouldAcceptResult()))
                        {
                            onCanceled?.Invoke();
                            return;
                        }

                        if (transitionResult.ExposureDeferred)
                        {
                            onDeferred?.Invoke(transitionRequest, transitionResult);
                            return;
                        }

                        if (!transitionResult.Applied || !transitionResult.UsedSparseResidency)
                        {
                            target.ClearSparseTextureStreamingState();
                            XRTexture2D.ApplyResidentData(target, residentData, includeMipChain: true);
                            XRTexture2D.ScheduleGpuUploadInternal(target, cancellationToken, () =>
                            {
                                if (cancellationToken.IsCancellationRequested
                                    || (shouldAcceptResult is not null && !shouldAcceptResult()))
                                {
                                    onCanceled?.Invoke();
                                    return;
                                }

                                onProgress?.Invoke(1.0f);
                                onFinished?.Invoke(target);
                            });
                            return;
                        }

                        onProgress?.Invoke(1.0f);
                        onFinished?.Invoke(target);
                    }

                    // A promotion (requesting a finer mip than what is currently resident) can use
                    // the async upload path. SparseTextureStreamingResidentBaseMipLevel defaults to
                    // int.MaxValue ("nothing resident"), so this correctly evaluates to true for every
                    // initial load and avoids blocking a thread-pool thread on the render thread.
                    bool allowAsyncUpload = transitionRequest.RequestedBaseMipLevel < target.SparseTextureStreamingResidentBaseMipLevel && TryEnterSharedUpload();
                    if (allowAsyncUpload)
                    {
                        RecordUploadBytes(uploadBytes);
                        bool asyncScheduled = RuntimeRenderingHostServices.Current.TryScheduleSparseTextureStreamingTransitionAsync(
                            target,
                            transitionRequest,
                            cancellationToken,
                            transitionResult =>
                            {
                                ExitSharedUpload();
                                if (RuntimeRenderingHostServices.Current.IsRenderThread)
                                    CompleteSparseTransition(transitionResult);
                                else
                                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
                                        () => CompleteSparseTransition(transitionResult),
                                        $"TextureStreaming.CompleteSparseTransition[{target.Name}]");
                            },
                            ex =>
                            {
                                ExitSharedUpload();
                                onError?.Invoke(ex);
                            });
                        if (asyncScheduled)
                            return;

                        ExitSharedUpload();
                    }

                    // Sync render-thread path: increment s_inFlightUploadCount so that
                    // anyGpuUploadActive is true while this thread is blocked, preventing
                    // the stuck-transition detector from firing prematurely.
                    Interlocked.Increment(ref s_inFlightUploadCount);
                    try
                    {
                        RecordUploadBytes(uploadBytes);
                        SparseTextureStreamingTransitionResult transitionResult = target.ApplySparseTextureStreamingTransition(transitionRequest);
                        CompleteSparseTransition(transitionResult);
                    }
                    finally
                    {
                        ExitSharedUpload();
                    }
                }

                bool ContinueOnRenderThreadBudgeted()
                {
                    if (!TryEnterSparseTransitionScheduleBudget())
                        return false;

                    ContinueOnRenderThreadCore();
                    return true;
                }

                if (RuntimeRenderingHostServices.Current.IsRenderThread && TryEnterSparseTransitionScheduleBudget())
                    ContinueOnRenderThreadCore();
                else
                    RuntimeRenderingHostServices.Current.EnqueueRenderThreadCoroutine(
                        ContinueOnRenderThreadBudgeted,
                        $"TextureStreaming.ScheduleSparseTransition[{target.Name}]");
            }
            catch (OperationCanceledException)
            {
                onCanceled?.Invoke();
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                if (!decodeActivated)
                {
                    int remaining = Interlocked.Decrement(ref s_queuedDecodeCount);
                    if (remaining < 0)
                        Interlocked.Exchange(ref s_queuedDecodeCount, 0);
                }
            }
        }, cancellationToken);

        IEnumerable Routine()
        {
            yield return loadTask;
        }

        return RuntimeRenderingHostServices.Current.ScheduleEnumeratorJob(
            Routine,
            priority,
            cancellationToken: CancellationToken.None);
    }

    private static SparseTextureStreamingTransitionRequest BuildSparseTransitionRequest(TextureStreamingResidentData residentData, SparseTextureStreamingPageSelection pageSelection)
    {
        int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(
            residentData.SourceWidth,
            residentData.SourceHeight,
            residentData.ResidentMaxDimension);
        int logicalMipCount = XRTexture2D.GetLogicalMipCount(residentData.SourceWidth, residentData.SourceHeight);

        return new SparseTextureStreamingTransitionRequest(
            residentData.SourceWidth,
            residentData.SourceHeight,
            ESizedInternalFormat.Rgba8,
            logicalMipCount,
            requestedBaseMipLevel,
            residentData.Mipmaps,
            pageSelection.Normalize());
    }

    private static bool TryEnterSharedUpload()
    {
        while (true)
        {
            int current = Volatile.Read(ref s_inFlightUploadCount);
            if (current >= MaxSharedUploadsInFlight)
                return false;

            if (Interlocked.CompareExchange(ref s_inFlightUploadCount, current + 1, current) == current)
                return true;
        }
    }

    private static bool TryEnterSparseTransitionScheduleBudget()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_sparseTransitionScheduleFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_sparseTransitionScheduleCountThisFrame, 0);

        while (true)
        {
            int currentCount = Volatile.Read(ref s_sparseTransitionScheduleCountThisFrame);
            if (currentCount >= MaxSparseTransitionSchedulesPerFrame)
                return false;

            if (Interlocked.CompareExchange(ref s_sparseTransitionScheduleCountThisFrame, currentCount + 1, currentCount) == currentCount)
                return true;
        }
    }

    private static void ExitSharedUpload()
    {
        int remaining = Interlocked.Decrement(ref s_inFlightUploadCount);
        if (remaining < 0)
            Interlocked.Exchange(ref s_inFlightUploadCount, 0);
    }

    private static void RecordUploadBytes(long bytes)
    {
        if (bytes <= 0)
            return;

        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Interlocked.Exchange(ref s_uploadBytesFrameTicks, currentFrame);
        if (previousFrame != currentFrame)
            Interlocked.Exchange(ref s_uploadBytesScheduledThisFrame, 0L);

        _ = Interlocked.Add(ref s_uploadBytesScheduledThisFrame, bytes);
    }

    private static long GetUploadBytesScheduledThisFrame()
    {
        long currentFrame = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
        long previousFrame = Volatile.Read(ref s_uploadBytesFrameTicks);
        if (previousFrame != currentFrame)
            return 0L;

        return Volatile.Read(ref s_uploadBytesScheduledThisFrame);
    }
}

internal sealed class ImportedTextureStreamingManager
{
    private const int MaxResidentTransitionsPerFrame = 2;
    private const int MaxPromotionTransitionsPerFrame = 1;
    private const int MaxSparseFinalizationsPerFrame = 2;
    private const int MinTransitionCooldownFrames = 2;
    private const int PromotionFadeFrames = 90;
    private const long MaxPromotionBytesPerFrame = 12L * 1024L * 1024L;
    private const int RecordRefCompactionIntervalFrames = 600;
    private const float PageSelectionFullCoverageThreshold = 0.85f;
    private static readonly bool EnablePartialSparsePageResidency = false;

    /// <summary>
    /// If a pending transition has been stuck for this many frames with no active
    /// decodes on either backend, force-clear it so the next Evaluate cycle can
    /// re-queue a fresh transition.
    /// </summary>
    private const int StuckPendingTransitionFrameThreshold = 300;

    private static readonly uint[] PolicyResidentCandidates =
    [
        64u,
        128u,
        256u,
        512u,
        1024u,
        2048u,
        4096u,
        8192u,
    ];

    private static readonly ConditionalWeakTable<XRTexture2D, ImportedTextureStreamingRecord> s_recordsByTexture = new();
    private static readonly ConcurrentQueue<WeakReference<ImportedTextureStreamingRecord>> s_recordRefs = new();

    public static ImportedTextureStreamingManager Instance { get; } = new();

    private readonly ITextureResidencyBackend _tieredBackend = new GLTieredTextureResidencyBackend();
    private readonly ITextureResidencyBackend _sparseBackend = new GLSparseTextureResidencyBackend();

    private int _callbacksSubscribed;
    private int _activeImportedModelImports;
    private long _collectFrameId;
    private int _queuedTransitionsThisFrame;
    private int _sparseFinalizeScheduled;
    private long _lastTelemetryFrameId;
    private long _lastCurrentManagedBytes;
    private long _lastAvailableManagedBytes;
    private long _lastAssignedManagedBytes;
    private long _lastCompactionFrameId = long.MinValue;

    private ImportedTextureStreamingManager()
    {
    }

    internal bool HasActiveImportedModelImports
        => Volatile.Read(ref _activeImportedModelImports) > 0;

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

    private sealed class ImportedTextureStreamingRecord(XRTexture2D texture)
    {
        public readonly object Sync = new();
        public readonly WeakReference<XRTexture2D> Texture = new(texture);
        public string? FilePath = texture.FilePath;
        public ITextureStreamingSource? Source;
        public ITextureResidencyBackend? Backend;
        public ESizedInternalFormat Format = texture.SizedInternalFormat;
        public uint SourceWidth;
        public uint SourceHeight;
        public uint ResidentMaxDimension;
        public uint DesiredMaxDimension;
        public uint PendingMaxDimension;
        public int SparseNumLevels;
        public long LastVisibleFrameId = long.MinValue;
        public float MinVisibleDistance = float.PositiveInfinity;
        public float MaxProjectedPixelSpan;
        public float MaxScreenCoverage;
        public float UvDensityHint = 1.0f;
        public SparseTextureStreamingPageSelection VisiblePageSelection = SparseTextureStreamingPageSelection.Full;
        public bool PreviewReady;
        public CancellationTokenSource? PendingLoadCts;
        public SparseTextureStreamingPageSelection PendingPageSelection = SparseTextureStreamingPageSelection.Full;
        public SparseTextureStreamingTransitionRequest PendingSparseTransitionRequest;
        public SparseTextureStreamingTransitionResult? PendingSparseTransitionResult;
        public long LastTransitionFrameId = long.MinValue;
        public float BaseLodBias;
        public float CurrentStreamingLodBias;
        public float PromotionFadeStartBias;
        public long PromotionFadeStartFrameId = long.MinValue;
        public long PromotionFadeEndFrameId = long.MinValue;
    }

    private readonly record struct ImportedTextureStreamingSnapshot(
        ImportedTextureStreamingRecord Record,
        ITextureResidencyBackend Backend,
        string? TextureName,
        string? FilePath,
        string? SamplerName,
        string FairnessGroupKey,
        ESizedInternalFormat Format,
        uint SourceWidth,
        uint SourceHeight,
        uint ResidentMaxDimension,
        uint PendingMaxDimension,
        int SparseNumLevels,
        long CurrentCommittedBytes,
        SparseTextureStreamingPageSelection CurrentPageSelection,
        long LastVisibleFrameId,
        float MinVisibleDistance,
        float MaxProjectedPixelSpan,
        float MaxScreenCoverage,
        float UvDensityHint,
        SparseTextureStreamingPageSelection VisiblePageSelection,
        bool PreviewReady,
        long LastTransitionFrameId);

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
        return previewBackend.SchedulePreviewLoad(
            record.Source ?? TextureStreamingSourceFactory.Create(filePath),
            target,
            residentData =>
            {
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
                }
            },
            onDeferred: null,
            tex =>
            {
                lock (record.Sync)
                {
                    record.SparseNumLevels = tex.SparseTextureStreamingNumSparseLevels;
                    record.Backend ??= ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format);
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
        if (material?.Textures is not { Count: > 0 })
            return;

        long frameId = Volatile.Read(ref _collectFrameId);
        if (frameId <= 0)
            return;

        float distance = usage.ClampedDistance;
        float projectedPixelSpan = usage.ClampedProjectedPixelSpan;
        float screenCoverage = usage.ClampedScreenCoverage;
        float uvDensityHint = usage.NormalizedUvDensityHint;
        SparseTextureStreamingPageSelection pageSelection = usage.NormalizedPageSelection;
        for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
        {
            if (material.Textures[textureIndex] is not XRTexture2D texture
                || !s_recordsByTexture.TryGetValue(texture, out ImportedTextureStreamingRecord? record))
            {
                continue;
            }

            if (!Monitor.TryEnter(record.Sync))
                continue;

            try
            {
                if (record.LastVisibleFrameId != frameId)
                {
                    record.LastVisibleFrameId = frameId;
                    record.MinVisibleDistance = distance;
                    record.MaxProjectedPixelSpan = projectedPixelSpan;
                    record.MaxScreenCoverage = screenCoverage;
                    record.UvDensityHint = uvDensityHint;
                    record.VisiblePageSelection = pageSelection;
                }
                else
                {
                    if (distance < record.MinVisibleDistance)
                        record.MinVisibleDistance = distance;

                    if (projectedPixelSpan > record.MaxProjectedPixelSpan)
                        record.MaxProjectedPixelSpan = projectedPixelSpan;

                    if (screenCoverage > record.MaxScreenCoverage)
                        record.MaxScreenCoverage = screenCoverage;

                    if (uvDensityHint > record.UvDensityHint)
                        record.UvDensityHint = uvDensityHint;

                    record.VisiblePageSelection = record.VisiblePageSelection.Union(pageSelection);
                }
            }
            finally
            {
                Monitor.Exit(record.Sync);
            }
        }
    }

    public ImportedTextureStreamingTelemetry GetTelemetry()
    {
        List<ImportedTextureStreamingSnapshot> snapshots = CollectSnapshots();
        int pendingTransitions = 0;
        string backendName = "None";
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].PendingMaxDimension != 0)
                pendingTransitions++;
        }

        if (snapshots.Count > 0)
        {
            backendName = snapshots[0].Backend.Name;
            for (int i = 1; i < snapshots.Count; i++)
            {
                if (!string.Equals(backendName, snapshots[i].Backend.Name, StringComparison.Ordinal))
                {
                    backendName = "Mixed";
                    break;
                }
            }
        }

        return new ImportedTextureStreamingTelemetry(
            backendName,
            Volatile.Read(ref _activeImportedModelImports),
            snapshots.Count,
            pendingTransitions,
            _tieredBackend.ActiveDecodeCount + _sparseBackend.ActiveDecodeCount,
            _tieredBackend.QueuedDecodeCount + _sparseBackend.QueuedDecodeCount,
            Volatile.Read(ref _queuedTransitionsThisFrame),
            Volatile.Read(ref _lastTelemetryFrameId),
            Volatile.Read(ref _lastCurrentManagedBytes),
            Volatile.Read(ref _lastAvailableManagedBytes),
            Volatile.Read(ref _lastAssignedManagedBytes),
            _tieredBackend.UploadBytesScheduledThisFrame + _sparseBackend.UploadBytesScheduledThisFrame,
            Volatile.Read(ref _activeImportedModelImports) > 0);
    }

    public IReadOnlyList<ImportedTextureStreamingTextureTelemetry> GetTrackedTextureTelemetry()
    {
        List<ImportedTextureStreamingSnapshot> snapshots = CollectSnapshots();
        List<ImportedTextureStreamingTextureTelemetry> telemetry = new(snapshots.Count);
        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
            uint desiredResidentSize = snapshot.PendingMaxDimension != 0
                ? snapshot.PendingMaxDimension
                : snapshot.Record.DesiredMaxDimension;
            SparseTextureStreamingPageSelection desiredPageSelection = snapshot.PendingMaxDimension != 0
                ? snapshot.Record.PendingPageSelection.Normalize(PageSelectionFullCoverageThreshold)
                : DetermineDesiredPageSelection(snapshot, Math.Max(XRTexture2D.GetPreviewResidentSize(sourceMaxDimension), desiredResidentSize), Volatile.Read(ref _lastTelemetryFrameId));
            long desiredCommittedBytes = snapshot.Backend.EstimateCommittedBytes(
                snapshot.SourceWidth,
                snapshot.SourceHeight,
                Math.Max(XRTexture2D.GetPreviewResidentSize(sourceMaxDimension), desiredResidentSize),
                snapshot.Format,
                snapshot.SparseNumLevels,
                desiredPageSelection);

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
                desiredCommittedBytes));
        }

        return telemetry;
    }

    internal static uint DetermineDesiredResidentSize(
        ImportedTextureStreamingPolicyInput input,
        long frameId,
        bool allowPromotions,
        uint previewMaxDimension)
    {
        uint sourceMaxDimension = Math.Max(input.SourceWidth, input.SourceHeight);
        uint minimumResidentSize = sourceMaxDimension == 0
            ? previewMaxDimension
            : Math.Min(sourceMaxDimension, previewMaxDimension);
        if (sourceMaxDimension != 0 && sourceMaxDimension <= minimumResidentSize)
            return sourceMaxDimension;

        if (!input.PreviewReady || sourceMaxDimension == 0)
            return minimumResidentSize;

        if (!allowPromotions)
            return minimumResidentSize;

        if (input.LastVisibleFrameId == frameId)
        {
            float roleMultiplier = ResolveTextureRoleMultiplier(input.SamplerName);
            float uvDensityHint = NormalizeUvDensityHint(input.UvDensityHint);
            float projectedPixelSpan = MathF.Max(0.0f, input.MaxProjectedPixelSpan);
            float targetPixelSpan = projectedPixelSpan * roleMultiplier * uvDensityHint;

            if (input.MaxScreenCoverage >= 0.95f)
                return sourceMaxDimension;

            uint visibleTarget = QuantizeResidentSize(sourceMaxDimension, minimumResidentSize, targetPixelSpan);
            if (input.ResidentMaxDimension > visibleTarget)
            {
                uint nextLowerResidentSize = GetNextLowerResidentCandidate(sourceMaxDimension, input.ResidentMaxDimension, minimumResidentSize);
                visibleTarget = Math.Max(visibleTarget, nextLowerResidentSize);
            }

            return visibleTarget;
        }

        long framesSinceVisible = input.LastVisibleFrameId < 0
            ? long.MaxValue
            : Math.Max(0L, frameId - input.LastVisibleFrameId);

        if (framesSinceVisible <= 4)
            return Math.Max(minimumResidentSize, input.ResidentMaxDimension);

        if (framesSinceVisible <= 12)
            return GetNextLowerResidentCandidate(sourceMaxDimension, input.ResidentMaxDimension, minimumResidentSize);

        return minimumResidentSize;
    }

    internal static float CalculatePromotionFadeBias(uint sourceWidth, uint sourceHeight, uint previousResidentSize, uint nextResidentSize)
    {
        if (previousResidentSize == 0 || nextResidentSize == 0 || nextResidentSize <= previousResidentSize)
            return 0.0f;

        int previousBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(sourceWidth, sourceHeight, previousResidentSize);
        int nextBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(sourceWidth, sourceHeight, nextResidentSize);
        return Math.Max(0, previousBaseMipLevel - nextBaseMipLevel);
    }

    internal static float SmoothPromotionFadeProgress(float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);
        return t * t * (3.0f - (2.0f * t));
    }

    internal uint FitResidentSizeToBudget(
        ImportedTextureStreamingBudgetInput input,
        uint desiredResidentSize,
        long availableManagedBytes,
        ITextureResidencyBackend? backend = null,
        ESizedInternalFormat format = ESizedInternalFormat.Rgba8,
        int sparseNumLevels = 0)
    {
        backend ??= _tieredBackend;
        uint sourceMaxDimension = Math.Max(input.SourceWidth, input.SourceHeight);
        uint minimumResidentSize = sourceMaxDimension == 0
            ? input.PreviewMaxDimension
            : Math.Min(sourceMaxDimension, input.PreviewMaxDimension);
        uint candidate = Math.Max(minimumResidentSize, desiredResidentSize);

        if (availableManagedBytes == long.MaxValue)
            return candidate;

        while (candidate > minimumResidentSize)
        {
            long requiredBytes = backend.EstimateCommittedBytes(input.SourceWidth, input.SourceHeight, candidate, format, sparseNumLevels, input.PageSelection);
            if (requiredBytes <= availableManagedBytes)
                return candidate;

            candidate = backend.GetNextLowerResidentSize(sourceMaxDimension, candidate);
        }

        return minimumResidentSize;
    }

    private static float NormalizeUvDensityHint(float value)
        => float.IsFinite(value)
            ? Math.Clamp(value, 0.5f, 2.0f)
            : 1.0f;

    private static uint QuantizeResidentSize(uint sourceMaxDimension, uint minimumResidentSize, float targetPixelSpan)
    {
        if (!float.IsFinite(targetPixelSpan) || targetPixelSpan <= minimumResidentSize)
            return minimumResidentSize;

        uint clampedTarget = (uint)Math.Clamp(MathF.Ceiling(targetPixelSpan), minimumResidentSize, sourceMaxDimension);
        for (int i = 0; i < PolicyResidentCandidates.Length; i++)
        {
            uint candidate = PolicyResidentCandidates[i];
            if (candidate < clampedTarget)
                continue;

            return Math.Min(sourceMaxDimension, Math.Max(minimumResidentSize, candidate));
        }

        return sourceMaxDimension;
    }

    private static uint GetNextLowerResidentCandidate(uint sourceMaxDimension, uint currentResidentSize, uint minimumResidentSize)
    {
        if (currentResidentSize <= minimumResidentSize)
            return minimumResidentSize;

        for (int index = PolicyResidentCandidates.Length - 1; index >= 0; index--)
        {
            uint candidate = PolicyResidentCandidates[index];
            if (candidate >= currentResidentSize)
                continue;
            if (sourceMaxDimension != 0 && candidate > sourceMaxDimension)
                continue;

            return Math.Max(candidate, minimumResidentSize);
        }

        return minimumResidentSize;
    }

    private static float ResolveTextureRoleMultiplier(string? samplerName)
    {
        if (string.IsNullOrWhiteSpace(samplerName))
            return 1.0f;

        string normalized = samplerName.Trim().ToLowerInvariant();
        if (normalized.Contains("normal") || normalized.Contains("bump") || normalized.Contains("height") || normalized.Contains("parallax"))
            return 0.85f;

        if (normalized.Contains("rough") || normalized.Contains("metal") || normalized.Contains("occlusion") || normalized.Contains("ao") || normalized.Contains("orm") || normalized.Contains("specular"))
            return 0.65f;

        if (normalized.Contains("alpha") || normalized.Contains("mask") || normalized.Contains("opacity") || normalized.Contains("emissive"))
            return 0.95f;

        return 1.0f;
    }

    private static SparseTextureStreamingPageSelection DetermineDesiredPageSelection(ImportedTextureStreamingSnapshot snapshot, uint residentSize, long frameId)
    {
        if (snapshot.Backend is not GLSparseTextureResidencyBackend)
            return SparseTextureStreamingPageSelection.Full;

        // Conservative for now: partial sparse page residency can expose black holes
        // when material UV transforms, wrapping, filtering, or rapid camera movement
        // sample outside the last recorded mesh UV rectangle. Keep sparse mip-level
        // residency, but fully commit the resident sparse mips until page tracking is
        // driven by the actual material sampling domain.
        if (!EnablePartialSparsePageResidency)
            return SparseTextureStreamingPageSelection.Full;

        uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
        uint previewResidentSize = XRTexture2D.GetPreviewResidentSize(sourceMaxDimension);
        if (residentSize <= previewResidentSize
            || snapshot.LastVisibleFrameId != frameId
            || sourceMaxDimension < 2048u)
        {
            return SparseTextureStreamingPageSelection.Full;
        }

        return snapshot.VisiblePageSelection.Normalize(PageSelectionFullCoverageThreshold);
    }

    private ITextureResidencyBackend ResolvePreviewBackendCandidate(ESizedInternalFormat format)
        => RuntimeRenderingHostServices.Current.CurrentRenderBackend == RuntimeGraphicsApiKind.OpenGL
            && RuntimeRenderingHostServices.Current.GetSparseTextureStreamingSupport(format).IsAvailable
                ? _sparseBackend
                : _tieredBackend;

    private ITextureResidencyBackend ResolveBackendForTexture(uint sourceWidth, uint sourceHeight, ESizedInternalFormat format)
    {
        if (RuntimeRenderingHostServices.Current.CurrentRenderBackend != RuntimeGraphicsApiKind.OpenGL)
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
        // with viewport CollectVisibleAutomatic handlers (multicast delegate
        // ordering is subscription-order-dependent).  If the viewport fires
        // first, RecordUsage tags data with the PREVIOUS frame's ID, and
        // Evaluate never sees the texture as "visible this frame".
        // Instead we advance the frame counter at the end of OnSwapBuffers,
        // after Evaluate has consumed the current frame's data.
        RuntimeRenderingHostServices.Current.SubscribeViewportSwapBuffers(OnSwapBuffers);
    }

    private void OnSwapBuffers()
    {
        long frameId = Volatile.Read(ref _collectFrameId);
        if (frameId <= 0)
            return;

        FinalizePendingSparseTransitions(frameId);
        UpdatePromotionFades(frameId);
        Evaluate(frameId);

        // Advance for the next collection phase.  Every viewport that runs
        // during the upcoming CollectVisible tick will tag usages with the
        // new value, and the next OnSwapBuffers will evaluate against it.
        Interlocked.Increment(ref _collectFrameId);
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
                    "TextureStreaming.FinalizeSparseTransitions");
            }

            return;
        }

        int completedFinalizations = 0;
        WeakReference<ImportedTextureStreamingRecord>[] refs = s_recordRefs.ToArray();
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
            RuntimeRenderingHostServices.Current.LogWarning(
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
    {
        ImportedTextureStreamingRecord record = s_recordsByTexture.GetValue(texture, static target =>
        {
            ImportedTextureStreamingRecord created = new(target);
            s_recordRefs.Enqueue(new WeakReference<ImportedTextureStreamingRecord>(created));
            return created;
        });

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            lock (record.Sync)
            {
                record.Format = texture.SizedInternalFormat;
                if (!string.Equals(record.FilePath, filePath, StringComparison.OrdinalIgnoreCase) || record.Source is null)
                {
                    record.FilePath = filePath;
                    record.Source = TextureStreamingSourceFactory.Create(filePath);
                }

                record.Backend ??= ResolvePreviewBackendCandidate(record.Format);
            }
        }

        return record;
    }

    private void UpdatePromotionFades(long frameId)
    {
        WeakReference<ImportedTextureStreamingRecord>[] refs = s_recordRefs.ToArray();
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

        // Drain the entire queue, discard dead entries, and re-enqueue live ones.
        // Items enqueued by GetOrCreateRecord() during this pass land after the last
        // TryDequeue and are preserved automatically.
        int count = s_recordRefs.Count;
        List<WeakReference<ImportedTextureStreamingRecord>> live = new(count);
        while (s_recordRefs.TryDequeue(out WeakReference<ImportedTextureStreamingRecord>? refEntry))
        {
            if (refEntry.TryGetTarget(out ImportedTextureStreamingRecord? record)
                && record.Texture.TryGetTarget(out _))
            {
                live.Add(refEntry);
            }
        }

        foreach (WeakReference<ImportedTextureStreamingRecord> refEntry in live)
            s_recordRefs.Enqueue(refEntry);
    }

    private void Evaluate(long frameId)
    {
        if (_lastCompactionFrameId == long.MinValue
            || frameId - _lastCompactionFrameId >= RecordRefCompactionIntervalFrames)
        {
            CompactRecordRefs(frameId);
        }

        List<ImportedTextureStreamingSnapshot> snapshots = CollectSnapshots();
        if (snapshots.Count == 0)
        {
            // Log once when first called with no snapshots, to confirm Evaluate is running
            if (frameId <= 10 && frameId % 5 == 0)
                RuntimeRenderingHostServices.Current.LogOutput(
                    $"[TextureStreaming] frame={frameId} Evaluate called but 0 snapshots (recordRefs={s_recordRefs.Count})");
            return;
        }

        bool allowPromotions = Volatile.Read(ref _activeImportedModelImports) == 0;
        long currentManagedBytes = 0L;
        for (int i = 0; i < snapshots.Count; i++)
            currentManagedBytes += snapshots[i].CurrentCommittedBytes;

        // Recovery pass: detect and force-clear stuck pending transitions.
        // A pending transition is "stuck" when its async load / upload should have
        // completed but ClearPendingTransition was never called (e.g., a callback
        // dropped after decode/upload work finished).
        bool anyDecodeActive = _tieredBackend.ActiveDecodeCount > 0
            || _tieredBackend.QueuedDecodeCount > 0
            || _sparseBackend.ActiveDecodeCount > 0
            || _sparseBackend.QueuedDecodeCount > 0;
        bool anyGpuUploadActive = _tieredBackend.ActiveGpuUploadCount > 0
            || _sparseBackend.ActiveGpuUploadCount > 0;
        if (!anyDecodeActive && !anyGpuUploadActive)
        {
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

                CancellationTokenSource? pendingLoadCts;
                lock (snapshot.Record.Sync)
                {
                    if (snapshot.Record.PendingMaxDimension == 0)
                        continue;

                    RuntimeRenderingHostServices.Current.LogWarning(
                        $"[TextureStreaming] Clearing stuck pending transition for '{snapshot.Record.FilePath}' "
                        + $"(pending={snapshot.Record.PendingMaxDimension}, resident={snapshot.Record.ResidentMaxDimension}, "
                        + $"staleFrames={framesSincePending})");

                    pendingLoadCts = snapshot.Record.PendingLoadCts;
                    snapshot.Record.PendingLoadCts = null;
                    snapshot.Record.PendingMaxDimension = 0;
                    snapshot.Record.PendingPageSelection = SparseTextureStreamingPageSelection.Full;
                    snapshot.Record.PendingSparseTransitionRequest = default;
                    snapshot.Record.PendingSparseTransitionResult = null;
                    snapshot.Record.LastTransitionFrameId = frameId;
                }

                CancelPendingLoad(pendingLoadCts);
            }
        }

        long trackedBudgetBytes = RuntimeRenderingHostServices.Current.TrackedVramBudgetBytes;
        long trackedVramBytes = RuntimeRenderingHostServices.Current.TrackedVramBytes;
        long nonManagedBytes = Math.Max(0L, trackedVramBytes - currentManagedBytes);
        long availableManagedBytes = trackedBudgetBytes == long.MaxValue
            ? long.MaxValue
            : Math.Max(0L, trackedBudgetBytes - nonManagedBytes);

        snapshots.Sort((left, right) => ComparePriority(left, right, frameId));

        long assignedManagedBytes = 0L;
        int queuedTransitions = 0;
        int queuedPromotions = 0;
        long queuedPromotionBytes = 0L;
        Dictionary<string, int> promotionCountsByGroup = new(StringComparer.OrdinalIgnoreCase);
        List<(ImportedTextureStreamingSnapshot Snapshot, uint AssignedResidentSize, SparseTextureStreamingPageSelection DesiredPageSelection, long TargetCommittedBytes)> deferredPromotions = [];

        bool TryQueueCandidate(
            ImportedTextureStreamingSnapshot snapshot,
            uint assignedResidentSize,
            SparseTextureStreamingPageSelection desiredPageSelection,
            long targetCommittedBytes,
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

            bool isPromotion = targetCommittedBytes > currentCommittedBytes;
            long deltaBytes = Math.Max(0L, targetCommittedBytes - currentCommittedBytes);
            if (isPromotion)
            {
                if (queuedTransitions >= MaxResidentTransitionsPerFrame
                    || queuedPromotions >= MaxPromotionTransitionsPerFrame
                    || queuedPromotionBytes + deltaBytes > MaxPromotionBytesPerFrame)
                {
                    return false;
                }

                if (enforceFairness
                    && !string.IsNullOrWhiteSpace(snapshot.FairnessGroupKey)
                    && promotionCountsByGroup.GetValueOrDefault(snapshot.FairnessGroupKey) > 0)
                {
                    deferredPromotions.Add((snapshot, assignedResidentSize, desiredPageSelection, targetCommittedBytes));
                    return false;
                }
            }
            else if (queuedTransitions >= MaxResidentTransitionsPerFrame)
            {
                return false;
            }

            if (!QueueResidentTransition(snapshot, assignedResidentSize, desiredPageSelection, frameId))
                return false;

            queuedTransitions++;
            if (isPromotion)
            {
                queuedPromotions++;
                queuedPromotionBytes += deltaBytes;
                if (!string.IsNullOrWhiteSpace(snapshot.FairnessGroupKey))
                    promotionCountsByGroup[snapshot.FairnessGroupKey] = promotionCountsByGroup.GetValueOrDefault(snapshot.FairnessGroupKey) + 1;
            }

            return true;
        }

        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            uint desiredResidentSize = DetermineDesiredResidentSize(
                new ImportedTextureStreamingPolicyInput(
                    snapshot.SourceWidth,
                    snapshot.SourceHeight,
                    snapshot.ResidentMaxDimension,
                    snapshot.PreviewReady,
                    snapshot.LastVisibleFrameId,
                    snapshot.MinVisibleDistance,
                    snapshot.MaxProjectedPixelSpan,
                    snapshot.MaxScreenCoverage,
                    snapshot.UvDensityHint,
                    snapshot.SamplerName),
                frameId,
                allowPromotions,
                snapshot.Backend.PreviewMaxDimension);

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

            _ = TryQueueCandidate(snapshot, assignedResidentSize, desiredPageSelection, targetCommittedBytes, enforceFairness: true);
        }

        for (int i = 0; i < deferredPromotions.Count; i++)
        {
            if (queuedTransitions >= MaxResidentTransitionsPerFrame || queuedPromotions >= MaxPromotionTransitionsPerFrame)
                break;

            (ImportedTextureStreamingSnapshot Snapshot, uint AssignedResidentSize, SparseTextureStreamingPageSelection DesiredPageSelection, long TargetCommittedBytes) deferred = deferredPromotions[i];
            _ = TryQueueCandidate(
                deferred.Snapshot,
                deferred.AssignedResidentSize,
                deferred.DesiredPageSelection,
                deferred.TargetCommittedBytes,
                enforceFairness: false);
        }

        Volatile.Write(ref _queuedTransitionsThisFrame, queuedTransitions);
        Volatile.Write(ref _lastTelemetryFrameId, frameId);
        Volatile.Write(ref _lastCurrentManagedBytes, currentManagedBytes);
        Volatile.Write(ref _lastAvailableManagedBytes, availableManagedBytes);
        Volatile.Write(ref _lastAssignedManagedBytes, assignedManagedBytes);

        // Periodic streaming diagnostics (every ~10 seconds at 60fps, or first few frames for early state)
        bool shouldLog = frameId % 600 == 0
            || (frameId <= 60 && frameId % 10 == 0)
            || (frameId > 60 && frameId <= 300 && frameId % 60 == 0);
        if (shouldLog)
        {
            int visibleCount = 0;
            int previewReadyCount = 0;
            int atPreviewCount = 0;
            int promotedCount = 0;
            int pendingCount = 0;
            uint maxResident = 0;
            float maxPixelSpan = 0;
            for (int i = 0; i < snapshots.Count; i++)
            {
                ImportedTextureStreamingSnapshot s = snapshots[i];
                if (s.LastVisibleFrameId == frameId) visibleCount++;
                if (s.PreviewReady) previewReadyCount++;
                uint previewSize = s.Backend.PreviewMaxDimension;
                if (s.ResidentMaxDimension <= previewSize) atPreviewCount++;
                else promotedCount++;
                if (s.PendingMaxDimension != 0) pendingCount++;
                if (s.ResidentMaxDimension > maxResident) maxResident = s.ResidentMaxDimension;
                if (s.MaxProjectedPixelSpan > maxPixelSpan) maxPixelSpan = s.MaxProjectedPixelSpan;
            }

            RuntimeRenderingHostServices.Current.LogOutput(
                $"[TextureStreaming] frame={frameId} tracked={snapshots.Count} visible={visibleCount} " +
                $"previewReady={previewReadyCount} atPreview={atPreviewCount} promoted={promotedCount} " +
                $"pending={pendingCount} maxResident={maxResident} maxPixelSpan={maxPixelSpan:F0} " +
                $"allowPromotions={allowPromotions} activeImports={Volatile.Read(ref _activeImportedModelImports)} " +
                $"queuedTransitions={queuedTransitions} queuedPromotions={queuedPromotions} " +
                $"budget={(availableManagedBytes == long.MaxValue ? "unlimited" : $"{availableManagedBytes / (1024 * 1024)}MB")}");
        }
    }

    private List<ImportedTextureStreamingSnapshot> CollectSnapshots()
    {
        List<ImportedTextureStreamingSnapshot> snapshots = [];
        WeakReference<ImportedTextureStreamingRecord>[] refs = s_recordRefs.ToArray();
        for (int i = 0; i < refs.Length; i++)
        {
            if (!refs[i].TryGetTarget(out ImportedTextureStreamingRecord? record)
                || !record.Texture.TryGetTarget(out XRTexture2D? texture))
            {
                continue;
            }

            if (!Monitor.TryEnter(record.Sync))
                continue;

            try
            {
                if (string.IsNullOrWhiteSpace(record.FilePath) || record.Source is null)
                    continue;

                snapshots.Add(new ImportedTextureStreamingSnapshot(
                    record,
                    record.Backend ?? Instance.ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format),
                    texture.Name,
                    record.FilePath,
                    texture.SamplerName,
                    ResolveFairnessGroupKey(record.FilePath),
                    record.Format,
                    record.SourceWidth,
                    record.SourceHeight,
                    record.ResidentMaxDimension,
                    record.PendingMaxDimension,
                    record.SparseNumLevels,
                    texture.SparseTextureStreamingEnabled
                        ? texture.SparseTextureStreamingCommittedBytes
                        : (record.Backend ?? Instance.ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format)).EstimateCommittedBytes(record.SourceWidth, record.SourceHeight, record.ResidentMaxDimension, record.Format, record.SparseNumLevels),
                    texture.SparseTextureStreamingEnabled
                        ? texture.SparseTextureStreamingResidentPageSelection
                        : SparseTextureStreamingPageSelection.Full,
                    record.LastVisibleFrameId,
                    record.MinVisibleDistance,
                    record.MaxProjectedPixelSpan,
                    record.MaxScreenCoverage,
                    record.UvDensityHint,
                    record.VisiblePageSelection,
                    record.PreviewReady,
                    record.LastTransitionFrameId));
            }
            finally
            {
                Monitor.Exit(record.Sync);
            }
        }

        return snapshots;
    }

    private static int ComparePriority(
        ImportedTextureStreamingSnapshot left,
        ImportedTextureStreamingSnapshot right,
        long frameId)
    {
        bool leftVisible = left.LastVisibleFrameId == frameId;
        bool rightVisible = right.LastVisibleFrameId == frameId;
        if (leftVisible != rightVisible)
            return leftVisible ? -1 : 1;

        if (leftVisible && rightVisible)
        {
            int priorityCompare = CalculatePriorityScore(right).CompareTo(CalculatePriorityScore(left));
            if (priorityCompare != 0)
                return priorityCompare;

            int distanceCompare = left.MinVisibleDistance.CompareTo(right.MinVisibleDistance);
            if (distanceCompare != 0)
                return distanceCompare;
        }

        int recencyCompare = right.LastVisibleFrameId.CompareTo(left.LastVisibleFrameId);
        if (recencyCompare != 0)
            return recencyCompare;

        return right.ResidentMaxDimension.CompareTo(left.ResidentMaxDimension);
    }

    private static float CalculatePriorityScore(ImportedTextureStreamingSnapshot snapshot)
        => (snapshot.MaxProjectedPixelSpan + snapshot.MaxScreenCoverage * 1024.0f)
            * ResolveTextureRoleMultiplier(snapshot.SamplerName)
            * NormalizeUvDensityHint(snapshot.UvDensityHint);

    private static string ResolveFairnessGroupKey(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        string? directory = Path.GetDirectoryName(filePath);
        return string.IsNullOrWhiteSpace(directory)
            ? Path.GetFileNameWithoutExtension(filePath) ?? string.Empty
            : directory;
    }

    private static void CancelPendingLoad(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool QueueResidentTransition(ImportedTextureStreamingSnapshot snapshot, uint targetResidentSize, SparseTextureStreamingPageSelection pageSelection, long frameId)
    {
        ImportedTextureStreamingRecord record = snapshot.Record;
        uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
        uint minimumResidentSize = XRTexture2D.GetPreviewResidentSize(sourceMaxDimension);
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
                return false;

            if (record.PendingMaxDimension == 0
                && normalizedTarget == record.ResidentMaxDimension
                && snapshot.CurrentPageSelection.NearlyEquals(normalizedPageSelection))
                return false;
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
        CancellationTokenSource? previousPendingLoadCts;
        bool includeMipChain;
        if (!Monitor.TryEnter(record.Sync))
        {
            cts.Dispose();
            return false;
        }

        try
        {
            filePath = record.FilePath;
            source = record.Source;
            backend = record.Backend ?? ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format);
            includeMipChain = normalizedTarget > minimumResidentSize;
            previousPendingLoadCts = record.PendingLoadCts;
            record.PendingLoadCts = cts;
            record.PendingMaxDimension = normalizedTarget;
            record.PendingPageSelection = normalizedPageSelection;
        }
        finally
        {
            Monitor.Exit(record.Sync);
        }

        CancelPendingLoad(previousPendingLoadCts);

        bool IsCurrentTransition()
        {
            lock (record.Sync)
                return ReferenceEquals(record.PendingLoadCts, cts) && !cts.IsCancellationRequested;
        }

        if (string.IsNullOrWhiteSpace(filePath) || source is null)
        {
            ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId);
            return false;
        }

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
                    record.Format = ESizedInternalFormat.Rgba8;
                    record.SourceWidth = residentData.SourceWidth;
                    record.SourceHeight = residentData.SourceHeight;
                    record.Backend = ResolveBackendForTexture(residentData.SourceWidth, residentData.SourceHeight, record.Format);
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
                ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId);
                RuntimeRenderingHostServices.Current.LogException(ex, $"Failed to stream imported texture '{filePath}' to resident size {normalizedTarget}.");
            },
            () => ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId),
            shouldAcceptResult: IsCurrentTransition,
            cancellationToken: cts.Token);

        return true;
    }

    private static void ClearPendingTransition(
        ImportedTextureStreamingRecord record,
        CancellationTokenSource cts,
        XRTexture2D? texture,
        uint completedResidentSize,
        long frameId)
    {
        float targetLodBias = 0.0f;
        bool shouldApplyLodBias = false;
        float textureLodBias = texture?.LodBias ?? 0.0f;
        int sparseNumLevels = texture?.SparseTextureStreamingNumSparseLevels ?? 0;
        lock (record.Sync)
        {
            if (!ReferenceEquals(record.PendingLoadCts, cts))
            {
                cts.Dispose();
                return;
            }

            if (completedResidentSize > 0)
            {
                uint previousResidentSize = record.ResidentMaxDimension;
                record.ResidentMaxDimension = completedResidentSize;
                record.PreviewReady = true;

                if (texture is not null)
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
            }

            if (texture is not null)
                record.SparseNumLevels = sparseNumLevels;

            record.PendingMaxDimension = 0;
            record.PendingLoadCts = null;
            record.PendingPageSelection = SparseTextureStreamingPageSelection.Full;
            record.PendingSparseTransitionRequest = default;
            record.PendingSparseTransitionResult = null;
            record.LastTransitionFrameId = frameId;
        }

        if (texture is not null && shouldApplyLodBias && !NearlyEquals(texture.LodBias, targetLodBias))
            texture.LodBias = targetLodBias;

        cts.Dispose();
    }

    private static void ClearPromotionFadeState(ImportedTextureStreamingRecord record)
    {
        record.CurrentStreamingLodBias = 0.0f;
        record.PromotionFadeStartBias = 0.0f;
        record.PromotionFadeStartFrameId = long.MinValue;
        Volatile.Write(ref record.PromotionFadeEndFrameId, long.MinValue);
    }

    private static bool NearlyEquals(float left, float right, float epsilon = 0.001f)
        => MathF.Abs(left - right) <= epsilon;
}
