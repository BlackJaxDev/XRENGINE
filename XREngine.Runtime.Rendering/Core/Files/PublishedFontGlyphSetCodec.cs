using System.Buffers;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using K4os.Compression.LZ4;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Core.Files;

/// <summary>Portable, path-free cooked bitmap font payload with an owned R8 atlas.</summary>
internal static class PublishedFontGlyphSetCodec
{
    private const uint Magic = 0x21464758; // XGF! in little-endian byte order.
    private const int BrotliVersion = 2;
    private const int Version = 3;
    private const byte RawMipEncoding = 0;
    private const byte Lz4MipEncoding = 1;
    private const int MaxGlyphCount = 65536;
    private const int MaxAtlasDimension = 8192;
    private const int MaxAtlasBytes = 64 * 1024 * 1024;
    private const int MaxPayloadBytes = 4 * 1024 * 1024 - 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Serialize(FontGlyphSet font)
    {
        ArgumentNullException.ThrowIfNull(font);
        XRTexture2D atlas = font.Atlas ?? throw new InvalidDataException("Cooked bitmap font has no atlas.");
        Dictionary<string, FontGlyphSet.Glyph> glyphs = font.Glyphs ?? throw new InvalidDataException("Cooked bitmap font has no glyphs.");
        List<string> characters = font.Characters;
        ValidateFont(font, atlas, glyphs, characters);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((int)font.AtlasType);
        writer.Write(font.DistanceRange);
        writer.Write(font.DistanceRangeMiddle);
        writer.Write(font.LayoutEmSize);
        writer.Write(characters.Count);
        foreach (string character in characters)
            WriteScalar(writer, character);
        writer.Write(glyphs.Count);
        foreach ((string key, FontGlyphSet.Glyph glyph) in glyphs.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            WriteScalar(writer, key);
            WriteVector(writer, glyph.Position);
            WriteVector(writer, glyph.AtlasSize);
            WriteVector(writer, glyph.Size);
            WriteVector(writer, glyph.Bearing);
            writer.Write(glyph.AdvanceX);
        }

        writer.Write(atlas.Width);
        writer.Write(atlas.Height);
        writer.Write((int)atlas.SizedInternalFormat);
        writer.Write((int)atlas.MinFilter);
        writer.Write((int)atlas.MagFilter);
        writer.Write((int)atlas.UWrap);
        writer.Write((int)atlas.VWrap);
        writer.Write(atlas.AutoGenerateMipmaps);
        writer.Write(atlas.Resizable);
        writer.Write(atlas.LargestMipmapLevel);
        writer.Write(atlas.SmallestAllowedMipmapLevel);
        writer.Write(atlas.Mipmaps.Length);
        foreach (Mipmap2D mip in atlas.Mipmaps)
        {
            byte[] bytes = mip.Data?.GetBytes() ?? throw new InvalidDataException("Cooked bitmap atlas has a missing mip payload.");
            byte[] compressed = new byte[LZ4Codec.MaximumOutputSize(bytes.Length)];
            int compressedLength = LZ4Codec.Encode(bytes, compressed, LZ4Level.L00_FAST);
            if (compressedLength <= 0)
                throw new InvalidDataException("Cooked bitmap atlas mip compression failed.");
            bool useLz4 = compressedLength < bytes.Length;
            int encodedLength = useLz4 ? compressedLength : bytes.Length;
            if (stream.Length + sizeof(uint) * 2 + sizeof(int) * 5 + sizeof(byte) + encodedLength > MaxPayloadBytes)
                throw new InvalidDataException("Cooked bitmap font exceeds the browser payload limit.");
            writer.Write(mip.Width);
            writer.Write(mip.Height);
            writer.Write((int)mip.InternalFormat);
            writer.Write((int)mip.PixelFormat);
            writer.Write((int)mip.PixelType);
            writer.Write(bytes.Length);
            writer.Write(useLz4 ? Lz4MipEncoding : RawMipEncoding);
            writer.Write(encodedLength);
            ReadOnlySpan<byte> encoded = useLz4 ? compressed.AsSpan(0, compressedLength) : bytes;
            writer.Write(encoded);
        }
        writer.Flush();
        if (stream.Length > MaxPayloadBytes)
            throw new InvalidDataException("Cooked bitmap font exceeds the portable payload limit.");
        return stream.ToArray();
    }

