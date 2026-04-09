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

internal readonly record struct ImportedTextureStreamingPolicyInput(
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    bool PreviewReady,
    long LastVisibleFrameId,
    float MinVisibleDistance);

internal readonly record struct ImportedTextureStreamingBudgetInput(
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    uint PreviewMaxDimension);

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
    uint SourceWidth,
    uint SourceHeight,
    uint ResidentMaxDimension,
    uint DesiredResidentMaxDimension,
    uint PendingResidentMaxDimension,
    long LastVisibleFrameId,
    float MinVisibleDistance,
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
    long UploadBytesScheduledThisFrame { get; }
    long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0);
    uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize);
    EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low);
    EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low);
}

internal static class TextureStreamingSourceFactory
{
    internal static ITextureStreamingSource Create(string filePath)
    {
        string authorityPath = XRTexture2D.ResolveTextureStreamingAuthorityPathInternal(filePath, out _);

        if (XRTexture2D.HasAssetExtensionInternal(authorityPath))
            return new AssetTextureStreamingSource(authorityPath);

        return new ThirdPartyTextureStreamingSource(authorityPath);
    }
}

internal sealed class AssetTextureStreamingSource(string assetPath) : ITextureStreamingSource
{
    public string SourcePath => assetPath;

    public TextureStreamingResidentData LoadResidentData(uint maxResidentDimension, bool includeMipChain)
    {
        byte[] assetBytes = RuntimeRenderingHostServices.Current.ReadAllBytes(assetPath);
        if (XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes(assetBytes, maxResidentDimension, includeMipChain, out TextureStreamingResidentData residentData))
            return residentData;

        XRTexture2D? scratch = RuntimeRenderingHostServices.Current.LoadAsset<XRTexture2D>(assetPath);
        if (scratch is null)
            throw new FileNotFoundException($"Failed to load texture asset '{assetPath}'.", assetPath);

        return XRTexture2D.BuildResidentDataFromLoadedTexture(scratch, maxResidentDimension, includeMipChain);
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

        using MagickImage sourceImage = new(sourcePath);
        return XRTexture2D.BuildResidentDataFromImage(sourceImage, maxResidentDimension, includeMipChain);
    }
}

internal sealed class GLTieredTextureResidencyBackend : ITextureResidencyBackend
{
    private const int MaxConcurrentStreamingDecodes = 2;

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

    public string Name => nameof(GLTieredTextureResidencyBackend);
    public uint PreviewMaxDimension => XRTexture2D.ImportedPreviewMaxDimensionInternal;
    public int ActiveDecodeCount => Volatile.Read(ref s_activeDecodeCount);
    public int QueuedDecodeCount => Volatile.Read(ref s_queuedDecodeCount);
    public long UploadBytesScheduledThisFrame => GetUploadBytesScheduledThisFrame();

