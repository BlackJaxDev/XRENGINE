using System.Numerics;
using System.Text;
using SharpFont;
using XREngine.Data.Rendering;
using XREngine.Imaging;
using XREngine.Rendering;

namespace XREngine.Runtime.Text.FreeType;

/// <summary>Renders source-font glyph coverage with FreeType for portable bitmap font cooking.</summary>
public sealed class FreeTypeFontBitmapRasterizer : IFontBitmapRasterizer
{
    private const int AtlasPadding = 8;
    private const int MaxAtlasDimension = 8192;
    private const int MaxAtlasBytes = 64 * 1024 * 1024;

    public FontBitmapAtlasResult Rasterize(string fontPath, IReadOnlyList<string> characters,
        string outputAtlasPath, float textSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputAtlasPath);
        if (!float.IsFinite(textSize) || textSize is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(textSize));
        if (characters.Count is < 1 or > 65536)
            throw new InvalidDataException("Bitmap font character count is out of range.");

        using Library library = new();
        using Face face = new(library, fontPath);
        face.SetPixelSizes(0, checked((uint)MathF.Round(textSize)));
        List<GlyphBitmap> rendered = new(characters.Count);
        Dictionary<string, FontGlyphSet.Glyph> glyphs = new(StringComparer.Ordinal);
        long area = 0;
        foreach (string character in characters)
        {
            if (string.IsNullOrEmpty(character) || !Rune.TryGetRuneAt(character, 0, out Rune rune) ||
                rune.Utf16SequenceLength != character.Length)
                throw new InvalidDataException("Bitmap font characters must each be one Unicode scalar.");
            if (glyphs.ContainsKey(character) || face.GetCharIndex((uint)rune.Value) == 0)
                continue;

            face.LoadChar((uint)rune.Value, LoadFlags.Render, LoadTarget.Normal);
            GlyphSlot slot = face.Glyph;
            FTBitmap bitmap = slot.Bitmap;
            int width = bitmap.Width;
            int height = bitmap.Rows;
            if (width < 0 || height < 0 || width > MaxAtlasDimension || height > MaxAtlasDimension)
                throw new InvalidDataException("FreeType returned an invalid glyph bitmap size.");
            float advance = (float)slot.Advance.X;
            FontGlyphSet.Glyph glyph = new(new Vector2(width, height),
                new Vector2(slot.BitmapLeft, slot.BitmapTop))
            {
                AdvanceX = advance,
            };
            glyphs.Add(character, glyph);
            byte[] coverage = CopyCoverage(bitmap, width, height);
            rendered.Add(new GlyphBitmap(glyph, width, height, coverage));
            if (width > 0 && height > 0)
                area += (long)(width + AtlasPadding * 2) * (height + AtlasPadding * 2);
        }
        if (glyphs.Count == 0)
            throw new InvalidDataException("Source font contains none of the requested glyphs.");

        int targetWidth = Math.Clamp((int)Math.Ceiling(Math.Sqrt(area)), 256, MaxAtlasDimension);
        int cursorX = AtlasPadding;
        int cursorY = AtlasPadding;
        int rowHeight = 0;
        int usedWidth = 1;
        foreach (GlyphBitmap item in rendered)
        {
            if (item.Width == 0 || item.Height == 0)
                continue;
            int paddedWidth = checked(item.Width + AtlasPadding * 2);
            int paddedHeight = checked(item.Height + AtlasPadding * 2);
            if (paddedWidth > MaxAtlasDimension || paddedHeight > MaxAtlasDimension)
                throw new InvalidDataException("Bitmap font glyph exceeds the atlas dimension limit.");
            if (cursorX + item.Width + AtlasPadding > targetWidth && cursorX > AtlasPadding)
            {
                cursorX = AtlasPadding;
                cursorY = checked(cursorY + rowHeight);
                rowHeight = 0;
            }
            if (cursorY + item.Height + AtlasPadding > MaxAtlasDimension)
                throw new InvalidDataException("Bitmap font atlas exceeds the dimension limit.");
            item.Glyph.Position = new Vector2(cursorX, cursorY);
            cursorX = checked(cursorX + item.Width + AtlasPadding * 2);
            rowHeight = Math.Max(rowHeight, paddedHeight);
            usedWidth = Math.Max(usedWidth, cursorX - AtlasPadding);
        }
        int atlasWidth = Math.Min(MaxAtlasDimension, Math.Max(1, usedWidth));
        int atlasHeight = Math.Max(1, checked(cursorY + rowHeight));
        if (atlasHeight > MaxAtlasDimension || (long)atlasWidth * atlasHeight > MaxAtlasBytes)
            throw new InvalidDataException("Bitmap font atlas exceeds the portable payload limit.");
        byte[] rgba = new byte[checked(atlasWidth * atlasHeight * 4)];
        foreach (GlyphBitmap item in rendered)
        {
            int x = (int)item.Glyph.Position.X;
            int y = (int)item.Glyph.Position.Y;
            for (int row = 0; row < item.Height; row++)
            {
                for (int column = 0; column < item.Width; column++)
                {
                    int target = ((y + row) * atlasWidth + x + column) * 4;
                    rgba[target] = rgba[target + 1] = rgba[target + 2] = 255;
                    rgba[target + 3] = item.Coverage[row * item.Width + column];
                }
            }
        }
        using RuntimeImage image = new((uint)atlasWidth, (uint)atlasHeight,
            EPixelFormat.Rgba, EPixelType.UnsignedByte, rgba);
        byte[] png = RuntimeImageCodecs.Require().EncodePng(image);
        string? directory = Path.GetDirectoryName(outputAtlasPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(outputAtlasPath, png);
        return new FontBitmapAtlasResult(glyphs);
    }

    private static byte[] CopyCoverage(FTBitmap bitmap, int width, int height)
    {
        if (width == 0 || height == 0)
            return [];
        int pitch = bitmap.Pitch;
        int stride = Math.Abs(pitch);
        byte[] source = bitmap.BufferData;
        if ((long)stride * height > source.Length)
            throw new InvalidDataException("FreeType returned a truncated glyph bitmap.");
        byte[] pixels = new byte[checked(width * height)];
        switch (bitmap.PixelMode)
        {
            case PixelMode.Gray:
                if (stride < width || bitmap.GrayLevels < 2)
                    throw new InvalidDataException("FreeType returned invalid grayscale glyph coverage.");
                for (int row = 0; row < height; row++)
                {
                    int sourceRow = pitch < 0 ? height - row - 1 : row;
                    for (int column = 0; column < width; column++)
                    {
                        byte value = source[sourceRow * stride + column];
                        pixels[row * width + column] = bitmap.GrayLevels == 256
                            ? value
                            : (byte)Math.Clamp(value * 255 / (bitmap.GrayLevels - 1), 0, 255);
                    }
                }
                break;
            case PixelMode.Mono:
                if (stride < (width + 7) / 8)
                    throw new InvalidDataException("FreeType returned invalid monochrome glyph coverage.");
                for (int row = 0; row < height; row++)
                {
                    int sourceRow = pitch < 0 ? height - row - 1 : row;
                    for (int column = 0; column < width; column++)
                        pixels[row * width + column] =
                            (source[sourceRow * stride + column / 8] & (0x80 >> (column % 8))) != 0
                                ? (byte)255 : (byte)0;
                }
                break;
            default:
                throw new NotSupportedException($"FreeType bitmap pixel mode '{bitmap.PixelMode}' cannot cook an R8 atlas.");
        }
        return pixels;
    }

    private sealed record GlyphBitmap(FontGlyphSet.Glyph Glyph, int Width, int Height, byte[] Coverage);
}