    public static FontGlyphSet Deserialize(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException("Cooked bitmap font exceeds the portable payload limit.");
        using MemoryStream stream = new(payload, writable: false);
        using BinaryReader reader = new(stream, Utf8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Unsupported cooked bitmap font format or version.");
        int version = reader.ReadInt32();
        if (version is not (BrotliVersion or Version))
            throw new InvalidDataException("Unsupported cooked bitmap font format or version.");
        if (version == BrotliVersion && OperatingSystem.IsBrowser())
            throw new NotSupportedException("BrowserFont.CookedV2RequiresRecook: Brotli-compressed cooked bitmap fonts must be republished for the browser.");
        EFontAtlasType atlasType = (EFontAtlasType)reader.ReadInt32();
        float distanceRange = reader.ReadSingle();
        float distanceRangeMiddle = reader.ReadSingle();
        float layoutEmSize = reader.ReadSingle();
        int characterCount = ReadCount(reader, MaxGlyphCount, "character");
        List<string> characters = new(characterCount);
        for (int index = 0; index < characterCount; index++)
            characters.Add(ReadScalar(reader));
        int glyphCount = ReadCount(reader, MaxGlyphCount, "glyph");
        Dictionary<string, FontGlyphSet.Glyph> glyphs = new(glyphCount, StringComparer.Ordinal);
        for (int index = 0; index < glyphCount; index++)
        {
            string key = ReadScalar(reader);
            FontGlyphSet.Glyph glyph = new()
            {
                Position = ReadVector(reader),
                AtlasSize = ReadVector(reader),
                Size = ReadVector(reader),
                Bearing = ReadVector(reader),
                AdvanceX = reader.ReadSingle(),
            };
            if (!glyphs.TryAdd(key, glyph))
                throw new InvalidDataException($"Duplicate cooked font glyph key '{key}'.");
        }

        uint width = reader.ReadUInt32();
        uint height = reader.ReadUInt32();
        ESizedInternalFormat sizedFormat = (ESizedInternalFormat)reader.ReadInt32();
        ETexMinFilter minFilter = (ETexMinFilter)reader.ReadInt32();
        ETexMagFilter magFilter = (ETexMagFilter)reader.ReadInt32();
        ETexWrapMode uWrap = (ETexWrapMode)reader.ReadInt32();
        ETexWrapMode vWrap = (ETexWrapMode)reader.ReadInt32();
        bool autoMipmaps = reader.ReadBoolean();
        bool resizable = reader.ReadBoolean();
        int largestMipmapLevel = reader.ReadInt32();
        int smallestAllowedMipmapLevel = reader.ReadInt32();
        int mipCount = ReadCount(reader, 14, "atlas mip");
        ValidateHeader(atlasType, distanceRange, distanceRangeMiddle, layoutEmSize, width, height,
            sizedFormat, minFilter, magFilter, uWrap, vWrap, autoMipmaps, resizable,
            largestMipmapLevel, smallestAllowedMipmapLevel, mipCount);

        byte[][] mipPayloads = new byte[mipCount][];
        long totalMipBytes = 0;
        for (int level = 0; level < mipCount; level++)
        {
            uint mipWidth = reader.ReadUInt32();
            uint mipHeight = reader.ReadUInt32();
            EPixelInternalFormat internalFormat = (EPixelInternalFormat)reader.ReadInt32();
            EPixelFormat pixelFormat = (EPixelFormat)reader.ReadInt32();
            EPixelType pixelType = (EPixelType)reader.ReadInt32();
            int byteCount = reader.ReadInt32();
            byte encoding = version == Version ? reader.ReadByte() : byte.MaxValue;
            int encodedLength = reader.ReadInt32();
            long expectedBytes = (long)Math.Max(1u, width >> level) * Math.Max(1u, height >> level);
            if (mipWidth != Math.Max(1u, width >> level) || mipHeight != Math.Max(1u, height >> level) ||
                internalFormat != EPixelInternalFormat.R8 || pixelFormat != EPixelFormat.Red ||
                pixelType != EPixelType.UnsignedByte || byteCount != expectedBytes ||
                (totalMipBytes += byteCount) > MaxAtlasBytes || encodedLength < 1 ||
                encodedLength > MaxPayloadBytes || encodedLength > stream.Length - stream.Position ||
                (version == Version && encoding is not (RawMipEncoding or Lz4MipEncoding)))
                throw new InvalidDataException($"Invalid cooked bitmap atlas mip {level}.");
            byte[] bytes = new byte[byteCount];
            ReadOnlySpan<byte> encoded = payload.AsSpan((int)stream.Position, encodedLength);
            if (version == BrotliVersion)
            {
                using BrotliDecoder decoder = new();
                OperationStatus status = decoder.Decompress(encoded, bytes,
                    out int consumed, out int written);
                if (status != OperationStatus.Done || consumed != encodedLength || written != byteCount)
                    throw new InvalidDataException($"Invalid cooked bitmap atlas mip {level} compression.");
            }
            else if (encoding == RawMipEncoding)
            {
                if (encodedLength != byteCount)
                    throw new InvalidDataException($"Invalid cooked bitmap atlas mip {level} raw length.");
                encoded.CopyTo(bytes);
            }
            else
            {
                if (encodedLength > LZ4Codec.MaximumOutputSize(byteCount) ||
                    LZ4Codec.Decode(encoded, bytes) != byteCount)
                    throw new InvalidDataException($"Invalid cooked bitmap atlas mip {level} LZ4 data.");
            }
            stream.Position += encodedLength;
            mipPayloads[level] = bytes;
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Cooked bitmap font has trailing bytes.");

        Mipmap2D[] mips = new Mipmap2D[mipCount];
        XRTexture2D? atlas = null;
        FontGlyphSet? font = null;
        bool transferred = false;
        try
        {
            for (int level = 0; level < mipCount; level++)
                mips[level] = new Mipmap2D(Math.Max(1u, width >> level), Math.Max(1u, height >> level),
                    EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte,
                    allocateData: false)
                {
                    Data = new DataSource(mipPayloads[level]),
                };
            atlas = new XRTexture2D(width, height, EPixelInternalFormat.R8, EPixelFormat.Red,
                EPixelType.UnsignedByte, allocateData: false)
            {
                Mipmaps = mips,
                SizedInternalFormat = sizedFormat,
                MinFilter = minFilter,
                MagFilter = magFilter,
                UWrap = uWrap,
                VWrap = vWrap,
                AutoGenerateMipmaps = autoMipmaps,
                Resizable = resizable,
                LargestMipmapLevel = largestMipmapLevel,
                SmallestAllowedMipmapLevel = smallestAllowedMipmapLevel,
                PreferSynchronousGpuUpload = true,
            };
            font = new FontGlyphSet(characters, glyphs, atlas)
            {
                AtlasType = atlasType,
                DistanceRange = distanceRange,
                DistanceRangeMiddle = distanceRangeMiddle,
                LayoutEmSize = layoutEmSize,
            };
            font.InstallOwnedCookedBitmapAtlas(atlas);
            transferred = true;
            ValidateFont(font, atlas, glyphs, characters);
            return font;
        }
        catch
        {
            // Failed construction has no caller to release its native mip allocations.
            try { font?.Destroy(now: true); } catch { }
            if (!transferred)
            {
                try { atlas?.Destroy(now: true); } catch { }
                foreach (Mipmap2D? mip in mips)
                    mip?.Data?.Dispose();
            }
            throw;
        }
    }

    private static void ValidateFont(FontGlyphSet font, XRTexture2D atlas,
        Dictionary<string, FontGlyphSet.Glyph> glyphs, List<string> characters)
    {
        if (characters.Count is < 1 or > MaxGlyphCount || glyphs.Count is < 1 or > MaxGlyphCount)
            throw new InvalidDataException("Cooked bitmap font character or glyph count is out of range.");
        foreach (string character in characters)
            ValidateScalar(character);
        foreach ((string key, FontGlyphSet.Glyph glyph) in glyphs)
        {
            ValidateScalar(key);
            if (glyph is null)
                throw new InvalidDataException($"Cooked bitmap font glyph '{key}' is null.");
            Vector2 effectiveAtlasSize = glyph.EffectiveAtlasSize;
            if (!Finite(glyph.Position) || !Finite(glyph.AtlasSize) ||
                !Finite(glyph.Size) || !Finite(glyph.Bearing) || !float.IsFinite(glyph.AdvanceX) ||
                glyph.Position.X < 0 || glyph.Position.Y < 0 || glyph.AtlasSize.X < 0 ||
                glyph.AtlasSize.Y < 0 || glyph.Size.X < 0 || glyph.Size.Y < 0 ||
                (double)glyph.Position.X + effectiveAtlasSize.X > atlas.Width ||
                (double)glyph.Position.Y + effectiveAtlasSize.Y > atlas.Height)
                throw new InvalidDataException($"Cooked bitmap font glyph '{key}' has invalid metrics or atlas bounds.");
        }
        ValidateHeader(font.AtlasType, font.DistanceRange, font.DistanceRangeMiddle, font.LayoutEmSize,
            atlas.Width, atlas.Height, atlas.SizedInternalFormat, atlas.MinFilter, atlas.MagFilter,
            atlas.UWrap, atlas.VWrap, atlas.AutoGenerateMipmaps, atlas.Resizable,
            atlas.LargestMipmapLevel, atlas.SmallestAllowedMipmapLevel, atlas.Mipmaps.Length);
        long totalBytes = 0;
        for (int level = 0; level < atlas.Mipmaps.Length; level++)
        {
            Mipmap2D mip = atlas.Mipmaps[level];
            byte[]? bytes = mip.Data?.GetBytes();
            long expected = (long)Math.Max(1u, atlas.Width >> level) * Math.Max(1u, atlas.Height >> level);
            if (mip.Width != Math.Max(1u, atlas.Width >> level) ||
                mip.Height != Math.Max(1u, atlas.Height >> level) ||
                mip.InternalFormat != EPixelInternalFormat.R8 || mip.PixelFormat != EPixelFormat.Red ||
                mip.PixelType != EPixelType.UnsignedByte || bytes?.Length != expected ||
                (totalBytes += expected) > MaxAtlasBytes)
                throw new InvalidDataException($"Cooked bitmap atlas mip {level} is invalid.");
        }
    }

    private static void ValidateHeader(EFontAtlasType atlasType, float distanceRange,
        float distanceRangeMiddle, float layoutEmSize, uint width, uint height,
        ESizedInternalFormat sizedFormat, ETexMinFilter minFilter, ETexMagFilter magFilter,
        ETexWrapMode uWrap, ETexWrapMode vWrap, bool autoMipmaps, bool resizable,
        int largestMipmapLevel, int smallestAllowedMipmapLevel, int mipCount)
    {
        if (atlasType != EFontAtlasType.Bitmap || !float.IsFinite(distanceRange) || distanceRange != 0 ||
            !float.IsFinite(distanceRangeMiddle) || !float.IsFinite(layoutEmSize) || layoutEmSize <= 0 ||
            width is 0 or > MaxAtlasDimension || height is 0 or > MaxAtlasDimension ||
            (long)width * height > MaxAtlasBytes || sizedFormat != ESizedInternalFormat.R8 ||
            minFilter != ETexMinFilter.LinearMipmapLinear || magFilter != ETexMagFilter.Linear ||
            uWrap != ETexWrapMode.ClampToEdge || vWrap != ETexWrapMode.ClampToEdge ||
            autoMipmaps || resizable || largestMipmapLevel != 0 ||
            mipCount != XRTexture.GetSmallestMipmapLevel(width, height) + 1 ||
            smallestAllowedMipmapLevel != mipCount - 1)
            throw new InvalidDataException("Cooked bitmap font atlas metadata is unsupported or invalid.");
    }

    private static int ReadCount(BinaryReader reader, int maximum, string description)
    {
        int count = reader.ReadInt32();
        if (count is < 1 || count > maximum)
            throw new InvalidDataException($"Cooked bitmap font {description} count is out of range.");
        return count;
    }

    private static void WriteScalar(BinaryWriter writer, string value)
    {
        ValidateScalar(value);
        byte[] bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadScalar(BinaryReader reader)
    {
        int byteCount = reader.ReadInt32();
        if (byteCount is < 1 or > 4)
            throw new InvalidDataException("Cooked bitmap font has an invalid Unicode key length.");
        byte[] bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
            throw new EndOfStreamException("Cooked bitmap font Unicode key is truncated.");
        string value;
        try
        {
            value = Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Cooked bitmap font has an invalid Unicode key.", exception);
        }
        ValidateScalar(value);
        return value;
    }

    private static void ValidateScalar(string value)
    {
        if (string.IsNullOrEmpty(value) || !Rune.TryGetRuneAt(value, 0, out Rune rune) ||
            rune.Utf16SequenceLength != value.Length)
            throw new InvalidDataException("Cooked bitmap font glyph keys must each be one Unicode scalar.");
    }

    private static void WriteVector(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    private static Vector2 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