    public long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0)
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
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            PreviewMaxDimension,
            includeMipChain: false,
            onPrepared,
            onFinished,
            onError,
            onCanceled,
            cancellationToken,
            priority,
            onProgress);

    public EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            maxResidentDimension,
            includeMipChain,
            onPrepared,
            onFinished,
            onError,
            onCanceled,
            cancellationToken,
            priority,
            onProgress: null);

    private static EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        Action<TextureStreamingResidentData>? onPrepared,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled,
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

                onPrepared?.Invoke(residentData);
                XRTexture2D.ApplyResidentData(target, residentData, includeMipChain);
                onProgress?.Invoke(0.5f);

                if (cancellationToken.IsCancellationRequested)
                {
                    onCanceled?.Invoke();
                    return;
                }

                RecordUploadBytes(XRTexture2D.CalculateResidentUploadBytes(residentData));
                XRTexture2D.ScheduleGpuUploadInternal(target, cancellationToken, () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        onCanceled?.Invoke();
                        return;
                    }

                    onProgress?.Invoke(1.0f);
                    onFinished?.Invoke(target);
                });
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

    public string Name => nameof(GLSparseTextureResidencyBackend);
    public uint PreviewMaxDimension => XRTexture2D.ImportedPreviewMaxDimensionInternal;
    public int ActiveDecodeCount => Volatile.Read(ref s_activeDecodeCount);
    public int QueuedDecodeCount => Volatile.Read(ref s_queuedDecodeCount);
    public long UploadBytesScheduledThisFrame => GetUploadBytesScheduledThisFrame();

    public long EstimateCommittedBytes(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, ESizedInternalFormat format = ESizedInternalFormat.Rgba8, int sparseNumLevels = 0)
    {
        if (residentMaxDimension == 0)
            return 0L;

        int logicalMipCount = XRTexture2D.GetLogicalMipCount(sourceWidth, sourceHeight);
        if (logicalMipCount <= 0)
            return 0L;

        int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(sourceWidth, sourceHeight, residentMaxDimension);
        int committedBaseMipLevel = ResolveCommittedBaseMipLevel(requestedBaseMipLevel, sparseNumLevels, logicalMipCount);
        return XRTexture2D.EstimateMipRangeBytes(sourceWidth, sourceHeight, committedBaseMipLevel, logicalMipCount, format);
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
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            PreviewMaxDimension,
            includeMipChain: true,
            onPrepared,
            onFinished,
            onError,
            onCanceled,
            cancellationToken,
            priority,
            onProgress);

    public EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ScheduleResidentLoad(
            source,
            target,
            maxResidentDimension,
            includeMipChain: true,
            onPrepared,
            onFinished,
            onError,
            onCanceled,
            cancellationToken,
            priority,
            onProgress: null);

    private static EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        Action<TextureStreamingResidentData>? onPrepared,
        Action<XRTexture2D>? onFinished,
        Action<Exception>? onError,
        Action? onCanceled,
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

                onPrepared?.Invoke(residentData);
                onProgress?.Invoke(0.5f);

                if (cancellationToken.IsCancellationRequested)
                {
                    onCanceled?.Invoke();
                    return;
                }

                RecordUploadBytes(XRTexture2D.CalculateResidentUploadBytes(residentData));

                SparseTextureStreamingTransitionResult transitionResult = RunSparseTransitionOnRenderThread(target, residentData, cancellationToken);
                if (!transitionResult.Applied || !transitionResult.UsedSparseResidency)
                {
                    target.ClearSparseTextureStreamingState();
                    XRTexture2D.ApplyResidentData(target, residentData, includeMipChain: true);
                    XRTexture2D.ScheduleGpuUploadInternal(target, cancellationToken, () =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            onCanceled?.Invoke();
                            return;
                        }

                        onProgress?.Invoke(1.0f);
                        onFinished?.Invoke(target);
                    });
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    onCanceled?.Invoke();
                    return;
                }

                onProgress?.Invoke(1.0f);
                onFinished?.Invoke(target);
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

    private static SparseTextureStreamingTransitionResult RunSparseTransitionOnRenderThread(
        XRTexture2D target,
        TextureStreamingResidentData residentData,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<SparseTextureStreamingTransitionResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                int requestedBaseMipLevel = XRTexture2D.ResolveResidentBaseMipLevel(
                    residentData.SourceWidth,
                    residentData.SourceHeight,
                    residentData.ResidentMaxDimension);
                int logicalMipCount = XRTexture2D.GetLogicalMipCount(residentData.SourceWidth, residentData.SourceHeight);
                target.SizedInternalFormat = ESizedInternalFormat.Rgba8;
                SparseTextureStreamingTransitionResult result = target.ApplySparseTextureStreamingTransition(
                    new SparseTextureStreamingTransitionRequest(
                        residentData.SourceWidth,
                        residentData.SourceHeight,
                        ESizedInternalFormat.Rgba8,
                        logicalMipCount,
                        requestedBaseMipLevel,
                        residentData.Mipmaps));
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    private static int ResolveCommittedBaseMipLevel(int requestedBaseMipLevel, int sparseNumLevels, int logicalMipCount)
    {
        if (logicalMipCount <= 0)
            return 0;

        if (sparseNumLevels <= 0)
            return Math.Clamp(requestedBaseMipLevel, 0, logicalMipCount - 1);

        int tailFirstMipLevel = Math.Min(Math.Max(0, sparseNumLevels), logicalMipCount);
        if (tailFirstMipLevel >= logicalMipCount)
            return Math.Clamp(requestedBaseMipLevel, 0, logicalMipCount - 1);

        return Math.Min(Math.Clamp(requestedBaseMipLevel, 0, logicalMipCount - 1), tailFirstMipLevel);
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
    private const int MaxResidentTransitionsPerFrame = 4;
    private const int MaxPromotionTransitionsPerFrame = 2;
    private const int MinTransitionCooldownFrames = 2;
    private const long MaxPromotionBytesPerFrame = 24L * 1024L * 1024L;
    private const int RecordRefCompactionIntervalFrames = 600;

    private static readonly ConditionalWeakTable<XRTexture2D, ImportedTextureStreamingRecord> s_recordsByTexture = new();
    private static readonly ConcurrentQueue<WeakReference<ImportedTextureStreamingRecord>> s_recordRefs = new();

    public static ImportedTextureStreamingManager Instance { get; } = new();

    private readonly ITextureResidencyBackend _tieredBackend = new GLTieredTextureResidencyBackend();
    private readonly ITextureResidencyBackend _sparseBackend = new GLSparseTextureResidencyBackend();

    private int _callbacksSubscribed;
    private int _activeImportedModelImports;
    private long _collectFrameId;
    private int _queuedTransitionsThisFrame;
    private long _lastTelemetryFrameId;
    private long _lastCurrentManagedBytes;
    private long _lastAvailableManagedBytes;
    private long _lastAssignedManagedBytes;
    private long _lastCompactionFrameId = long.MinValue;

    private ImportedTextureStreamingManager()
    {
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
        public bool PreviewReady;
        public CancellationTokenSource? PendingLoadCts;
        public long LastTransitionFrameId = long.MinValue;
    }

    private readonly record struct ImportedTextureStreamingSnapshot(
        ImportedTextureStreamingRecord Record,
        ITextureResidencyBackend Backend,
        string? TextureName,
        string? FilePath,
        ESizedInternalFormat Format,
        uint SourceWidth,
        uint SourceHeight,
        uint ResidentMaxDimension,
        uint PendingMaxDimension,
        int SparseNumLevels,
        long LastVisibleFrameId,
        float MinVisibleDistance,
        bool PreviewReady,
        long LastTransitionFrameId);

    public IDisposable EnterScope()
    {
        EnsureCallbacksSubscribed();
        Interlocked.Increment(ref _activeImportedModelImports);
        return new ImportedTextureStreamingScope(this);
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
        ImportedTextureStreamingRecord record = GetOrCreateRecord(target, authorityPath);
        lock (record.Sync)
        {
            record.Format = ESizedInternalFormat.Rgba8;
            record.Backend = ResolvePreviewBackendCandidate(record.Format);
        }

        ITextureResidencyBackend previewBackend = record.Backend;
        return previewBackend.SchedulePreviewLoad(
            record.Source ?? TextureStreamingSourceFactory.Create(authorityPath),
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
            cancellationToken,
            priority);
    }

    public void RecordUsage(XRMaterial? material, float distanceFromCamera)
    {
        if (material?.Textures is not { Count: > 0 })
            return;

        long frameId = Volatile.Read(ref _collectFrameId);
        if (frameId <= 0)
            return;

        float distance = MathF.Max(0.0f, distanceFromCamera);
        for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
        {
            if (material.Textures[textureIndex] is not XRTexture2D texture
                || !s_recordsByTexture.TryGetValue(texture, out ImportedTextureStreamingRecord? record))
            {
                continue;
            }

            lock (record.Sync)
            {
                if (record.LastVisibleFrameId != frameId)
                {
                    record.LastVisibleFrameId = frameId;
                    record.MinVisibleDistance = distance;
                }
                else if (distance < record.MinVisibleDistance)
                {
                    record.MinVisibleDistance = distance;
                }
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

            telemetry.Add(new ImportedTextureStreamingTextureTelemetry(
                snapshot.TextureName,
                snapshot.FilePath,
                snapshot.SourceWidth,
                snapshot.SourceHeight,
                snapshot.ResidentMaxDimension,
                desiredResidentSize,
                snapshot.PendingMaxDimension,
                snapshot.LastVisibleFrameId,
                snapshot.MinVisibleDistance,
                snapshot.PreviewReady,
                snapshot.Backend.EstimateCommittedBytes(snapshot.SourceWidth, snapshot.SourceHeight, snapshot.ResidentMaxDimension, snapshot.Format, snapshot.SparseNumLevels),
                snapshot.Backend.EstimateCommittedBytes(snapshot.SourceWidth, snapshot.SourceHeight, Math.Max(XRTexture2D.GetPreviewResidentSize(sourceMaxDimension), desiredResidentSize), snapshot.Format, snapshot.SparseNumLevels)));
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

        if (!input.PreviewReady || !allowPromotions || sourceMaxDimension == 0)
            return minimumResidentSize;

        if (input.LastVisibleFrameId == frameId)
        {
            float distance = input.MinVisibleDistance;
            return distance switch
            {
                <= 2.0f => sourceMaxDimension,
                <= 5.0f => Math.Min(sourceMaxDimension, 2048u),
                <= 10.0f => Math.Min(sourceMaxDimension, 1024u),
                <= 20.0f => Math.Min(sourceMaxDimension, 512u),
                <= 40.0f => Math.Min(sourceMaxDimension, 256u),
                _ => Math.Min(sourceMaxDimension, 128u),
            };
        }

        long framesSinceVisible = input.LastVisibleFrameId < 0
            ? long.MaxValue
            : Math.Max(0L, frameId - input.LastVisibleFrameId);

        if (framesSinceVisible <= 4)
            return Math.Min(sourceMaxDimension, Math.Max(input.ResidentMaxDimension, Math.Min(sourceMaxDimension, 256u)));

        if (framesSinceVisible <= 12)
            return Math.Min(sourceMaxDimension, Math.Max(minimumResidentSize, Math.Min(input.ResidentMaxDimension, 128u)));

        return minimumResidentSize;
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
            long requiredBytes = backend.EstimateCommittedBytes(input.SourceWidth, input.SourceHeight, candidate, format, sparseNumLevels);
            if (requiredBytes <= availableManagedBytes)
                return candidate;

            candidate = backend.GetNextLowerResidentSize(sourceMaxDimension, candidate);
        }

        return minimumResidentSize;
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

        RuntimeRenderingHostServices.Current.SubscribeViewportCollectVisible(OnCollectVisible);
        RuntimeRenderingHostServices.Current.SubscribeViewportSwapBuffers(OnSwapBuffers);
    }

    private void OnCollectVisible()
        => Interlocked.Increment(ref _collectFrameId);

    private void OnSwapBuffers()
    {
        long frameId = Volatile.Read(ref _collectFrameId);
        if (frameId <= 0)
            return;

        Evaluate(frameId);
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
            return;

        bool allowPromotions = Volatile.Read(ref _activeImportedModelImports) == 0;
        long currentManagedBytes = 0L;
        for (int i = 0; i < snapshots.Count; i++)
        {
            ImportedTextureStreamingSnapshot snapshot = snapshots[i];
            currentManagedBytes += snapshot.Backend.EstimateCommittedBytes(
                snapshot.SourceWidth,
                snapshot.SourceHeight,
                snapshot.ResidentMaxDimension,
                snapshot.Format,
                snapshot.SparseNumLevels);
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
                    snapshot.MinVisibleDistance),
                frameId,
                allowPromotions,
                snapshot.Backend.PreviewMaxDimension);

            long remainingBudget = availableManagedBytes == long.MaxValue
                ? long.MaxValue
                : Math.Max(0L, availableManagedBytes - assignedManagedBytes);
            uint assignedResidentSize = FitResidentSizeToBudget(
                new ImportedTextureStreamingBudgetInput(
                    snapshot.SourceWidth,
                    snapshot.SourceHeight,
                    snapshot.ResidentMaxDimension,
                    snapshot.Backend.PreviewMaxDimension),
                desiredResidentSize,
                remainingBudget,
                snapshot.Backend,
                snapshot.Format,
                snapshot.SparseNumLevels);

            assignedManagedBytes += snapshot.Backend.EstimateCommittedBytes(
                snapshot.SourceWidth,
                snapshot.SourceHeight,
                assignedResidentSize,
                snapshot.Format,
                snapshot.SparseNumLevels);
            lock (snapshot.Record.Sync)
                snapshot.Record.DesiredMaxDimension = assignedResidentSize;

            uint currentResidentSize = snapshot.PendingMaxDimension != 0
                ? snapshot.PendingMaxDimension
                : snapshot.ResidentMaxDimension;
            if (assignedResidentSize == currentResidentSize)
                continue;

            if (snapshot.LastTransitionFrameId != long.MinValue
                && frameId - snapshot.LastTransitionFrameId < MinTransitionCooldownFrames
                && snapshot.PendingMaxDimension == 0)
            {
                continue;
            }

            bool isPromotion = assignedResidentSize > currentResidentSize;
            if (isPromotion)
            {
                long currentBytes = snapshot.Backend.EstimateCommittedBytes(snapshot.SourceWidth, snapshot.SourceHeight, currentResidentSize, snapshot.Format, snapshot.SparseNumLevels);
                long targetBytes = snapshot.Backend.EstimateCommittedBytes(snapshot.SourceWidth, snapshot.SourceHeight, assignedResidentSize, snapshot.Format, snapshot.SparseNumLevels);
                long deltaBytes = Math.Max(0L, targetBytes - currentBytes);
                if (queuedTransitions >= MaxResidentTransitionsPerFrame
                    || queuedPromotions >= MaxPromotionTransitionsPerFrame
                    || queuedPromotionBytes + deltaBytes > MaxPromotionBytesPerFrame)
                {
                    continue;
                }

                queuedPromotions++;
                queuedPromotionBytes += deltaBytes;
            }
            else if (queuedTransitions >= MaxResidentTransitionsPerFrame)
            {
                continue;
            }

            if (QueueResidentTransition(snapshot, assignedResidentSize, frameId))
                queuedTransitions++;
        }

        Volatile.Write(ref _queuedTransitionsThisFrame, queuedTransitions);
        Volatile.Write(ref _lastTelemetryFrameId, frameId);
        Volatile.Write(ref _lastCurrentManagedBytes, currentManagedBytes);
        Volatile.Write(ref _lastAvailableManagedBytes, availableManagedBytes);
        Volatile.Write(ref _lastAssignedManagedBytes, assignedManagedBytes);
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

            lock (record.Sync)
            {
                if (string.IsNullOrWhiteSpace(record.FilePath) || record.Source is null)
                    continue;

                snapshots.Add(new ImportedTextureStreamingSnapshot(
                    record,
                    record.Backend ?? Instance.ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format),
                    texture.Name,
                    record.FilePath,
                    record.Format,
                    record.SourceWidth,
                    record.SourceHeight,
                    record.ResidentMaxDimension,
                    record.PendingMaxDimension,
                    record.SparseNumLevels,
                    record.LastVisibleFrameId,
                    record.MinVisibleDistance,
                    record.PreviewReady,
                    record.LastTransitionFrameId));
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
            int distanceCompare = left.MinVisibleDistance.CompareTo(right.MinVisibleDistance);
            if (distanceCompare != 0)
                return distanceCompare;
        }

        int recencyCompare = right.LastVisibleFrameId.CompareTo(left.LastVisibleFrameId);
        if (recencyCompare != 0)
            return recencyCompare;

        return right.ResidentMaxDimension.CompareTo(left.ResidentMaxDimension);
    }

    private bool QueueResidentTransition(ImportedTextureStreamingSnapshot snapshot, uint targetResidentSize, long frameId)
    {
        ImportedTextureStreamingRecord record = snapshot.Record;
        uint sourceMaxDimension = Math.Max(snapshot.SourceWidth, snapshot.SourceHeight);
        uint minimumResidentSize = XRTexture2D.GetPreviewResidentSize(sourceMaxDimension);
        uint normalizedTarget = Math.Max(minimumResidentSize, targetResidentSize);

        lock (record.Sync)
        {
            if (normalizedTarget == record.PendingMaxDimension)
                return false;

            if (record.PendingMaxDimension == 0 && normalizedTarget == record.ResidentMaxDimension)
                return false;
        }

        if (!record.Texture.TryGetTarget(out XRTexture2D? texture))
            return false;

        string? filePath;
        ITextureStreamingSource? source;
        ITextureResidencyBackend backend;
        CancellationTokenSource cts = new();
        bool includeMipChain;
        lock (record.Sync)
        {
            filePath = record.FilePath;
            source = record.Source;
            backend = record.Backend ?? ResolveBackendForTexture(record.SourceWidth, record.SourceHeight, record.Format);
            includeMipChain = normalizedTarget > minimumResidentSize;
            record.PendingLoadCts?.Cancel();
            record.PendingLoadCts = cts;
            record.PendingMaxDimension = normalizedTarget;
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
            tex => ClearPendingTransition(record, cts, tex, normalizedTarget, frameId),
            ex =>
            {
                ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId);
                RuntimeRenderingHostServices.Current.LogException(ex, $"Failed to stream imported texture '{filePath}' to resident size {normalizedTarget}.");
            },
            () => ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId),
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
        lock (record.Sync)
        {
            if (!ReferenceEquals(record.PendingLoadCts, cts))
            {
                cts.Dispose();
                return;
            }

            if (completedResidentSize > 0)
                record.ResidentMaxDimension = completedResidentSize;

            if (texture is not null)
                record.SparseNumLevels = texture.SparseTextureStreamingNumSparseLevels;

            record.PendingMaxDimension = 0;
            record.PendingLoadCts = null;
            record.LastTransitionFrameId = frameId;
        }

        cts.Dispose();
    }
}
