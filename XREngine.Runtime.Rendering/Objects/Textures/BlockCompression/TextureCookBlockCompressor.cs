using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Converts the RGBA8 mip chain of a texture cook into block-compressed mips before the
/// streaming cache stores it.
/// </summary>
/// <remarks>
/// The cook stores BC7 UNORM bits for color, data, and normal maps, so every channel
/// and the current shader sampling stay the same. The resident format later applies the
/// texture color space (BC7 sRGB for sRGB textures). The auxiliary log
/// <see cref="LogFileName"/> records the result of each cook in every build configuration.
/// </remarks>
internal static class TextureCookBlockCompressor
{
    /// <summary>Auxiliary log that records the cooked format and sizes of each texture.</summary>
    internal const string LogFileName = "texture-compression.log";

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
    internal static Mipmap2D[] CompressForCache(string sourceFilePath, Mipmap2D[] mipmaps)
    {
        if (mipmaps is not { Length: > 0 } || EffectiveMode != ETextureCookCompression.Bc7)
            return mipmaps;

        ITextureBlockEncoder encoder = TextureBlockEncoderServices.Current!;
        if (!IsSupportedSource(mipmaps, out string unsupported))
        {
            Debug.WriteAuxiliaryLog(LogFileName, $"{sourceFilePath}: stays RGBA8 ({unsupported}).");
            return mipmaps;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long sourceBytes = 0;
        long compressedBytes = 0;
        Mipmap2D[] compressed = new Mipmap2D[mipmaps.Length];
        for (int index = 0; index < mipmaps.Length; index++)
        {
            Mipmap2D mip = mipmaps[index];
            byte[] blocks = new byte[TextureBlockFormats.GetImageBytes(ESizedInternalFormat.Bc7Unorm, mip.Width, mip.Height)];
            if (!encoder.TryEncodeBc7(mip.Data!.AsReadOnlySpan(), mip.Width, mip.Height, perceptual: false, blocks, out string failure))
            {
                string message = $"{sourceFilePath}: BC7 encoding of mip {index} ({mip.Width}x{mip.Height}) failed, so the texture stays RGBA8. {failure}";
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
                InternalFormat = EPixelInternalFormat.CompressedRgbaBptcUnorm,
                PixelFormat = EPixelFormat.Rgba,
                PixelType = EPixelType.UnsignedByte,
                Data = new DataSource(blocks),
            };
        }

        double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Debug.WriteAuxiliaryLog(
            LogFileName,
            $"{sourceFilePath}: BC7 {mipmaps[0].Width}x{mipmaps[0].Height} mips={mipmaps.Length} " +
            $"rgba8Bytes={sourceBytes} bc7Bytes={compressedBytes} encodeMs={milliseconds:F1} encoder='{encoder.Description}'");
        return compressed;
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
