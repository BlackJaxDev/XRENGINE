using ImageMagick;
using System.IO;
using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    internal const uint ImportedPreviewMaxDimensionInternal = 64;

    public static IDisposable EnterImportedTextureStreamingScope()
        => ImportedTextureStreamingManager.Instance.EnterScope();

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

    public static ImportedTextureStreamingTelemetry GetImportedTextureStreamingTelemetry()
        => ImportedTextureStreamingManager.Instance.GetTelemetry();

    public static IReadOnlyList<ImportedTextureStreamingTextureTelemetry> GetImportedTextureStreamingTextureTelemetry()
        => ImportedTextureStreamingManager.Instance.GetTrackedTextureTelemetry();

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

    internal static TextureStreamingResidentData BuildResidentDataFromLoadedTexture(
        XRTexture2D texture,
        uint maxResidentDimension,
        bool includeMipChain)
    {
        Mipmap2D[] sourceMipmaps = texture.Mipmaps;
        if (sourceMipmaps is { Length: > 0 } && sourceMipmaps[0] is not null && sourceMipmaps[0].HasData())
        {
            Mipmap2D[] residentMipmaps = SelectResidentMipmaps(sourceMipmaps, maxResidentDimension, includeMipChain);
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
        return BuildResidentDataFromImage(filler, maxResidentDimension, includeMipChain);
    }

    internal static TextureStreamingResidentData BuildResidentDataFromImage(
        MagickImage image,
        uint maxResidentDimension,
        bool includeMipChain)
    {
        uint sourceWidth = image.Width;
        uint sourceHeight = image.Height;

        using MagickImage residentImage = (MagickImage)image.Clone();
        ResizePreviewIfNeeded(residentImage, Math.Max(1u, maxResidentDimension));

        Mipmap2D[] mipmaps = includeMipChain
            ? GetMipmapsFromImage(residentImage)
            : [new Mipmap2D(residentImage)];

        uint residentMaxDimension = mipmaps.Length > 0
            ? Math.Max(mipmaps[0].Width, mipmaps[0].Height)
            : 0u;

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
    }

    internal static uint GetPreviewResidentSize(uint sourceMaxDimension)
        => sourceMaxDimension == 0
            ? ImportedPreviewMaxDimensionInternal
            : Math.Min(sourceMaxDimension, ImportedPreviewMaxDimensionInternal);

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
