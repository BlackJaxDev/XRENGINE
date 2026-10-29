using System.Globalization;
using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>Place independently shaped hard lines from one captured text request.</summary>
internal static class UITextShapedHardLineLayoutKernel
{
    internal static UITextLayoutResult Measure(UITextLayoutInput input, Vector2 availableSize)
    {
        UITextShapedRunInput run = input.ShapedRun ??
            throw new ArgumentException("A shaped run is required.", nameof(input));
        UITextParagraphAnalysis paragraph = run.ParagraphAnalysis ??
            throw new NotSupportedException("UI.ShapedLineAnalysisUnavailable: hard lines require paragraph analysis.");
        if (!run.HasMeasuredHardLines || paragraph.Breakpoints.Length != input.Text.Length)
            throw new NotSupportedException("UI.ShapedLineAnalysisUnavailable: hard lines were not measured independently.");
        if (input.EmSize <= 0.0f || input.AtlasSize.X <= 0.0f || input.AtlasSize.Y <= 0.0f)
            throw new InvalidOperationException("UI.ShapedFontMetricsInvalid: the atlas or em size is empty.");
        int expectedParity = run.Direction == UITextDirection.RightToLeft ? 1 : 0;
        foreach (UITextParagraphRun range in paragraph.Runs.Span)
            if ((range.ResolvedBidiLevel & 1) != expectedParity)
                throw new NotSupportedException(
                    "UI.BidiAnalysisUnavailable: resolved direction runs require paragraph placement.");

        UITextSourceLine[] lines = UITextHardLineBreaks.Parse(input.Text);
        List<UITextLayoutGlyph> glyphs = new(run.Glyphs.Length);
        List<UITextCaretStop> carets = new(run.Glyphs.Length * 2 + lines.Length * 2);
        float[] widths = new float[lines.Length];
        float[] heights = new float[lines.Length];
        bool complete = true;
        int glyphCursor = 0;
        bool rtl = run.Direction == UITextDirection.RightToLeft;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            UITextSourceLine line = lines[lineIndex];
            int firstGlyph = glyphCursor;
            int boundary = rtl ? line.End : line.Start;
            while (glyphCursor < run.Glyphs.Length && run.Glyphs[glyphCursor].ClusterStart < line.End)
            {
                UITextShapedGlyph first = run.Glyphs[glyphCursor];
                int start = first.ClusterStart;
                int end = checked(start + first.ClusterLength);
                if (start < line.Start || end > line.End || end <= start ||
                    (rtl ? end : start) != boundary)
                    throw new NotSupportedException(
                        "UI.ShapedClusterOrderUnsupported: a hard-line cluster crosses or reorders a line.");
                boundary = rtl ? start : end;
                do { glyphCursor++; }
                while (glyphCursor < run.Glyphs.Length &&
                    run.Glyphs[glyphCursor].ClusterStart == start &&
                    run.Glyphs[glyphCursor].ClusterLength == end - start);
            }
            if (glyphCursor > firstGlyph && boundary != (rtl ? line.Start : line.End))
                throw new NotSupportedException(
                    "UI.ShapedClusterOrderUnsupported: hard-line clusters do not cover their source line.");

            UITextShapedRunInput localRun = run.SliceLine(line, firstGlyph, glyphCursor - firstGlyph);
            UITextLayoutInput localInput = input with
            {
                Text = localRun.SourceText,
                ShapedRun = localRun,
                ShapingRequest = null,
                HorizontalAlignment = EHorizontalAlignment.Left,
                VerticalAlignment = EVerticalAlignment.Bottom,
                Margins = Vector4.Zero
            };
            UITextLayoutResult local = UITextShapedLayoutKernel.Measure(localInput,
                new Vector2(float.MaxValue, float.MaxValue));
            complete &= local.HasCompleteGraphemeCaretStops;
            widths[lineIndex] = local.IntrinsicSize.X;
            heights[lineIndex] = MathF.Max(input.FontSize ?? 1.0f, local.IntrinsicSize.Y);
            foreach (UITextLayoutGlyph glyph in local.Glyphs.Span)
                glyphs.Add(glyph with
                {
                    ClusterStart = checked(line.Start + glyph.ClusterStart),
                    Line = lineIndex
                });
            int firstCaret = carets.Count;
            foreach (UITextCaretStop caret in local.Carets.Span)
                carets.Add(caret with
                {
                    Utf16Offset = checked(line.Start + caret.Utf16Offset),
                    Line = lineIndex,
                    VisualIndex = carets.Count
                });
            bool hasLineEnd = false;
            for (int i = firstCaret; i < carets.Count; i++)
                hasLineEnd |= carets[i].Utf16Offset == line.End;
            if (line.BreakLength > 0 && !hasLineEnd)
                carets.Add(new UITextCaretStop(line.End, lineIndex,
                    new Vector2(rtl ? 0.0f : widths[lineIndex], 0.0f), heights[lineIndex],
                    carets.Count));
        }
        if (glyphCursor != run.Glyphs.Length)
            throw new InvalidOperationException("UI.ShapedParagraphMismatch: a glyph lies outside the hard lines.");

