using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Converts the RGBA8 mip chain of a texture cook into block-compressed mips before the
/// streaming cache stores it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ETextureCookCompression.Bc7"/> stores BC7 UNORM bits for every texture role.
/// <see cref="ETextureCookCompression.Bcn"/> selects the format from the role and the
/// source pixels (see <see cref="SelectFormat"/>). The resident format later applies the
/// texture color space (BC7 sRGB for sRGB textures).
/// </para>
/// <para>
/// BC5 and BC4 keep the current shader sampling through the image-view swizzle that the
/// renderers apply to them: BC4 samples as (R, R, R, 1), so a gray texture reads the same
/// RGB as before; BC5 samples as (R, G, 0, 0), and the zero alpha tells the shared
/// normal-unpack helpers to rebuild Z from X and Y.
/// </para>
/// <para>
/// The auxiliary log <see cref="LogFileName"/> records the result of each cook in every
/// build configuration.
/// </para>
/// </remarks>
internal static class TextureCookBlockCompressor
{
    /// <summary>Auxiliary log that records the cooked format and sizes of each texture.</summary>
    internal const string LogFileName = "texture-compression.log";

    /// <summary>Largest per-channel difference for a pixel to count as gray.</summary>
    private const int GrayTolerance = 2;

    /// <summary>Largest difference from 1 of the decoded vector length for a pixel to count as a unit normal.</summary>
    private const float UnitNormalTolerance = 0.12f;

    /// <summary>Smallest share of unit-length pixels for a texture to count as a tangent-space normal map.</summary>
    private const float UnitNormalShare = 0.9f;

    /// <summary>Largest number of pixels that the content check reads; larger images are sampled with a stride.</summary>
    private const int MaxAnalyzedPixels = 1 << 18;

    private static int s_missingEncoderLogged;

    /// <summary>
    /// The compression that the cook applies now: the setting (or its environment
    /// override), reduced to <see cref="ETextureCookCompression.None"/> when no encoder is installed.
    /// The streaming cache key includes this value, so caches of different modes never mix.
    /// </summary>
    internal static ETextureCookCompression EffectiveMode
    {
        get
        {
            ETextureCookCompression requested = RequestedMode;
            if (requested == ETextureCookCompression.None)
                return ETextureCookCompression.None;

            if (TextureBlockEncoderServices.Current is not null)
                return requested;

            if (Interlocked.Exchange(ref s_missingEncoderLogged, 1) == 0)
            {
                string message =
                    $"Texture cook compression {requested} was requested, but no texture block encoder is installed, " +
                    "so cooked textures stay RGBA8. Run Tools/Dependencies/Build-Bc7Enc.ps1 to build the encoder.";
                Debug.LogWarning(message);
                Debug.WriteAuxiliaryLog(LogFileName, message);
            }

            return ETextureCookCompression.None;
        }
    }

