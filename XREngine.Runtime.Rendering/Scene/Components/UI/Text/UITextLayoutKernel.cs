using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace XREngine.Rendering.UI;

/// <summary>Measures and places text using only captured values.</summary>
public static class UITextLayoutKernel
{
    public static UITextLayoutResult Measure(UITextLayoutInput input, Vector2 availableSize)
    {
        if (!float.IsFinite(input.DpiScale) || input.DpiScale <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(input), "DPI scale must be finite and positive.");
        if (input.DpiScale != 1.0f)
        {
            float dpiScale = input.DpiScale;
            UITextLayoutResult unscaled = Measure(input with
            {
                DpiScale = 1.0f,
                Margins = input.Margins / dpiScale
            }, availableSize / dpiScale);
            UITextLayoutGlyph[] scaledGlyphs = unscaled.Glyphs.ToArray();
            for (int i = 0; i < scaledGlyphs.Length; i++)
                scaledGlyphs[i] = scaledGlyphs[i] with { Transform = scaledGlyphs[i].Transform * dpiScale };
            UITextCaretStop[] scaledCarets = unscaled.Carets.ToArray();
            for (int i = 0; i < scaledCarets.Length; i++)
                scaledCarets[i] = scaledCarets[i] with
                {
                    Position = scaledCarets[i].Position * dpiScale,
                    LineHeight = scaledCarets[i].LineHeight * dpiScale
                };
            return UITextLayoutResult.FromOwned(unscaled.IntrinsicSize * dpiScale, scaledGlyphs,
                scaledCarets, unscaled.TextRevision, unscaled.FontRevision, unscaled.HasVisualCarets);
        }
        if (input.ShapedRun is not null)
            return UITextShapedLayoutKernel.Measure(input, availableSize);
        if (input.Text.Length == 0 || input.EmSize <= 0.0f || input.AtlasSize.X <= 0.0f || input.AtlasSize.Y <= 0.0f)
            return UITextLayoutResult.FromOwned(Vector2.Zero, [], [new(0, 0, Vector2.Zero, input.FontSize ?? 0.0f)],
                input.TextRevision, input.FontRevision);

        string text = input.Text;
        int[] clusters = StringInfo.ParseCombiningCharacters(text);
        List<UITextLayoutGlyph> glyphs = new(text.Length);
        List<UITextCaretStop> carets = [new(0, 0, Vector2.Zero, input.FontSize ?? 0.0f)];
        List<int> lineStarts = [0];
        List<float> lineWidths = [];
        List<float> lineBreakHeights = [];
        float scale = (input.FontSize ?? 1.0f) / input.EmSize;
        float x = 0.0f;
        float lineHeight = input.FontSize ?? 0.0f;
        float maxWidth = 0.0f;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        int line = 0;
        int clusterIndex = 0;
        bool atWordStart = true;
        ReadOnlySpan<char> source = text.AsSpan();
        for (int index = 0; index < source.Length;)
        {
            int runeStart = index;
            OperationStatus status = Rune.DecodeFromUtf16(source[index..], out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                index++;
                continue;
            }
            index += consumed;
            while (clusterIndex + 1 < clusters.Length && clusters[clusterIndex + 1] <= runeStart)
                clusterIndex++;
            int clusterStart = clusters[clusterIndex];
            int clusterEnd = clusterIndex + 1 < clusters.Length ? clusters[clusterIndex + 1] : text.Length;

            if (rune.Value == '\n')
            {
                FinishLine();
                atWordStart = true;
                AddCaret(clusterEnd);
                continue;
            }

            if (rune.Value == ' ' || rune.Value == '\t')
                atWordStart = true;
            else if (atWordStart)
            {
                if (input.Wrap == FontGlyphSet.EWrapMode.Word && glyphs.Count > lineStarts[^1] &&
                    availableSize.X > 0.0f && availableSize.X < float.MaxValue * 0.5f &&
                    x * scale + MeasureWordWidth(source[runeStart..]) > availableSize.X)
                {
                    FinishLine();
                    AddCaret(runeStart);
                }
                atWordStart = false;
            }

            if (!input.Runes.TryGetValue(rune.Value, out UIRuneMetrics metric))
            {
                if (rune.Value == ' ')
                    x += input.EmSize * 0.25f + (index < text.Length ? input.Spacing : 0.0f);
                if (index == clusterEnd)
                    AddCaret(clusterEnd);
                continue;
            }
            if (rune.Value == ' ')
            {
                x += MathF.Max(metric.Advance, input.EmSize * 0.25f) + (index < text.Length ? input.Spacing : 0.0f);
                if (index == clusterEnd)
                    AddCaret(clusterEnd);
                continue;
            }

            float left = (x + metric.Bearing.X) * scale;
            if (runeStart == 0 && input.TrimFirstBearing)
            {
                x -= metric.Bearing.X;
                left = 0.0f;
            }
            float width = metric.Size.X * scale;
            if (runeStart == clusterStart && input.Wrap != FontGlyphSet.EWrapMode.None && availableSize.X > 0.0f &&
                availableSize.X < float.MaxValue * 0.5f &&
                MeasureClusterRight(source[clusterStart..clusterEnd], x) > availableSize.X &&
                glyphs.Count > lineStarts[^1])
            {
                FinishLine();
                AddCaret(runeStart);
                left = (x + metric.Bearing.X) * scale;
            }

            float top = metric.Bearing.Y * scale;
            Vector4 rect = metric.AtlasRect;
            Vector4 uvs = new(rect.X / input.AtlasSize.X, rect.Y / input.AtlasSize.Y,
                rect.Z / input.AtlasSize.X, rect.W / input.AtlasSize.Y);
            glyphs.Add(new(new(left, top, width, -metric.Size.Y * scale), uvs,
                clusterStart, clusterEnd - clusterStart, line));
            if (input.FontSize is null)
                lineHeight = MathF.Max(lineHeight, metric.Size.Y * scale);
            minY = MathF.Min(minY, top - metric.Size.Y * scale);
            maxY = MathF.Max(maxY, top);
            x += metric.Advance + (index < text.Length ? input.Spacing : 0.0f);
            if (index == clusterEnd)
                AddCaret(clusterEnd);
        }
        lineWidths.Add(MathF.Max(x * scale, CurrentLineRight()));
        maxWidth = MathF.Max(maxWidth, lineWidths[^1]);

        // The legacy UI text origin is at the bottom. Move earlier lines up.
        int lineCount = lineStarts.Count;
        float[] lineShifts = new float[lineCount];
        for (int i = lineCount - 2; i >= 0; i--)
            lineShifts[i] = lineShifts[i + 1] + lineBreakHeights[i] + input.LineSpacing;
        for (int i = 0; i < glyphs.Count; i++)
        {
            UITextLayoutGlyph glyph = glyphs[i];
            Vector4 t = glyph.Transform;
            t.Y += lineShifts[glyph.Line];
            glyphs[i] = glyph with { Transform = t };
        }
        for (int i = 0; i < carets.Count; i++)
        {
            UITextCaretStop caret = carets[i];
            carets[i] = caret with
            {
                Position = new(caret.Position.X, caret.Position.Y + lineShifts[caret.Line]),
                LineHeight = caret.Line < lineBreakHeights.Count ? lineBreakHeights[caret.Line] : lineHeight
            };
        }

        float intrinsicHeight = glyphs.Count == 0 ? lineShifts[0] :
            MathF.Max(0.0f, maxY - minY + lineShifts[0]);
        Vector2 intrinsic = new(maxWidth, intrinsicHeight);
        if (input.FontSize is null && intrinsic.X > 0.0f && intrinsic.Y > 0.0f &&
            (availableSize.X < float.MaxValue * 0.5f || availableSize.Y < float.MaxValue * 0.5f))
        {
            float fit = MathF.Min(availableSize.X / intrinsic.X, availableSize.Y / intrinsic.Y);
            if (float.IsFinite(fit) && fit > 0.0f)
            {
                for (int i = 0; i < glyphs.Count; i++)
                {
                    UITextLayoutGlyph glyph = glyphs[i];
                    glyphs[i] = glyph with { Transform = glyph.Transform * fit };
                }
                intrinsic *= fit;
                for (int i = 0; i < lineWidths.Count; i++)
                    lineWidths[i] *= fit;
                for (int i = 0; i < carets.Count; i++)
                {
                    UITextCaretStop caret = carets[i];
                    carets[i] = caret with { Position = caret.Position * fit, LineHeight = caret.LineHeight * fit };
                }
            }
        }
        float regionWidth = availableSize.X < float.MaxValue * 0.5f ? availableSize.X : intrinsic.X;
        float regionHeight = availableSize.Y < float.MaxValue * 0.5f ? availableSize.Y : intrinsic.Y;
        float verticalOffset = input.VerticalAlignment switch
        {
            EVerticalAlignment.Top => regionHeight - intrinsic.Y - input.Margins.W,
            EVerticalAlignment.Center => (regionHeight - intrinsic.Y - input.Margins.W) * 0.5f,
            _ => 0.0f
        };
        for (int i = 0; i < glyphs.Count; i++)
        {
            UITextLayoutGlyph glyph = glyphs[i];
            float horizontalOffset = input.HorizontalAlignment switch
            {
                EHorizontalAlignment.Center => (regionWidth - lineWidths[glyph.Line] - input.Margins.Z) * 0.5f,
                EHorizontalAlignment.Right => regionWidth - intrinsic.X - input.Margins.Z,
                _ => 0.0f
            };
            Vector4 t = glyph.Transform;
            t.X += horizontalOffset;
            t.Y += verticalOffset;
            glyphs[i] = glyph with { Transform = t };
        }
        for (int i = 0; i < carets.Count; i++)
        {
            UITextCaretStop caret = carets[i];
            float horizontalOffset = input.HorizontalAlignment switch
            {
                EHorizontalAlignment.Center => (regionWidth - lineWidths[caret.Line] - input.Margins.Z) * 0.5f,
                EHorizontalAlignment.Right => regionWidth - intrinsic.X - input.Margins.Z,
                _ => 0.0f
            };
            carets[i] = caret with { Position = caret.Position + new Vector2(horizontalOffset, verticalOffset) };
        }
        return UITextLayoutResult.FromOwned(intrinsic, [.. glyphs], [.. carets], input.TextRevision, input.FontRevision);

        float CurrentLineRight()
        {
            float right = 0.0f;
            for (int i = lineStarts[^1]; i < glyphs.Count; i++)
                right = MathF.Max(right, glyphs[i].Transform.X + glyphs[i].Transform.Z);
            return right;
        }

        void FinishLine()
        {
            float width = MathF.Max(x * scale, CurrentLineRight());
            lineWidths.Add(width);
            maxWidth = MathF.Max(maxWidth, width);
            lineBreakHeights.Add(lineHeight);
            lineStarts.Add(glyphs.Count);
            line++;
            x = 0.0f;
            if (input.FontSize is null)
                lineHeight = 0.0f;
        }

        void AddCaret(int utf16Offset)
        {
            carets.Add(new(utf16Offset, line, new Vector2(x * scale, 0.0f), lineHeight));
        }

        float MeasureClusterRight(ReadOnlySpan<char> cluster, float startX)
        {
            float advance = startX;
            float right = startX * scale;
            for (int offset = 0; offset < cluster.Length;)
            {
                if (Rune.DecodeFromUtf16(cluster[offset..], out Rune value, out int length) != OperationStatus.Done)
                {
                    offset++;
                    continue;
                }
                offset += length;
                if (!input.Runes.TryGetValue(value.Value, out UIRuneMetrics metrics))
                    continue;
                right = MathF.Max(right, (advance + metrics.Bearing.X + metrics.Size.X) * scale);
                advance += metrics.Advance + (offset < cluster.Length ? input.Spacing : 0);
            }
            return right;
        }

        float MeasureWordWidth(ReadOnlySpan<char> remaining)
        {
            float advance = 0.0f;
            float right = 0.0f;
            for (int i = 0; i < remaining.Length;)
            {
                if (Rune.DecodeFromUtf16(remaining[i..], out Rune wordRune, out int consumed) != OperationStatus.Done)
                {
                    i++;
                    continue;
                }
                if (Rune.IsWhiteSpace(wordRune))
                    break;
                i += consumed;
                if (!input.Runes.TryGetValue(wordRune.Value, out UIRuneMetrics metric))
                    continue;
                right = MathF.Max(right, (advance + metric.Bearing.X + metric.Size.X) * scale);
                advance += metric.Advance + (i < remaining.Length ? input.Spacing : 0.0f);
            }
            return right;
        }
    }
}
