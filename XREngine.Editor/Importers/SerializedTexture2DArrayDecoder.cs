using System.Buffers.Binary;
using System.Globalization;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

/// <summary>
/// Decodes inline Unity Texture2DArray image data in supported block formats.
/// </summary>
internal static class SerializedTexture2DArrayDecoder
{
    private const string ArrayHeader = "Texture2DArray:";
    private const string PixelDataField = "  _typelessdata: ";

    /// <summary>
    /// Reads array layers and authored mip levels from a Unity YAML asset.
    /// </summary>
    public static XRTexture2DArray DecodeFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        bool isArray = false;
        int width = 0;
        int height = 0;
        int depth = 0;
        int mipCount = 0;
        int format = -1;
        int colorSpace = -1;
        int dataSize = -1;
        int filterMode = 1;
        int anisotropy = 1;
        int wrapU = 0;
        int wrapV = 0;
        int wrapW = 0;
        float mipBias = 0.0f;
        byte[]? imageData = null;
        long streamSize = 0;
        string? streamPath = null;

        foreach (string line in File.ReadLines(path))
        {
            if (line == ArrayHeader)
            {
                isArray = true;
                continue;
            }
            if (!isArray)
                continue;

            if (line.StartsWith(PixelDataField, StringComparison.Ordinal))
            {
                ReadOnlySpan<char> hex = line.AsSpan(PixelDataField.Length).Trim();
                try
                {
                    imageData = Convert.FromHexString(hex);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("Texture2DArray _typelessdata is not valid hexadecimal image data.", ex);
                }
                continue;
            }

            if (TryReadInt(line, "  m_Width: ", out int value)) width = value;
            else if (TryReadInt(line, "  m_Height: ", out value)) height = value;
            else if (TryReadInt(line, "  m_Depth: ", out value)) depth = value;
            else if (TryReadInt(line, "  m_MipCount: ", out value)) mipCount = value;
            else if (TryReadInt(line, "  m_Format: ", out value)) format = value;
            else if (TryReadInt(line, "  m_ColorSpace: ", out value)) colorSpace = value;
            else if (TryReadInt(line, "  m_DataSize: ", out value)) dataSize = value;
            else if (TryReadInt(line, "    m_FilterMode: ", out value)) filterMode = value;
            else if (TryReadInt(line, "    m_Aniso: ", out value)) anisotropy = value;
            else if (TryReadInt(line, "    m_WrapU: ", out value)) wrapU = value;
            else if (TryReadInt(line, "    m_WrapV: ", out value)) wrapV = value;
            else if (TryReadInt(line, "    m_WrapW: ", out value)) wrapW = value;
            else if (TryReadFloat(line, "    m_MipBias: ", out float bias)) mipBias = bias;
            else if (TryReadLong(line, "    size: ", out long size)) streamSize = size;
            else if (line.StartsWith("    path: ", StringComparison.Ordinal)) streamPath = line[10..].Trim();
        }