    private static ETextureCookCompression RequestedMode
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TextureCookCompression);
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw.Trim(), ignoreCase: true, out ETextureCookCompression parsed) &&
                Enum.IsDefined(parsed))
            {
                return parsed;
            }

            return RuntimeEngine.Rendering.Settings.CookedTextureCompression;
        }
    }

    /// <summary>
    /// Returns the mip chain to store in the cache. The method returns
    /// <paramref name="mipmaps"/> unchanged when compression is off, when the source is not
    /// tightly packed RGBA8, or when the encoder fails; it logs the reason for the last two.
    /// </summary>
    /// <param name="usage">The texture role, or null when the cook does not know it (the cook then uses BC7).</param>
    internal static Mipmap2D[] CompressForCache(string sourceFilePath, Mipmap2D[] mipmaps, ETextureImportUsage? usage)
    {
        ETextureCookCompression mode = EffectiveMode;
        if (mipmaps is not { Length: > 0 } || mode == ETextureCookCompression.None)
            return mipmaps;

        ITextureBlockEncoder encoder = TextureBlockEncoderServices.Current!;
        if (!IsSupportedSource(mipmaps, out string unsupported))
        {
            Debug.WriteAuxiliaryLog(LogFileName, $"{sourceFilePath}: stays RGBA8 ({unsupported}).");
            return mipmaps;
        }

        string reason = "BC7 for every role";
        ESizedInternalFormat format = mode == ETextureCookCompression.Bcn
            ? SelectFormat(mipmaps[0], usage, out reason)
            : ESizedInternalFormat.Bc7Unorm;
        EPixelInternalFormat internalFormat = TextureBlockFormats.ToPixelInternalFormat(format);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long sourceBytes = 0;
        long compressedBytes = 0;
        Mipmap2D[] compressed = new Mipmap2D[mipmaps.Length];
        for (int index = 0; index < mipmaps.Length; index++)
        {
            Mipmap2D mip = mipmaps[index];
            byte[] blocks = new byte[TextureBlockFormats.GetImageBytes(format, mip.Width, mip.Height)];
            ReadOnlySpan<byte> pixels = mip.Data!.AsReadOnlySpan();
            string failure;
            bool encoded = format switch
            {
                ESizedInternalFormat.Bc5Unorm => encoder.TryEncodeBc5(pixels, mip.Width, mip.Height, 0u, 1u, blocks, out failure),
                ESizedInternalFormat.Bc4Unorm => encoder.TryEncodeBc4(pixels, mip.Width, mip.Height, 0u, blocks, out failure),
                _ => encoder.TryEncodeBc7(pixels, mip.Width, mip.Height, perceptual: false, blocks, out failure),
            };
            if (!encoded)
            {
                string message = $"{sourceFilePath}: {format} encoding of mip {index} ({mip.Width}x{mip.Height}) failed, so the texture stays RGBA8. {failure}";
                Debug.LogWarning($"[TextureCompression] {message}");
                Debug.WriteAuxiliaryLog(LogFileName, message);
                return mipmaps;
            }

            sourceBytes += mip.Data!.Length;
            compressedBytes += blocks.Length;
            compressed[index] = new Mipmap2D
            {
                Width = mip.Width,
                Height = mip.Height,
                InternalFormat = internalFormat,
                PixelFormat = EPixelFormat.Rgba,
                PixelType = EPixelType.UnsignedByte,
                Data = new DataSource(blocks),
            };
        }

        double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Debug.WriteAuxiliaryLog(
            LogFileName,
            $"{sourceFilePath}: {format} {mipmaps[0].Width}x{mipmaps[0].Height} mips={mipmaps.Length} " +
            $"usage={(usage?.ToString() ?? "unknown")} reason='{reason}' " +
            $"rgba8Bytes={sourceBytes} compressedBytes={compressedBytes} encodeMs={milliseconds:F1} encoder='{encoder.Description}'");
        return compressed;
    }

    /// <summary>
    /// Selects the block format for the role and the base mip pixels:
    /// <list type="bullet">
    /// <item><description>BC5 for a normal-role texture whose pixels decode to unit vectors and whose alpha is opaque.</description></item>
    /// <item><description>BC4 for a data- or normal-role texture that is gray and opaque (for example height and mask maps).</description></item>
    /// <item><description>BC7 for everything else, which includes color, unknown roles, and textures with alpha.</description></item>
    /// </list>
    /// Color textures never use BC4, because BC4 has no sRGB variant.
    /// </summary>
    private static ESizedInternalFormat SelectFormat(Mipmap2D baseMip, ETextureImportUsage? usage, out string reason)
    {
        if (usage is null)
        {
            reason = "role unknown";
            return ESizedInternalFormat.Bc7Unorm;
        }

        if (usage == ETextureImportUsage.Color)
        {
            reason = "color role";
            return ESizedInternalFormat.Bc7Unorm;
        }

        AnalyzePixels(baseMip, out bool opaque, out bool gray, out float unitShare);
        if (!opaque)
        {
            reason = "alpha channel is used";
            return ESizedInternalFormat.Bc7Unorm;
        }

        if (gray)
        {
            reason = "opaque single-channel data";
            return ESizedInternalFormat.Bc4Unorm;
        }

        if (usage == ETextureImportUsage.Normal && unitShare >= UnitNormalShare)
        {
            reason = $"tangent-space normal map (unit share {unitShare:F2})";
            return ESizedInternalFormat.Bc5Unorm;
        }

        reason = usage == ETextureImportUsage.Normal
            ? $"normal role, but pixels are not unit vectors (unit share {unitShare:F2})"
            : "multi-channel data";
        return ESizedInternalFormat.Bc7Unorm;
    }

    /// <summary>
    /// Reads the base mip and reports whether every alpha is 255, whether every pixel is
    /// gray, and the share of pixels whose RGB decodes to a unit vector.
    /// </summary>
    private static void AnalyzePixels(Mipmap2D mip, out bool opaque, out bool gray, out float unitShare)
    {
        ReadOnlySpan<byte> pixels = mip.Data!.AsReadOnlySpan();
        long pixelCount = (long)mip.Width * mip.Height;
        long stride = Math.Max(1L, pixelCount / MaxAnalyzedPixels);
        opaque = true;
        gray = true;
        long analyzed = 0;
        long unit = 0;
        for (long pixel = 0; pixel < pixelCount; pixel += stride)
        {
            int offset = (int)(pixel * 4);
            int r = pixels[offset];
            int g = pixels[offset + 1];
            int b = pixels[offset + 2];
            int a = pixels[offset + 3];
            if (a != 255)
                opaque = false;
            if (Math.Abs(r - g) > GrayTolerance || Math.Abs(g - b) > GrayTolerance)
                gray = false;

            float x = r / 127.5f - 1.0f;
            float y = g / 127.5f - 1.0f;
            float z = b / 127.5f - 1.0f;
            if (MathF.Abs(MathF.Sqrt(x * x + y * y + z * z) - 1.0f) <= UnitNormalTolerance && z > 0.0f)
                unit++;
            analyzed++;
        }

        unitShare = analyzed == 0 ? 0.0f : (float)unit / analyzed;
    }

    /// <summary>Returns false with a reason when a mip is not tightly packed 8-bit RGBA.</summary>
    private static bool IsSupportedSource(Mipmap2D[] mipmaps, out string reason)
    {
        for (int index = 0; index < mipmaps.Length; index++)
        {
            Mipmap2D mip = mipmaps[index];
            if (mip.InternalFormat != EPixelInternalFormat.Rgba8 ||
                mip.PixelFormat != EPixelFormat.Rgba ||
                mip.PixelType != EPixelType.UnsignedByte)
            {
                reason = $"mip {index} is {mip.InternalFormat}/{mip.PixelFormat}/{mip.PixelType}, not RGBA8";
                return false;
            }

            if (mip.Width == 0 || mip.Height == 0 || mip.Data is null || mip.Data.Length < (long)mip.Width * mip.Height * 4)
            {
                reason = $"mip {index} has no complete RGBA8 data";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }
}
