using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using Svg.Skia;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const int DefaultIconSize = 24;
    private const int ToolbarIconMaxInputBytes = 512 * 1024;
    private const int ToolbarIconMaxCpuReadAttempts = 2;
    private const int ToolbarIconMaxUploadAttempts = 3;
    private const int ToolbarIconCompletionsPerFrame = 4;
    private const int ToolbarIconTexturesPerFrame = 2;
    private const int ToolbarIconUploadsPerFrame = 1;
    private const double ToolbarIconOwnerBudgetMilliseconds = 1.0;

    private static readonly string[] ToolbarIconManifest =
    [
        SvgEditorIcons.IconTranslate,
        SvgEditorIcons.IconRotate,
        SvgEditorIcons.IconScale,
        SvgEditorIcons.IconWorld,
        SvgEditorIcons.IconLocal,
        SvgEditorIcons.IconParent,
        SvgEditorIcons.IconScreen,
        SvgEditorIcons.IconSnap,
        SvgEditorIcons.IconPlay,
        SvgEditorIcons.IconPause,
        SvgEditorIcons.IconStop,
        SvgEditorIcons.IconStepFrame,
    ];

    private static readonly Dictionary<ToolbarIconCacheKey, ToolbarIconCacheEntry> _toolbarIconCache = new();
    private static readonly ConcurrentQueue<ToolbarIconPreparationResult> _toolbarIconCompletions = new();
    private static readonly ConditionalWeakTable<AbstractRenderer, ToolbarIconRendererFrameState> _toolbarIconRendererFrames = new();

    private static ToolbarIconCacheEntry[] _toolbarIconEntries = [];
    private static CancellationTokenSource? _toolbarIconCancellation;
    private static Task? _toolbarIconWorker;
    private static long _toolbarIconSessionId;
    private static ulong _toolbarIconLastOwnerFrame = ulong.MaxValue;
    private static int _toolbarIconStopping;

    /// <summary>
    /// Starts the bounded toolbar-icon CPU preparation session before the first
    /// toolbar draw. Only immutable path snapshots cross to the worker.
    /// </summary>
    private static void InitializeToolbarIcons()
    {
        if (_toolbarIconWorker is not null || Volatile.Read(ref _toolbarIconStopping) != 0)
            return;

        string relativeRoot = SvgEditorIcons.ICON_ROOT;
        string? engineAssetsPath = Engine.Assets?.EngineAssetsPath;
        string? executableDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string currentDirectory = Directory.GetCurrentDirectory();
        long sessionId = Interlocked.Increment(ref _toolbarIconSessionId);

        ToolbarIconCacheEntry[] entries = new ToolbarIconCacheEntry[ToolbarIconManifest.Length];
        for (int i = 0; i < ToolbarIconManifest.Length; i++)
        {
            ToolbarIconCacheKey key = new(ToolbarIconManifest[i], DefaultIconSize);
            ToolbarIconCacheEntry entry = new(
                key,
                BuildToolbarIconCandidatePaths(
                    key.Name,
                    relativeRoot,
                    engineAssetsPath,
                    executableDirectory,
                    currentDirectory),
                requestRevision: 1);
            entries[i] = entry;
            _toolbarIconCache.Add(key, entry);
        }

        _toolbarIconEntries = entries;
        _toolbarIconCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _toolbarIconCancellation.Token;
        _toolbarIconWorker = Task.Run(
            () => PrepareToolbarIconsAsync(entries, sessionId, cancellationToken),
            cancellationToken);
    }

    private static string[] BuildToolbarIconCandidatePaths(
        string iconName,
        string relativeRoot,
        string? engineAssetsPath,
        string? executableDirectory,
        string currentDirectory)
    {
        string relativePath = Path.Combine(relativeRoot, iconName);
        List<string> candidates = new(3);

        if (!string.IsNullOrWhiteSpace(engineAssetsPath))
            candidates.Add(Path.Combine(engineAssetsPath, relativePath));
        if (!string.IsNullOrWhiteSpace(executableDirectory))
            candidates.Add(Path.Combine(executableDirectory, relativePath));

        candidates.Add(Path.GetFullPath(relativePath, currentDirectory));
        return [.. candidates.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task PrepareToolbarIconsAsync(
        ToolbarIconCacheEntry[] entries,
        long sessionId,
        CancellationToken cancellationToken)
    {
        foreach (ToolbarIconCacheEntry entry in entries)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            ToolbarIconPreparationResult result = await PrepareToolbarIconAsync(
                entry,
                sessionId,
                cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return;

            _toolbarIconCompletions.Enqueue(result);
        }
    }

    private static async Task<ToolbarIconPreparationResult> PrepareToolbarIconAsync(
        ToolbarIconCacheEntry entry,
        long sessionId,
        CancellationToken cancellationToken)
    {
        long pathStart = Stopwatch.GetTimestamp();
        string? sourcePath = entry.CandidatePaths.FirstOrDefault(File.Exists);
        double pathMilliseconds = Stopwatch.GetElapsedTime(pathStart).TotalMilliseconds;
        if (sourcePath is null)
        {
            return new ToolbarIconPreparationResult(
                entry.Key,
                sessionId,
                entry.RequestRevision,
                preparedPixels: null,
                $"SVG icon was not found in {entry.CandidatePaths.Length} configured location(s).");
        }

        double readMilliseconds = 0.0;
        Exception? lastReadException = null;
        for (int attempt = 1; attempt <= ToolbarIconMaxCpuReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long readStart = Stopwatch.GetTimestamp();
                byte[] svgBytes = ReadToolbarIconBytes(sourcePath);
                readMilliseconds += Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                return RasterizeToolbarIcon(
                    entry,
                    sessionId,
                    sourcePath,
                    svgBytes,
                    pathMilliseconds,
                    readMilliseconds);
            }
            catch (IOException ex)
            {
                lastReadException = ex;
                if (attempt < ToolbarIconMaxCpuReadAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ToolbarIconPreparationResult(
                    entry.Key,
                    sessionId,
                    entry.RequestRevision,
                    preparedPixels: null,
                    ex.Message);
            }
        }

        return new ToolbarIconPreparationResult(
            entry.Key,
            sessionId,
            entry.RequestRevision,
            preparedPixels: null,
            lastReadException?.Message ?? "The SVG icon could not be read.");
    }

    private static byte[] ReadToolbarIconBytes(string sourcePath)
    {
        using FileStream stream = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > ToolbarIconMaxInputBytes)
        {
            throw new InvalidDataException(
                $"SVG icon input length {stream.Length} is outside the supported 1-{ToolbarIconMaxInputBytes} byte range.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ToolbarIconPreparationResult RasterizeToolbarIcon(
        ToolbarIconCacheEntry entry,
        long sessionId,
        string sourcePath,
        byte[] svgBytes,
        double pathMilliseconds,
        double readMilliseconds)
    {
        long rasterStart = Stopwatch.GetTimestamp();
        try
        {
            using MemoryStream stream = new(svgBytes, writable: false);
            using SKSvg svg = new();
            svg.Load(stream);

            SKPicture? picture = svg.Picture;
            if (picture is null)
                throw new InvalidDataException("The SVG parser produced no drawable picture.");

            int size = entry.Key.Size;
            SKRect cullRect = picture.CullRect;
            float scaleX = cullRect.Width > 0 ? size / cullRect.Width : 1.0f;
            float scaleY = cullRect.Height > 0 ? size / cullRect.Height : 1.0f;
            float scale = MathF.Min(scaleX, scaleY);

            using SKBitmap bitmap = new(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.Transparent);

            float translatedX = (size - (cullRect.Width * scale)) * 0.5f;
            float translatedY = (size - (cullRect.Height * scale)) * 0.5f;
            SKMatrix matrix = SKMatrix.CreateScale(scale, scale);
            matrix = matrix.PostConcat(SKMatrix.CreateTranslation(translatedX, translatedY));
            canvas.DrawPicture(picture, in matrix);
            canvas.Flush();

            byte[] pixels = CopyPackedRgbaPixels(bitmap, size);
            double rasterMilliseconds = Stopwatch.GetElapsedTime(rasterStart).TotalMilliseconds;
            ToolbarIconPreparedPixels prepared = new(
                pixels,
                sourcePath,
                svgBytes.Length,
                pathMilliseconds,
                readMilliseconds,
                rasterMilliseconds);
            return new ToolbarIconPreparationResult(
                entry.Key,
                sessionId,
                entry.RequestRevision,
                prepared,
                failureReason: null);
        }
        catch (Exception ex)
        {
            return new ToolbarIconPreparationResult(
                entry.Key,
                sessionId,
                entry.RequestRevision,
                preparedPixels: null,
                ex.Message);
        }
    }

    private static byte[] CopyPackedRgbaPixels(SKBitmap bitmap, int size)
    {
        int packedRowBytes = checked(size * 4);
        int packedLength = checked(packedRowBytes * size);
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(bitmap.GetPixelSpan());
        if (bitmap.RowBytes < packedRowBytes || source.Length < checked(bitmap.RowBytes * size))
            throw new InvalidDataException("The rasterized icon has an invalid RGBA row layout.");

        byte[] pixels = new byte[packedLength];
        for (int row = 0; row < size; row++)
        {
            source.Slice(row * bitmap.RowBytes, packedRowBytes)
                .CopyTo(pixels.AsSpan(row * packedRowBytes, packedRowBytes));
        }
        return pixels;
    }

    /// <summary>
    /// Publishes prepared pixels and performs renderer-owner preview uploads
    /// outside the toolbar draw scope. Work starts at most once per render frame.
    /// </summary>
    private static void ProcessToolbarIconOwnerWork()
    {
        if (Volatile.Read(ref _toolbarIconStopping) != 0 || !Engine.IsRenderThread)
            return;

        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        long startTimestamp = Stopwatch.GetTimestamp();
        int completionCount = 0;
        int textureCount = 0;
        if (_toolbarIconLastOwnerFrame != frameId)
        {
            _toolbarIconLastOwnerFrame = frameId;
            completionCount = DrainToolbarIconCompletions();
            textureCount = PublishToolbarIconTextures(startTimestamp);
        }

        int uploadCount = 0;
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is { AcceptsBackendWork: true })
        {
            ToolbarIconRendererFrameState rendererFrame = _toolbarIconRendererFrames.GetValue(
                renderer,
                static _ => new ToolbarIconRendererFrameState());
            if (rendererFrame.LastProcessedFrame != frameId)
            {
                rendererFrame.LastProcessedFrame = frameId;
                uploadCount = UploadToolbarIconsForRenderer(renderer, frameId, startTimestamp);
            }
        }
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        if (completionCount > 0 || textureCount > 0 || uploadCount > 0)
        {
            Debug.Rendering(
                "[ToolbarIcons] phase=OwnerPump frame={0} completions={1} textures={2} uploads={3} elapsedMs={4:F3} budgetMs={5:F3}",
                frameId,
                completionCount,
                textureCount,
                uploadCount,
                elapsedMilliseconds,
                ToolbarIconOwnerBudgetMilliseconds);
        }

        if (renderer is not null)
            LogToolbarIconReadySummaryIfComplete(renderer);
    }

    private static int DrainToolbarIconCompletions()
    {
        int count = 0;
        while (count < ToolbarIconCompletionsPerFrame &&
               _toolbarIconCompletions.TryDequeue(out ToolbarIconPreparationResult? result))
        {
            count++;
            if (result.SessionId != Volatile.Read(ref _toolbarIconSessionId) ||
                !_toolbarIconCache.TryGetValue(result.Key, out ToolbarIconCacheEntry? entry) ||
                result.RequestRevision != entry.RequestRevision ||
                entry.Status != ToolbarIconPreparationStatus.CpuPending)
            {
                continue;
            }

            if (result.PreparedPixels is not { } prepared)
            {
                entry.Status = ToolbarIconPreparationStatus.Failed;
                entry.FailureReason = result.FailureReason ?? "Unknown CPU preparation failure.";
                Debug.RenderingWarning(
                    "[ToolbarIcons] phase=CpuPreparation kind={0} status=Failed reason='{1}'",
                    entry.Key.Name,
                    entry.FailureReason);
                continue;
            }

            entry.PreparedPixels = prepared;
            entry.Status = ToolbarIconPreparationStatus.PixelsReady;
            Debug.Rendering(
                "[ToolbarIcons] phase=CpuPreparation kind={0} status=Ready inputBytes={1} pathMs={2:F3} readMs={3:F3} rasterMs={4:F3}",
                entry.Key.Name,
                prepared.InputBytes,
                prepared.PathMilliseconds,
                prepared.ReadMilliseconds,
                prepared.RasterMilliseconds);
        }
        return count;
    }

    private static int PublishToolbarIconTextures(long startTimestamp)
    {
        int count = 0;
        foreach (ToolbarIconCacheEntry entry in _toolbarIconEntries)
        {
            if (count >= ToolbarIconTexturesPerFrame || OwnerBudgetExpired(startTimestamp))
                break;
            if (entry.Status != ToolbarIconPreparationStatus.PixelsReady ||
                entry.PreparedPixels is not { } prepared)
            {
                continue;
            }

            try
            {
                XRTexture2D texture = new(
                    (uint)entry.Key.Size,
                    (uint)entry.Key.Size,
                    prepared.Pixels)
                {
                    Name = $"ToolbarIcon.{Path.GetFileNameWithoutExtension(entry.Key.Name)}",
                    FilePath = prepared.SourcePath,
                    AutoGenerateMipmaps = false,
                    Resizable = false,
                    MinFilter = ETexMinFilter.Linear,
                    MagFilter = ETexMagFilter.Linear,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                };
                entry.Texture = texture;
                entry.Status = ToolbarIconPreparationStatus.TextureReady;
                count++;
            }
            catch (Exception ex)
            {
                entry.Status = ToolbarIconPreparationStatus.Failed;
                entry.FailureReason = ex.Message;
                Debug.RenderingWarning(
                    "[ToolbarIcons] phase=TexturePublication kind={0} status=Failed reason='{1}'",
                    entry.Key.Name,
                    ex.Message);
            }
        }
        return count;
    }

    private static int UploadToolbarIconsForRenderer(
        AbstractRenderer renderer,
        ulong frameId,
        long startTimestamp)
    {
        int count = 0;
        foreach (ToolbarIconCacheEntry entry in _toolbarIconEntries)
        {
            if (count >= ToolbarIconUploadsPerFrame || OwnerBudgetExpired(startTimestamp))
                break;
            if (entry.Status != ToolbarIconPreparationStatus.TextureReady || entry.Texture is not { } texture)
                continue;

            ToolbarIconRendererState rendererState = entry.GetOrCreateRendererState(renderer);
            if (rendererState.IsReady || rendererState.AttemptCount >= ToolbarIconMaxUploadAttempts ||
                frameId < rendererState.NextRetryFrame)
            {
                continue;
            }

            RenderTexturePreviewOptions options = new(UploadIfNeeded: true);
            bool ready = EditorTexturePreviewService.TryGetHandle(
                texture,
                in options,
                out nint handle,
                out bool requiresVerticalFlip,
                out string? failureReason);
            count++;

            if (ready && handle != nint.Zero)
            {
                rendererState.Handle = handle;
                rendererState.RequiresVerticalFlip = requiresVerticalFlip;
                rendererState.IsReady = true;
                continue;
            }

            if (failureReason is null)
            {
                rendererState.NextRetryFrame = frameId + 1UL;
                continue;
            }

            rendererState.AttemptCount++;
            rendererState.NextRetryFrame = frameId + (1UL << rendererState.AttemptCount);
            if (rendererState.AttemptCount >= ToolbarIconMaxUploadAttempts && !rendererState.FailureLogged)
            {
                rendererState.FailureLogged = true;
                Debug.RenderingWarning(
                    "[ToolbarIcons] phase=PreviewUpload kind={0} status=Failed renderer={1} generation={2} attempts={3} reason='{4}'",
                    entry.Key.Name,
                    renderer.GetType().Name,
                    renderer.BackendGeneration,
                    rendererState.AttemptCount,
                    failureReason ?? "The preview backend returned no handle.");
            }
        }
        return count;
    }

    private static bool OwnerBudgetExpired(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds >= ToolbarIconOwnerBudgetMilliseconds;

    private static void LogToolbarIconReadySummaryIfComplete(AbstractRenderer renderer)
    {
        ToolbarIconRendererFrameState rendererFrame = _toolbarIconRendererFrames.GetValue(
            renderer,
            static _ => new ToolbarIconRendererFrameState());
        if (rendererFrame.ReadySummaryLogged)
            return;

        int ready = 0;
        int failed = 0;
        double totalRasterMilliseconds = 0.0;
        foreach (ToolbarIconCacheEntry entry in _toolbarIconEntries)
        {
            if (entry.Status == ToolbarIconPreparationStatus.Failed)
            {
                failed++;
            }
            else if (entry.TryGetRendererState(renderer, out ToolbarIconRendererState state))
            {
                if (state.IsReady)
                    ready++;
                else if (state.AttemptCount >= ToolbarIconMaxUploadAttempts)
                    failed++;
            }
            totalRasterMilliseconds += entry.PreparedPixels?.RasterMilliseconds ?? 0.0;
        }

        if (ready + failed != _toolbarIconEntries.Length)
            return;

        rendererFrame.ReadySummaryLogged = true;
        Debug.Rendering(
            "[ToolbarIcons] phase=ReadySummary total={0} ready={1} failed={2} cpuRasterizations={3} totalRasterMs={4:F3} renderer={5} generation={6}",
            _toolbarIconEntries.Length,
            ready,
            failed,
            _toolbarIconEntries.Count(static entry => entry.PreparedPixels is not null),
            totalRasterMilliseconds,
            renderer.GetType().Name,
            renderer.BackendGeneration);
    }

    /// <summary>
    /// Returns only an already-published renderer-local preview handle. This is
    /// intentionally a pure toolbar draw-path lookup.
    /// </summary>
    private static bool TryGetIconHandle(
        string iconName,
        out nint handle,
        out bool requiresVerticalFlip)
    {
        handle = nint.Zero;
        requiresVerticalFlip = false;
        if (Volatile.Read(ref _toolbarIconStopping) != 0 || AbstractRenderer.Current is not { } renderer)
            return false;

        ToolbarIconCacheKey cacheKey = new(iconName, DefaultIconSize);
        if (!_toolbarIconCache.TryGetValue(cacheKey, out ToolbarIconCacheEntry? entry) ||
            !entry.TryGetRendererState(renderer, out ToolbarIconRendererState state) ||
            !state.IsReady)
        {
            return false;
        }

        handle = state.Handle;
        requiresVerticalFlip = state.RequiresVerticalFlip;
        return handle != nint.Zero;
    }

    /// <summary>
    /// Stops CPU preparation and releases managed cache ownership after the
    /// engine's renderer-owned wrappers have completed their normal teardown.
    /// </summary>
    internal static void ShutdownToolbarIcons()
    {
        if (Interlocked.Exchange(ref _toolbarIconStopping, 1) != 0)
            return;

        Interlocked.Increment(ref _toolbarIconSessionId);
        _toolbarIconCancellation?.Cancel();

        Task? worker = _toolbarIconWorker;
        if (worker is not null)
        {
            try
            {
                worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(
                static inner => inner is OperationCanceledException or TaskCanceledException))
            {
            }
        }

        foreach (ToolbarIconCacheEntry entry in _toolbarIconEntries)
        {
            entry.Status = ToolbarIconPreparationStatus.Stopping;
            entry.PreparedPixels = null;
            entry.Texture = null;
        }
        _toolbarIconEntries = [];
        _toolbarIconCache.Clear();
        while (_toolbarIconCompletions.TryDequeue(out _))
        {
        }

        _toolbarIconCancellation?.Dispose();
        _toolbarIconCancellation = null;
        _toolbarIconWorker = null;
    }
}