        if (!isArray)
            throw new InvalidDataException("The Unity asset does not contain a Texture2DArray document.");
        if (width <= 0 || height <= 0 || depth <= 0 || mipCount <= 0)
            throw new InvalidDataException($"Texture2DArray has invalid dimensions or mip count: {width}x{height}x{depth}, {mipCount} mips.");
        if (mipCount > 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(width, height)))
            throw new InvalidDataException($"Texture2DArray has {mipCount} mip levels, which exceeds its {width}x{height} dimensions.");
        if (format is not (96 or 97 or 98 or 99 or 100 or 101))
            throw new InvalidDataException($"Texture2DArray GraphicsFormat {format} is not supported. Supported formats are DXT1, DXT3, and DXT5, each in sRGB or UNorm.");
        if (colorSpace is not (0 or 1))
            throw new InvalidDataException($"Texture2DArray color space {colorSpace} is not supported.");
        bool isSrgb = format is 96 or 98 or 100;
        if (isSrgb != (colorSpace == 1))
            throw new InvalidDataException($"Texture2DArray GraphicsFormat {format} and color space {colorSpace} disagree.");
        if (wrapW != 0)
            throw new InvalidDataException($"Texture2DArray layer wrap mode {wrapW} is not supported.");
        if (filterMode is not (0 or 1 or 2))
            throw new InvalidDataException($"Texture2DArray filter mode {filterMode} is not supported.");
        if (wrapU is < 0 or > 3 || wrapV is < 0 or > 3)
            throw new InvalidDataException($"Texture2DArray UV wrap modes {wrapU} and {wrapV} are not supported.");
        if (anisotropy < 0 || !float.IsFinite(mipBias))
            throw new InvalidDataException("Texture2DArray sampler anisotropy or mip bias is invalid.");
        if (streamSize != 0)
            throw new InvalidDataException($"Texture2DArray streamed image data is not supported (size {streamSize}, path '{streamPath}').");
        if (imageData is null || imageData.Length == 0)
            throw new InvalidDataException($"Texture2DArray has no inline image data. Streamed image data is not supported (size {streamSize}, path '{streamPath}').");
        if (dataSize != imageData.Length)
            throw new InvalidDataException($"Texture2DArray declares {dataSize} image bytes but contains {imageData.Length}.");

        int blockSize = format is 96 or 97 ? 8 : 16;
        int[] mipSizes = new int[mipCount];
        int layerSize = 0;
        for (int mip = 0; mip < mipCount; ++mip)
        {
            int mipWidth = Math.Max(1, width >> mip);
            int mipHeight = Math.Max(1, height >> mip);
            mipSizes[mip] = checked(((mipWidth + 3) / 4) * ((mipHeight + 3) / 4) * blockSize);
            layerSize = checked(layerSize + mipSizes[mip]);
        }
        int requiredSize = checked(layerSize * depth);
        if (imageData.Length != requiredSize)
            throw new InvalidDataException($"Texture2DArray image data has {imageData.Length} bytes; {requiredSize} are required for {depth} layers and {mipCount} mips in GraphicsFormat {format}.");

        XRTexture2D[] layers = new XRTexture2D[depth];
        try
        {
            int layerOffset = 0;
            for (int layer = 0; layer < depth; ++layer)
            {
                Mipmap2D[] mips = new Mipmap2D[mipCount];
                try
                {
                    int mipOffset = layerOffset;
                    for (int mip = 0; mip < mipCount; ++mip)
                    {
                        int mipWidth = Math.Max(1, width >> mip);
                        int mipHeight = Math.Max(1, height >> mip);
                        byte[] pixels = DecodeBlocks(imageData.AsSpan(mipOffset, mipSizes[mip]), mipWidth, mipHeight, format);
                        mips[mip] = new Mipmap2D((uint)mipWidth, (uint)mipHeight, pixels);
                        mipOffset += mipSizes[mip];
                    }

                    XRTexture2D frame = new((uint)width, (uint)height)
                    {
                        Mipmaps = mips,
                        SizedInternalFormat = isSrgb
                            ? ESizedInternalFormat.Srgb8Alpha8
                            : ESizedInternalFormat.Rgba8,
                        ImportedColorSpace = isSrgb
                            ? ETextureColorSpace.Srgb
                            : ETextureColorSpace.Linear,
                        AutoGenerateMipmaps = false,
                        SmallestAllowedMipmapLevel = mipCount - 1,
                        Resizable = false,
                        UWrap = MapWrap(wrapU),
                        VWrap = MapWrap(wrapV),
                        MaxAnisotropy = Math.Max(1, anisotropy),
                        LodBias = mipBias,
                        MagFilter = filterMode == 0 ? ETexMagFilter.Nearest : ETexMagFilter.Linear,
                        MinFilter = filterMode switch
                        {
                            0 => ETexMinFilter.NearestMipmapNearest,
                            1 => ETexMinFilter.LinearMipmapNearest,
                            _ => ETexMinFilter.LinearMipmapLinear,
                        },
                    };
                    layers[layer] = frame;
                }
                catch
                {
                    foreach (Mipmap2D? mip in mips)
                        mip?.Data?.Dispose();
                    throw;
                }
                layerOffset += layerSize;
            }

            return new XRTexture2DArray(layers)
            {
                SizedInternalFormat = isSrgb
                    ? ESizedInternalFormat.Srgb8Alpha8
                    : ESizedInternalFormat.Rgba8,
                AutoGenerateMipmaps = false,
                SmallestAllowedMipmapLevel = mipCount - 1,
                Resizable = false,
            };
        }
        catch
        {
            foreach (XRTexture2D? frame in layers)
            {
                if (frame is null)
                    continue;
                foreach (Mipmap2D mip in frame.Mipmaps)
                    mip.Data?.Dispose();
                frame.Destroy(true);
            }
            throw;
        }
    }

    private static byte[] DecodeBlocks(ReadOnlySpan<byte> source, int width, int height, int format)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        int blockBytes = format is 96 or 97 ? 8 : 16;
        int blocksWide = (width + 3) / 4;
        int blocksHigh = (height + 3) / 4;
        Span<byte> palette = stackalloc byte[16];
        Span<byte> alphaPalette = stackalloc byte[8];
        for (int blockY = 0; blockY < blocksHigh; ++blockY)
        {
            for (int blockX = 0; blockX < blocksWide; ++blockX)
            {
                ReadOnlySpan<byte> block = source.Slice((blockY * blocksWide + blockX) * blockBytes, blockBytes);
                ReadOnlySpan<byte> color = blockBytes == 8 ? block : block[8..];
                ushort endpoint0 = BinaryPrimitives.ReadUInt16LittleEndian(color);
                ushort endpoint1 = BinaryPrimitives.ReadUInt16LittleEndian(color[2..]);
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(color[4..]);
                palette.Clear();
                WriteRgb565(endpoint0, palette);
                WriteRgb565(endpoint1, palette[4..]);
                palette[3] = palette[7] = 255;
                if (endpoint0 > endpoint1 || blockBytes == 16)
                {
                    for (int channel = 0; channel < 3; ++channel)
                    {
                        palette[8 + channel] = (byte)((2 * palette[channel] + palette[4 + channel]) / 3);
                        palette[12 + channel] = (byte)((palette[channel] + 2 * palette[4 + channel]) / 3);
                    }
                    palette[11] = palette[15] = 255;
                }
                else
                {
                    for (int channel = 0; channel < 3; ++channel)
                        palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
                    palette[11] = 255;
                }

                ulong alphaBits = blockBytes == 16
                    ? (ulong)block[2] | ((ulong)block[3] << 8) | ((ulong)block[4] << 16) |
                      ((ulong)block[5] << 24) | ((ulong)block[6] << 32) | ((ulong)block[7] << 40)
                    : 0;
                if (format is 100 or 101)
                    WriteDxt5AlphaPalette(block[0], block[1], alphaPalette);

                for (int y = 0; y < 4; ++y)
                {
                    int pixelY = blockY * 4 + y;
                    if (pixelY >= height)
                        break;
                    for (int x = 0; x < 4; ++x)
                    {
                        int pixelX = blockX * 4 + x;
                        if (pixelX >= width)
                            break;
                        int localIndex = y * 4 + x;
                        int paletteIndex = (int)((indices >> (2 * localIndex)) & 3);
                        int pixelOffset = (pixelY * width + pixelX) * 4;
                        palette.Slice(paletteIndex * 4, 4).CopyTo(pixels.AsSpan(pixelOffset, 4));
                        if (format is 98 or 99)
                        {
                            int nibble = (block[localIndex / 2] >> (4 * (localIndex & 1))) & 15;
                            pixels[pixelOffset + 3] = (byte)(nibble * 17);
                        }
                        else if (format is 100 or 101)
                        {
                            int alphaIndex = (int)((alphaBits >> (3 * localIndex)) & 7);
                            pixels[pixelOffset + 3] = alphaPalette[alphaIndex];
                        }
                    }
                }
            }
        }
        return pixels;
    }

    private static void WriteRgb565(ushort encoded, Span<byte> output)
    {
        int red = (encoded >> 11) & 31;
        int green = (encoded >> 5) & 63;
        int blue = encoded & 31;
        output[0] = (byte)((red << 3) | (red >> 2));
        output[1] = (byte)((green << 2) | (green >> 4));
        output[2] = (byte)((blue << 3) | (blue >> 2));
    }

    private static void WriteDxt5AlphaPalette(byte endpoint0, byte endpoint1, Span<byte> output)
    {
        output[0] = endpoint0;
        output[1] = endpoint1;
        if (endpoint0 > endpoint1)
        {
            for (int i = 1; i <= 6; ++i)
                output[i + 1] = (byte)(((7 - i) * endpoint0 + i * endpoint1 + 3) / 7);
        }
        else
        {
            for (int i = 1; i <= 4; ++i)
                output[i + 1] = (byte)(((5 - i) * endpoint0 + i * endpoint1 + 2) / 5);
            output[6] = 0;
            output[7] = 255;
        }
    }

    private static ETexWrapMode MapWrap(int source)
        => source switch
        {
            0 => ETexWrapMode.Repeat,
            1 => ETexWrapMode.ClampToEdge,
            _ => ETexWrapMode.MirroredRepeat,
        };

    private static bool TryReadInt(string line, string prefix, out int value)
    {
        value = 0;
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadLong(string line, string prefix, out long value)
    {
        value = 0;
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
               long.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadFloat(string line, string prefix, out float value)
    {
        value = 0;
        return line.StartsWith(prefix, StringComparison.Ordinal) &&
               float.TryParse(line.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
