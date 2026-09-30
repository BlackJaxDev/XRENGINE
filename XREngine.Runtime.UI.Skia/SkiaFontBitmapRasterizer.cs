using System.Numerics;
using SkiaSharp;
using XREngine.Rendering;

namespace XREngine.Runtime.UI.Skia;

/// <summary>Renders authoring fonts with Skia and writes a cooked PNG atlas.</summary>
public sealed class SkiaFontBitmapRasterizer : IFontBitmapRasterizer
{
    private const int AtlasPadding = 8;

    public FontBitmapAtlasResult Rasterize(string fontPath, IReadOnlyList<string> characters, string outputAtlasPath, float textSize)
    {
        using SKTypeface typeface = SKTypeface.FromFile(fontPath)
            ?? throw new InvalidDataException($"Unable to load font '{fontPath}'.");
        using SKPaint paint = new()
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsDither = true,
            BlendMode = SKBlendMode.SrcOver,
            IsAntialias = true,
        };
        using SKFont font = new(typeface, textSize)
        {
            BaselineSnap = true,
            Edging = SKFontEdging.Antialias,
            ForceAutoHinting = true,
            Subpixel = false,
            Hinting = SKFontHinting.Full,
        };

        List<(string Character, FontGlyphSet.Glyph Glyph)> glyphInfos = [];
        List<SKBitmap> glyphBitmaps = [];
        try
        {
            foreach (string character in characters)
            {
                ushort[] glyphIndices = new ushort[character.Length];
                font.GetGlyphs(character.AsSpan(), glyphIndices.AsSpan());
                if (glyphIndices.Length == 0 || glyphIndices[0] == 0)
                    continue;

                float[] widths = new float[glyphIndices.Length];
                SKRect[] bounds = new SKRect[glyphIndices.Length];
                font.GetGlyphWidths(glyphIndices, widths.AsSpan(), bounds.AsSpan(), paint);
                SKRect glyphBounds = bounds[0];
                int width = Math.Max(1, (int)Math.Ceiling(glyphBounds.Width));
                int height = Math.Max(1, (int)Math.Ceiling(glyphBounds.Height));
                float advance = widths[0] > 0.0f ? widths[0] : width;

                SKBitmap bitmap = new(width, height);
                using (SKCanvas canvas = new(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawText(character, -glyphBounds.Left, -glyphBounds.Top, SKTextAlign.Left, font, paint);
                }

                glyphBitmaps.Add(bitmap);
                glyphInfos.Add((character, new FontGlyphSet.Glyph(
                    new Vector2(width, height),
                    new Vector2(-glyphBounds.Left, -glyphBounds.Top))
                {
                    AtlasSize = new Vector2(width, height),
                    AdvanceX = advance,
                }));
            }

            if (glyphInfos.Count == 0)
                throw new InvalidDataException($"Font '{fontPath}' contains none of the requested glyphs.");

            int glyphsPerRow = (int)Math.Ceiling(Math.Sqrt(glyphInfos.Count));
            int maxGlyphWidth = 0;
            int maxGlyphHeight = 0;
            foreach (var (_, glyph) in glyphInfos)
            {
                maxGlyphWidth = Math.Max(maxGlyphWidth, (int)Math.Ceiling(glyph.Size.X));
                maxGlyphHeight = Math.Max(maxGlyphHeight, (int)Math.Ceiling(glyph.Size.Y));
            }

            int cellWidth = maxGlyphWidth + AtlasPadding * 2;
            int cellHeight = maxGlyphHeight + AtlasPadding * 2;
            int atlasWidth = cellWidth * glyphsPerRow;
            int atlasHeight = cellHeight * (int)Math.Ceiling((double)glyphInfos.Count / glyphsPerRow);
            using SKBitmap atlasBitmap = new(atlasWidth, atlasHeight);
            using (SKCanvas atlasCanvas = new(atlasBitmap))
            {
                atlasCanvas.Clear(SKColors.Transparent);
                for (int i = 0; i < glyphBitmaps.Count; i++)
                {
                    int x = (i % glyphsPerRow) * cellWidth + AtlasPadding;
                    int y = (i / glyphsPerRow) * cellHeight + AtlasPadding;
                    atlasCanvas.DrawBitmap(glyphBitmaps[i], x, y, SKSamplingOptions.Default, null);
                    glyphInfos[i].Glyph.Position = new Vector2(x, y);
                }
            }

            string? outputDirectory = Path.GetDirectoryName(outputAtlasPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);
            using (SKImage image = SKImage.FromBitmap(atlasBitmap))
            using (SKData data = image.Encode(SKEncodedImageFormat.Png, 100))
            using (FileStream stream = new(outputAtlasPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                data.SaveTo(stream);

            return new FontBitmapAtlasResult(glyphInfos.ToDictionary(item => item.Character, item => item.Glyph));
        }
        finally
        {
            foreach (SKBitmap bitmap in glyphBitmaps)
                bitmap.Dispose();
        }
    }
}
