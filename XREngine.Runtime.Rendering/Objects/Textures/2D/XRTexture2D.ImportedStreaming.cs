using ImageMagick;
using System.Diagnostics;
using System.IO;
using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    internal const uint ImportedPreviewMaxDimensionInternal = 64;
    internal const double ImportedTextureTimingLogThresholdMilliseconds = 5.0;

    internal static bool ShouldLogImportedTextureTiming
        => ImportedTextureStreamingManager.Instance.HasActiveImportedModelImports
            || TextureRuntimeDiagnostics.IsEnabled;

    public static IDisposable EnterImportedTextureStreamingScope()
        => ImportedTextureStreamingManager.Instance.EnterScope();

    public static void RegisterImportedTextureStreamingPlaceholder(string filePath, XRTexture2D texture)
        => ImportedTextureStreamingManager.Instance.RegisterTexture(filePath, texture);

    public static EnumeratorJob ScheduleImportedTexturePreviewJob(
        string filePath,
        XRTexture2D? texture = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low)
        => ImportedTextureStreamingManager.Instance.SchedulePreviewJob(
            filePath,
            texture,
            onFinished,
            onError,
            onCanceled,
            onProgress,
            cancellationToken,
            priority);

    public static void RecordImportedTextureStreamingUsage(XRMaterial? material, float distanceFromCamera)
        => ImportedTextureStreamingManager.Instance.RecordUsage(material, distanceFromCamera);

    public static void RecordImportedTextureStreamingUsage(XRMaterial? material, ImportedTextureStreamingUsage usage)
        => ImportedTextureStreamingManager.Instance.RecordUsage(material, usage);

    public static void RecordImportedTextureMaterialBinding(XRMaterialBase? material)
        => ImportedTextureStreamingManager.Instance.RecordMaterialBinding(material);

    public static ImportedTextureStreamingTelemetry GetImportedTextureStreamingTelemetry()
        => ImportedTextureStreamingManager.Instance.GetTelemetry();

    public static IReadOnlyList<ImportedTextureStreamingTextureTelemetry> GetImportedTextureStreamingTextureTelemetry()
        => ImportedTextureStreamingManager.Instance.GetTrackedTextureTelemetry();

    public static void DumpImportedTextureStreamingSummary()
        => ImportedTextureStreamingManager.Instance.DumpSummary();

    internal static bool HasAssetExtensionInternal(string filePath)
        => HasAssetExtension(filePath);

    internal static string ResolveTextureStreamingAuthorityPathInternal(string filePath, out string? originalSourcePath)
    {
        originalSourcePath = null;
        if (string.IsNullOrWhiteSpace(filePath))
            return filePath;

        string normalizedPath = Path.GetFullPath(filePath);
        if (HasAssetExtension(normalizedPath))
            return normalizedPath;

        string authorityPath = RuntimeRenderingHostServices.Current.ResolveTextureStreamingAuthorityPath(normalizedPath);
        if (string.IsNullOrWhiteSpace(authorityPath))
            return normalizedPath;

        authorityPath = Path.GetFullPath(authorityPath);
        if (!string.Equals(authorityPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            originalSourcePath = normalizedPath;

        return authorityPath;
    }

    internal static void ApplyTextureStreamingAuthorityPath(XRTexture2D texture, string requestedFilePath)
    {
        string authorityPath = ResolveTextureStreamingAuthorityPathInternal(requestedFilePath, out string? originalSourcePath);
        texture.FilePath = authorityPath;
        if (!string.IsNullOrWhiteSpace(originalSourcePath))
            texture.OriginalPath ??= originalSourcePath;
    }

    internal static void ScheduleGpuUploadInternal(XRTexture2D texture, CancellationToken cancellationToken, Action? onCompleted = null)
        => ScheduleGpuUpload(texture, cancellationToken, onCompleted);

    internal static long StartImportedTextureTiming()
        => ShouldLogImportedTextureTiming ? Stopwatch.GetTimestamp() : 0L;

    internal static double CompleteImportedTextureTiming(long startTimestamp)
    {
        if (startTimestamp == 0L)
            return 0.0;

        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return elapsedTicks * 1000.0 / Stopwatch.Frequency;
    }

    internal static void LogImportedTextureTiming(
        string? sourceLabel,
        uint sourceWidth,
        uint sourceHeight,
        uint requestedResidentMaxDimension,
        uint residentMaxDimension,
        bool includeMipChain,
        int mipCount,
        double decodeMilliseconds,
        double cloneMilliseconds,
        double resizeMilliseconds,
        double mipBuildMilliseconds)
    {
        if (!ShouldLogImportedTextureTiming || string.IsNullOrWhiteSpace(sourceLabel))
            return;

        double totalMilliseconds = decodeMilliseconds + cloneMilliseconds + resizeMilliseconds + mipBuildMilliseconds;
        double decodeResizeThreshold = RuntimeRenderingHostServices.Current.TextureSlowCpuDecodeResizeMilliseconds;
        double mipBuildThreshold = RuntimeRenderingHostServices.Current.TextureSlowMipBuildMilliseconds;
        if (totalMilliseconds < ImportedTextureTimingLogThresholdMilliseconds
            && decodeMilliseconds < decodeResizeThreshold
            && cloneMilliseconds < decodeResizeThreshold
            && resizeMilliseconds < decodeResizeThreshold
            && mipBuildMilliseconds < mipBuildThreshold)
        {
            return;
        }

        TextureRuntimeDiagnostics.LogCpuTextureWorkSlow(
            RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
            sourceLabel,
            sourceWidth,
            sourceHeight,
            requestedResidentMaxDimension,
            residentMaxDimension,
            includeMipChain,
            mipCount,
            decodeMilliseconds,
            cloneMilliseconds,
            resizeMilliseconds,
            mipBuildMilliseconds,
            totalMilliseconds,
            ImportedTextureTimingLogThresholdMilliseconds);

        RuntimeRenderingHostServices.Current.LogOutput(
            $"[ImportedTextureTiming] '{sourceLabel}' source={sourceWidth}x{sourceHeight} requestedMax={requestedResidentMaxDimension} resident={residentMaxDimension} includeMipChain={includeMipChain} mips={mipCount} decode={decodeMilliseconds:F1}ms clone={cloneMilliseconds:F1}ms resize={resizeMilliseconds:F1}ms mipBuild={mipBuildMilliseconds:F1}ms total={totalMilliseconds:F1}ms");
    }

    internal static TextureStreamingResidentData BuildResidentDataFromLoadedTexture(
        XRTexture2D texture,
        uint maxResidentDimension,
        bool includeMipChain,
        CancellationToken cancellationToken = default)
    {
        Mipmap2D[] sourceMipmaps = texture.Mipmaps;
        if (sourceMipmaps is { Length: > 0 } && sourceMipmaps[0] is not null && sourceMipmaps[0].HasData())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mipmap2D[] residentMipmaps = SelectResidentMipmaps(sourceMipmaps, maxResidentDimension, includeMipChain);
            cancellationToken.ThrowIfCancellationRequested();
            uint residentMaxDimension = residentMipmaps.Length > 0
                ? Math.Max(residentMipmaps[0].Width, residentMipmaps[0].Height)
                : 0u;
            return new TextureStreamingResidentData(
                residentMipmaps,
                sourceMipmaps[0].Width,
                sourceMipmaps[0].Height,
                residentMaxDimension);
        }

        using MagickImage filler = (MagickImage)FillerImage.Clone();
        return BuildResidentDataFromImage(filler, maxResidentDimension, includeMipChain, cancellationToken: cancellationToken);
    }

    internal static TextureStreamingResidentData BuildResidentDataFromImage(
        MagickImage image,
        uint maxResidentDimension,
        bool includeMipChain,
        string? timingLabel = null,
        double decodeMilliseconds = 0.0,
        CancellationToken cancellationToken = default)
    {
        uint sourceWidth = image.Width;
        uint sourceHeight = image.Height;

        cancellationToken.ThrowIfCancellationRequested();
        long cloneStartTimestamp = StartImportedTextureTiming();
        using MagickImage residentImage = (MagickImage)image.Clone();
        double cloneMilliseconds = CompleteImportedTextureTiming(cloneStartTimestamp);

        cancellationToken.ThrowIfCancellationRequested();
        long resizeStartTimestamp = StartImportedTextureTiming();
        ResizePreviewIfNeeded(residentImage, Math.Max(1u, maxResidentDimension));
        double resizeMilliseconds = CompleteImportedTextureTiming(resizeStartTimestamp);

        cancellationToken.ThrowIfCancellationRequested();
        long mipBuildStartTimestamp = StartImportedTextureTiming();
        Mipmap2D[] mipmaps = includeMipChain
            ? GetMipmapsFromImage(residentImage)
            : [new Mipmap2D(residentImage)];
        double mipBuildMilliseconds = CompleteImportedTextureTiming(mipBuildStartTimestamp);

        cancellationToken.ThrowIfCancellationRequested();
        uint residentMaxDimension = mipmaps.Length > 0
            ? Math.Max(mipmaps[0].Width, mipmaps[0].Height)
            : 0u;

        LogImportedTextureTiming(
            timingLabel,
            sourceWidth,
            sourceHeight,
            maxResidentDimension,
            residentMaxDimension,
            includeMipChain,
            mipmaps.Length,
            decodeMilliseconds,
            cloneMilliseconds,
            resizeMilliseconds,
            mipBuildMilliseconds);

        return new TextureStreamingResidentData(
            mipmaps,
            sourceWidth,
            sourceHeight,
            residentMaxDimension);
    }

    internal static void ApplyResidentData(
        XRTexture2D texture,
        TextureStreamingResidentData residentData,
        bool includeMipChain)
    {
        int previousMipmapCount = texture.Mipmaps?.Length ?? 0;
        uint previousWidth = texture.Mipmaps is { Length: > 0 } ? texture.Mipmaps[0].Width : 0u;
        uint previousHeight = texture.Mipmaps is { Length: > 0 } ? texture.Mipmaps[0].Height : 0u;

        // Compute lock mip before overwriting Mipmaps. lockMipLevel = newMipCount - oldMipCount:
        // the mip in the new chain that has the same physical resolution as old mip 0.
        // The progressive upload coroutine seeds this level first so the new GL storage never
        // goes through a fully-black state during promotion.
        int lockMipLevel = -1;
        if (includeMipChain
            && residentData.Mipmaps.Length > 0
            && texture.Mipmaps is { Length: > 0 }
            && residentData.Mipmaps.Length > texture.Mipmaps.Length)
        {
            lockMipLevel = residentData.Mipmaps.Length - texture.Mipmaps.Length;
        }
        texture.StreamingLockMipLevel = lockMipLevel;

        // ApplyResidentData is the dense/tiered upload path. If this texture was
        // previously using sparse residency, drop that state before publishing the
        // new mip chain so the GL uploader does not offset full-chain mip indices
        // by an old sparse resident base.
        if (texture.SparseTextureStreamingEnabled
            || texture.SparseTextureStreamingResidentBaseMipLevel != int.MaxValue
            || texture.SparseTextureStreamingCommittedBaseMipLevel != int.MaxValue
            || texture.SparseTextureStreamingCommittedBytes > 0L)
        {
            TextureRuntimeDiagnostics.LogSparseStateClearedForDenseUpload(
                RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                texture.Name,
                texture.FilePath,
                texture.SparseTextureStreamingResidentBaseMipLevel,
                texture.SparseTextureStreamingCommittedBaseMipLevel,
                texture.SparseTextureStreamingCommittedBytes,
                residentData.ResidentMaxDimension,
                residentData.Mipmaps.Length);
        }

        texture.ClearSparseTextureStreamingState();

        texture.Mipmaps = residentData.Mipmaps;
        texture.AutoGenerateMipmaps = false;
        texture.Resizable = false;
        texture.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        texture.LargestMipmapLevel = 0;
        texture.SmallestAllowedMipmapLevel = includeMipChain && residentData.Mipmaps.Length > 0
            ? residentData.Mipmaps.Length - 1
            : 0;
        texture.MinFilter = includeMipChain && residentData.Mipmaps.Length > 1
            ? ETexMinFilter.LinearMipmapLinear
            : ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;

        uint newWidth = residentData.Mipmaps.Length > 0 ? residentData.Mipmaps[0].Width : 0u;
        uint newHeight = residentData.Mipmaps.Length > 0 ? residentData.Mipmaps[0].Height : 0u;
        RuntimeRenderingHostServices.Current.LogOutput(
            $"[ApplyResidentData] '{texture.Name}' includeMipChain={includeMipChain} " +
            $"previous={previousWidth}x{previousHeight}({previousMipmapCount}mips) -> " +
            $"new={newWidth}x{newHeight}({residentData.Mipmaps.Length}mips) " +
            $"lockMipLevel={lockMipLevel} SmallestAllowedMipmapLevel={texture.SmallestAllowedMipmapLevel}.");
    }

    internal static uint GetPreviewResidentSize(uint sourceMaxDimension)
        => sourceMaxDimension == 0
            ? ImportedPreviewMaxDimensionInternal
            : Math.Min(sourceMaxDimension, ImportedPreviewMaxDimensionInternal);

    internal static uint GetMinimumResidentSize(uint sourceMaxDimension)
        => sourceMaxDimension == 0
            ? 1u
            : Math.Min(sourceMaxDimension, 1u);

    internal static long EstimateResidentBytes(
        uint sourceWidth,
        uint sourceHeight,
        uint residentMaxDimension,
        ESizedInternalFormat format = ESizedInternalFormat.Rgba8)
    {
        if (residentMaxDimension == 0)
            return 0L;

        ScaleResidentDimensions(sourceWidth, sourceHeight, residentMaxDimension, out uint residentWidth, out uint residentHeight);
        if (residentWidth == 0 || residentHeight == 0)
            return 0L;

        bool includeMipChain = residentMaxDimension > GetPreviewResidentSize(Math.Max(sourceWidth, sourceHeight));
        int mipCount = includeMipChain
            ? XRTexture.GetSmallestMipmapLevel(residentWidth, residentHeight) + 1
            : 1;
        int bytesPerPixel = Math.Max(1, RuntimeRenderingHostServices.Current.GetBytesPerPixel(format));

        long totalBytes = 0L;
        for (int mipIndex = 0; mipIndex < mipCount; mipIndex++)
        {
            uint mipWidth = Math.Max(1u, residentWidth >> mipIndex);
            uint mipHeight = Math.Max(1u, residentHeight >> mipIndex);
            totalBytes += (long)mipWidth * mipHeight * bytesPerPixel;
        }

        return totalBytes;
    }

    internal static long CalculateResidentUploadBytes(TextureStreamingResidentData residentData)
    {
        long totalBytes = 0L;
        Mipmap2D[] mipmaps = residentData.Mipmaps;
        for (int i = 0; i < mipmaps.Length; i++)
            totalBytes += mipmaps[i].Data?.Length ?? 0u;

        return totalBytes;
    }

    internal static void ScaleResidentDimensions(uint sourceWidth, uint sourceHeight, uint residentMaxDimension, out uint residentWidth, out uint residentHeight)
    {
        uint resolvedSourceWidth = Math.Max(1u, sourceWidth);
        uint resolvedSourceHeight = Math.Max(1u, sourceHeight);
        uint sourceMaxDimension = Math.Max(resolvedSourceWidth, resolvedSourceHeight);
        if (residentMaxDimension == 0 || sourceMaxDimension == 0 || residentMaxDimension >= sourceMaxDimension)
        {
            residentWidth = resolvedSourceWidth;
            residentHeight = resolvedSourceHeight;
            return;
        }

        double scale = residentMaxDimension / (double)sourceMaxDimension;
        residentWidth = Math.Max(1u, (uint)Math.Round(resolvedSourceWidth * scale));
        residentHeight = Math.Max(1u, (uint)Math.Round(resolvedSourceHeight * scale));
    }

    internal static Mipmap2D[] SelectResidentMipmaps(Mipmap2D[] sourceMipmaps, uint maxResidentDimension, bool includeMipChain)
    {
        if (sourceMipmaps is null || sourceMipmaps.Length == 0)
            return [];

        int baseMipIndex = ResolveResidentBaseMipIndex(sourceMipmaps, maxResidentDimension);
        int resultLength = includeMipChain
            ? sourceMipmaps.Length - baseMipIndex
            : 1;
        if (resultLength <= 0)
            return [];

        Mipmap2D[] result = new Mipmap2D[resultLength];
        for (int i = 0; i < resultLength; i++)
            result[i] = sourceMipmaps[baseMipIndex + i].Clone(cloneImage: true);

        return result;
    }

    internal static int ResolveResidentBaseMipIndex(Mipmap2D[] sourceMipmaps, uint maxResidentDimension)
    {
        if (sourceMipmaps is null || sourceMipmaps.Length == 0)
            return 0;

        uint targetDimension = Math.Max(1u, maxResidentDimension);
        for (int index = 0; index < sourceMipmaps.Length; index++)
        {
            Mipmap2D mip = sourceMipmaps[index];
            if (Math.Max(mip.Width, mip.Height) <= targetDimension)
                return index;
        }

        return sourceMipmaps.Length - 1;
    }
}