        float[] shifts = new float[lines.Length];
        float height = heights[^1];
        for (int i = lines.Length - 2; i >= 0; i--)
        {
            shifts[i] = shifts[i + 1] + heights[i + 1] + input.LineSpacing;
            height += heights[i] + input.LineSpacing;
        }
        float width = 0.0f;
        foreach (float lineWidth in widths)
            width = MathF.Max(width, lineWidth);
        Vector2 intrinsic = new(width, height);
        for (int i = 0; i < glyphs.Count; i++)
        {
            UITextLayoutGlyph glyph = glyphs[i];
            Vector4 transform = glyph.Transform;
            transform.Y += shifts[glyph.Line];
            glyphs[i] = glyph with { Transform = transform };
        }
        for (int i = 0; i < carets.Count; i++)
        {
            UITextCaretStop caret = carets[i];
            carets[i] = caret with
            {
                Position = caret.Position + new Vector2(0.0f, shifts[caret.Line]),
                LineHeight = heights[caret.Line]
            };
        }
        if (input.FontSize is null && intrinsic.X > 0.0f && intrinsic.Y > 0.0f &&
            (availableSize.X < float.MaxValue * 0.5f || availableSize.Y < float.MaxValue * 0.5f))
        {
            float fit = MathF.Min(availableSize.X / intrinsic.X, availableSize.Y / intrinsic.Y);
            if (float.IsFinite(fit) && fit > 0.0f)
            {
                intrinsic *= fit;
                for (int i = 0; i < widths.Length; i++)
                {
                    widths[i] *= fit;
                    heights[i] *= fit;
                    shifts[i] *= fit;
                }
                for (int i = 0; i < glyphs.Count; i++)
                    glyphs[i] = glyphs[i] with { Transform = glyphs[i].Transform * fit };
                for (int i = 0; i < carets.Count; i++)
                    carets[i] = carets[i] with
                    {
                        Position = carets[i].Position * fit,
                        LineHeight = carets[i].LineHeight * fit
                    };
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
            Vector4 transform = glyph.Transform;
            transform.X += HorizontalOffset(glyph.Line);
            transform.Y += verticalOffset;
            glyphs[i] = glyph with { Transform = transform };
        }
        for (int i = 0; i < carets.Count; i++)
        {
            UITextCaretStop caret = carets[i];
            carets[i] = caret with
            {
                Position = caret.Position + new Vector2(HorizontalOffset(caret.Line), verticalOffset)
            };
        }

        HashSet<int> represented = new(carets.Count);
        foreach (UITextCaretStop stop in carets) represented.Add(stop.Utf16Offset);
        complete &= represented.Contains(0) && represented.Contains(input.Text.Length);
        foreach (int boundary in StringInfo.ParseCombiningCharacters(input.Text))
            complete &= represented.Contains(boundary);
        return UITextLayoutResult.FromOwned(intrinsic, [.. glyphs], [.. carets],
            input.TextRevision, input.FontRevision, complete, usesShapedGlyphs: true);

        float HorizontalOffset(int line) => input.HorizontalAlignment switch
        {
            EHorizontalAlignment.Center => (regionWidth - widths[line] - input.Margins.Z) * 0.5f,
            EHorizontalAlignment.Right => regionWidth - widths[line] - input.Margins.Z,
            _ => 0.0f
        };
    }
}
