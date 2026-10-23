using System.Globalization;
using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Place one analyzed, shaped line from frozen glyph values.</summary>
internal static class UITextShapedLayoutKernel
{
    internal static bool HasLineBreak(string text)
    {
        foreach (char value in text)
            if (value is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029')
                return true;
        return false;
    }

    internal static UITextLayoutResult Measure(UITextLayoutInput input, Vector2 availableSize)
    {
        UITextShapedRunInput run = input.ShapedRun ??
            throw new ArgumentException("A shaped run is required.", nameof(input));
        if (input.Wrap != FontGlyphSet.EWrapMode.None || HasLineBreak(input.Text))
            throw new NotSupportedException("UI.ShapedLineAnalysisUnavailable: wrapped and multiline shaping require line analysis.");
        if (input.EmSize <= 0.0f || input.AtlasSize.X <= 0.0f || input.AtlasSize.Y <= 0.0f)
            throw new InvalidOperationException("UI.ShapedFontMetricsInvalid: the atlas or em size is empty.");

        float scale = (input.FontSize ?? 1.0f) / input.EmSize;
        UITextLayoutGlyph[] placed = new UITextLayoutGlyph[run.Glyphs.Length];
        List<UITextCaretStop> carets = new(run.Glyphs.Length * 2 + 1);
        float x = 0.0f;
        float maxRight = 0.0f;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        bool rtl = run.Direction == UITextDirection.RightToLeft;
        for (int i = 0; i < run.Glyphs.Length;)
        {
            UITextShapedGlyph first = run.Glyphs[i];
            int start = first.ClusterStart;
            int end = checked(start + first.ClusterLength);
            if (start < 0 || end > input.Text.Length || end <= start)
                throw new InvalidOperationException("UI.ShapedClusterInvalid: a glyph has no source cluster.");
            AddCaret(rtl ? end : start, rtl ? UITextCaretAffinity.Upstream : UITextCaretAffinity.Downstream);
            do
            {
                UITextShapedGlyph glyph = run.Glyphs[i];
                UIRuneMetrics metric = glyph.Metrics;
                float left = x + (glyph.Offset.X + metric.Bearing.X) * scale;
                float top = (glyph.Offset.Y + metric.Bearing.Y) * scale;
                float width = metric.Size.X * scale;
                float height = metric.Size.Y * scale;
                Vector4 rect = metric.AtlasRect;
                placed[i] = new UITextLayoutGlyph(new(left, top, width, -height),
                    new(rect.X / input.AtlasSize.X, rect.Y / input.AtlasSize.Y,
                        rect.Z / input.AtlasSize.X, rect.W / input.AtlasSize.Y),
                    start, end - start, 0, glyph.GlyphId);
                maxRight = MathF.Max(maxRight, left + width);
                minY = MathF.Min(minY, top - height);
                maxY = MathF.Max(maxY, top);
                x += glyph.Advance.X * scale;
                i++;
            }
            while (i < run.Glyphs.Length && run.Glyphs[i].ClusterStart == start &&
                run.Glyphs[i].ClusterLength == end - start);
            AddCaret(rtl ? start : end, rtl ? UITextCaretAffinity.Downstream : UITextCaretAffinity.Upstream);
            if (i < run.Glyphs.Length)
                x += input.Spacing * scale;
        }
        if (carets.Count == 0)
            AddCaret(0, UITextCaretAffinity.Downstream);

        Vector2 intrinsic = run.Glyphs.Length == 0 ? Vector2.Zero :
            new Vector2(MathF.Max(x, maxRight), MathF.Max(0.0f, maxY - minY));
        if (input.FontSize is null && intrinsic.X > 0.0f && intrinsic.Y > 0.0f &&
            (availableSize.X < float.MaxValue * 0.5f || availableSize.Y < float.MaxValue * 0.5f))
        {
            float fit = MathF.Min(availableSize.X / intrinsic.X, availableSize.Y / intrinsic.Y);
            if (float.IsFinite(fit) && fit > 0.0f)
            {
                for (int i = 0; i < placed.Length; i++)
                    placed[i] = placed[i] with { Transform = placed[i].Transform * fit };
                for (int i = 0; i < carets.Count; i++)
                    carets[i] = carets[i] with { Position = carets[i].Position * fit };
                intrinsic *= fit;
            }
        }
        float regionWidth = availableSize.X < float.MaxValue * 0.5f ? availableSize.X : intrinsic.X;
        float regionHeight = availableSize.Y < float.MaxValue * 0.5f ? availableSize.Y : intrinsic.Y;
        float horizontalOffset = input.HorizontalAlignment switch
        {
            EHorizontalAlignment.Center => (regionWidth - intrinsic.X - input.Margins.Z) * 0.5f,
            EHorizontalAlignment.Right => regionWidth - intrinsic.X - input.Margins.Z,
            _ => 0.0f
        };
        float verticalOffset = input.VerticalAlignment switch
        {
            EVerticalAlignment.Top => regionHeight - intrinsic.Y - input.Margins.W,
            EVerticalAlignment.Center => (regionHeight - intrinsic.Y - input.Margins.W) * 0.5f,
            _ => 0.0f
        };
        Vector2 alignment = new(horizontalOffset, verticalOffset);
        for (int i = 0; i < placed.Length; i++)
        {
            Vector4 transform = placed[i].Transform;
            transform.X += alignment.X;
            transform.Y += alignment.Y;
            placed[i] = placed[i] with { Transform = transform };
        }
        float lineHeight = input.FontSize ?? intrinsic.Y;
        for (int i = 0; i < carets.Count; i++)
            carets[i] = carets[i] with
            {
                Position = carets[i].Position + alignment,
                LineHeight = lineHeight
            };

        HashSet<int> represented = new(carets.Count);
        foreach (UITextCaretStop stop in carets)
            represented.Add(stop.Utf16Offset);
        bool complete = represented.Contains(0) && represented.Contains(input.Text.Length);
        foreach (int boundary in StringInfo.ParseCombiningCharacters(input.Text))
            complete &= represented.Contains(boundary);
        return UITextLayoutResult.FromOwned(intrinsic, placed, [.. carets],
            input.TextRevision, input.FontRevision, complete);

        void AddCaret(int offset, UITextCaretAffinity affinity)
        {
            carets.Add(new UITextCaretStop(offset, 0, new Vector2(x, 0.0f),
                input.FontSize ?? 0.0f, carets.Count, affinity));
        }
    }
}
