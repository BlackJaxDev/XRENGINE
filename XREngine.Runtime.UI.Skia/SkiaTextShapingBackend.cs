using System.Numerics;
using HarfBuzzSharp;
using XREngine.Rendering.UI;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace XREngine.Runtime.UI.Skia;

/// <summary>Shape one analyzed run with its owned OpenType face.</summary>
public sealed class SkiaTextShapingBackend : IUITextHardLineShapingBackend
{
    public long MaxScratchBytes(UITextShapingRequest request)
        => checked(4L * request.Face.ByteLength + 256L * request.Text.Length);

    public UITextShapingResult Shape(UITextShapingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text.Length > 65536)
            throw new NotSupportedException("UI.ShapedTextTooLong: one run exceeds 65536 UTF-16 units.");
        if (request.Text.Length == 0)
            return new UITextShapingResult(request, []);

        byte[] bytes = request.Face.CopyFontBytes();
        using MemoryStream stream = new(bytes, writable: false);
        using Blob blob = Blob.FromStream(stream);
        using Face face = new(blob, request.Face.FaceIndex);
        if (face.UnitsPerEm <= 0)
            throw new InvalidDataException("UI.ShapingFaceInvalid: no units-per-em value.");
        using HbFont font = new(face);
        font.SetFunctionsOpenType();
        font.SetScale(face.UnitsPerEm, face.UnitsPerEm);
        Feature[] features = new Feature[request.Features.Length];
        for (int i = 0; i < features.Length; i++)
            features[i] = Feature.Parse(request.Features[i]);
        float scale = request.Face.EmSize / face.UnitsPerEm;
        UITextSourceLine[] lines = UITextHardLineBreaks.Parse(request.Text);
        if (lines.Length > 1 && request.ParagraphAnalysis is null)
            throw new NotSupportedException("UI.ShapedLineAnalysisUnavailable: hard lines require paragraph analysis.");
        List<UITextShapedGlyph> glyphs = new(request.Text.Length);
        foreach (UITextSourceLine line in lines)
        {
            if (line.Length == 0)
                continue;
            if (lines.Length > 1)
            {
                ushort? script = null;
                foreach (UITextParagraphRun range in request.ParagraphAnalysis!.Runs.Span)
                {
                    if (range.Start >= line.End || range.Start + range.Length <= line.Start)
                        continue;
                    if (script is not null && script.Value != range.Script)
                        throw new NotSupportedException(
                            "UI.ShapedScriptRunsUnavailable: one hard line requires multiple script runs.");
                    script = range.Script;
                }
            }
            string lineText = request.Text.Substring(line.Start, line.Length);
            using HbBuffer buffer = new();
            buffer.AddUtf16(lineText);
            buffer.GuessSegmentProperties();
            buffer.Direction = request.Direction == UITextDirection.RightToLeft
                ? Direction.RightToLeft : Direction.LeftToRight;
            buffer.Language = new Language(request.Language);
            font.Shape(buffer, features);

            GlyphInfo[] infos = buffer.GlyphInfos;
            GlyphPosition[] positions = buffer.GlyphPositions;
            if (infos.Length != positions.Length)
                throw new InvalidDataException("UI.ShapingResultInvalid: glyph arrays differ in length.");
            if (glyphs.Count + infos.Length > (long)request.Text.Length * 4)
                throw new NotSupportedException("UI.ShapedGlyphCountExceeded: one run exceeds its glyph budget.");
            int[] starts = new int[infos.Length];
            for (int i = 0; i < infos.Length; i++)
            {
                if (infos[i].Cluster >= lineText.Length)
                    throw new InvalidDataException("UI.ShapingResultInvalid: cluster exceeds source line.");
                starts[i] = checked((int)infos[i].Cluster);
            }
            Array.Sort(starts);
            for (int i = 0; i < infos.Length; i++)
            {
                uint id = infos[i].Codepoint;
                if (id == 0 || !request.Face.Glyphs.TryGetValue(id, out UIRuneMetrics metric))
                    throw new NotSupportedException($"UI.ShapedGlyphAtlasMissing: glyph ID {id} has no atlas entry.");
                int localStart = checked((int)infos[i].Cluster);
                int next = Array.BinarySearch(starts, localStart);
                while (next < starts.Length && starts[next] <= localStart)
                    next++;
                int localEnd = next < starts.Length ? starts[next] : line.Length;
                glyphs.Add(new UITextShapedGlyph(id, metric,
                    new Vector2(positions[i].XAdvance, positions[i].YAdvance) * scale,
                    new Vector2(positions[i].XOffset, positions[i].YOffset) * scale,
                    checked(line.Start + localStart), localEnd - localStart));
            }
        }
        return new UITextShapingResult(request, [.. glyphs], hasMeasuredHardLines: lines.Length > 1);
    }
}
