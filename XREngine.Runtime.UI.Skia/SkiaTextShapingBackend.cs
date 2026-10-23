using System.Numerics;
using HarfBuzzSharp;
using XREngine.Rendering.UI;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace XREngine.Runtime.UI.Skia;

/// <summary>Shape one analyzed run with its owned OpenType face.</summary>
public sealed class SkiaTextShapingBackend : IUITextShapingBackend
{
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
        using HbBuffer buffer = new();
        buffer.AddUtf16(request.Text);
        buffer.GuessSegmentProperties();
        buffer.Direction = request.Direction == UITextDirection.RightToLeft
            ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Language = new Language(request.Language);
        Feature[] features = new Feature[request.Features.Length];
        for (int i = 0; i < features.Length; i++)
            features[i] = Feature.Parse(request.Features[i]);
        font.Shape(buffer, features);

        GlyphInfo[] infos = buffer.GlyphInfos;
        GlyphPosition[] positions = buffer.GlyphPositions;
        if (infos.Length != positions.Length)
            throw new InvalidDataException("UI.ShapingResultInvalid: glyph arrays differ in length.");
        if (infos.Length > request.Text.Length * 4)
            throw new NotSupportedException("UI.ShapedGlyphCountExceeded: one run exceeds its glyph budget.");
        int[] starts = new int[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            if (infos[i].Cluster >= request.Text.Length)
                throw new InvalidDataException("UI.ShapingResultInvalid: cluster exceeds source text.");
            starts[i] = checked((int)infos[i].Cluster);
        }
        Array.Sort(starts);
        float scale = request.Face.EmSize / face.UnitsPerEm;
        UITextShapedGlyph[] glyphs = new UITextShapedGlyph[infos.Length];
        for (int i = 0; i < glyphs.Length; i++)
        {
            uint id = infos[i].Codepoint;
            if (id == 0 || !request.Face.Glyphs.TryGetValue(id, out UIRuneMetrics metric))
                throw new NotSupportedException($"UI.ShapedGlyphAtlasMissing: glyph ID {id} has no atlas entry.");
            int start = checked((int)infos[i].Cluster);
            int next = Array.BinarySearch(starts, start);
            while (next < starts.Length && starts[next] <= start)
                next++;
            int end = next < starts.Length ? starts[next] : request.Text.Length;
            glyphs[i] = new UITextShapedGlyph(id, metric,
                new Vector2(positions[i].XAdvance, positions[i].YAdvance) * scale,
                new Vector2(positions[i].XOffset, positions[i].YOffset) * scale,
                start, end - start);
        }
        return new UITextShapingResult(request, glyphs);
    }
}
