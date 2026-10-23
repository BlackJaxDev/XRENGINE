using System.Numerics;
using NUnit.Framework;
using Shouldly;
using SkiaSharp;
using XREngine.Rendering.UI;
using XREngine.Runtime.UI.Skia;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextShapingBackendTests
{
    [Test]
    public void HarfBuzzKeepsGlyphIdsAndUtf16Clusters()
    {
        string path = SourceContractWorkspace.ResolveCanonicalFile(
            "Build/CommonAssets/Fonts/Roboto/Roboto-Regular.ttf");
        using SKTypeface typeface = SKTypeface.FromFile(path) ??
            throw new InvalidDataException("The test font could not be loaded.");
        using SKFont font = new(typeface, 128);
        ushort[] ids = font.GetGlyphs("AV");
        UIFontFaceSnapshot face = MakeFace(path, ids);

        UITextShapingResult result = new SkiaTextShapingBackend().Shape(
            new UITextShapingRequest("AV", face, "en-US", UITextDirection.LeftToRight));

        result.Glyphs.Length.ShouldBe(2);
        result.Glyphs.Span[0].GlyphId.ShouldBe(ids[0]);
        result.Glyphs.Span[1].GlyphId.ShouldBe(ids[1]);
        result.Glyphs.Span[0].ClusterStart.ShouldBe(0);
        result.Glyphs.Span[1].ClusterStart.ShouldBe(1);
        result.Glyphs.Span[0].Advance.X.ShouldBeGreaterThan(0.0f);
    }

    [Test]
    public void MissingShapedAtlasGlyphFailsExplicitly()
    {
        string path = SourceContractWorkspace.ResolveCanonicalFile(
            "Build/CommonAssets/Fonts/Roboto/Roboto-Regular.ttf");
        using SKTypeface typeface = SKTypeface.FromFile(path) ??
            throw new InvalidDataException("The test font could not be loaded.");
        using SKFont font = new(typeface, 128);
        ushort[] ids = font.GetGlyphs("AV");
        UIFontFaceSnapshot face = MakeFace(path, [ids[0]]);

        NotSupportedException error = Should.Throw<NotSupportedException>(() =>
            new SkiaTextShapingBackend().Shape(
                new UITextShapingRequest("AV", face, "en-US", UITextDirection.LeftToRight)));
        error.Message.ShouldContain("UI.ShapedGlyphAtlasMissing");
    }

    [Test]
    public void FaceOwnsItsSourceBytes()
    {
        byte[] bytes = [1, 2, 3];
        UIFontFaceSnapshot face = new(bytes, 0, 128,
            new Dictionary<uint, UIRuneMetrics>());
        bytes[0] = 9;
        byte[] copy = face.CopyFontBytes();
        copy[1] = 9;

        face.CopyFontBytes().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Test]
    public void RasterizerKeepsTheExactFaceBytesUsedForGlyphIds()
    {
        string source = SourceContractWorkspace.ResolveCanonicalFile(
            "Build/CommonAssets/Fonts/Roboto/Roboto-Regular.ttf");
        string directory = Path.Combine(Path.GetTempPath(), "ui-shaping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string copiedFont = Path.Combine(directory, "font.ttf");
            string atlas = Path.Combine(directory, "atlas.png");
            byte[] original = File.ReadAllBytes(source);
            File.WriteAllBytes(copiedFont, original);
            var result = new SkiaFontBitmapRasterizer().Rasterize(copiedFont,
                ["A", "V"], atlas, 128);
            File.WriteAllBytes(copiedFont, [1, 2, 3]);

            result.GlyphIds.ShouldNotBeNull();
            result.CopyFaceBytes().ShouldBe(original);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static UIFontFaceSnapshot MakeFace(string path, IReadOnlyList<ushort> ids)
    {
        Dictionary<uint, UIRuneMetrics> glyphs = new(ids.Count);
        foreach (ushort id in ids)
            glyphs[id] = new UIRuneMetrics(Vector2.Zero, new Vector2(5, 10), 6,
                new Vector4(0, 0, 5, 10));
        return new UIFontFaceSnapshot(File.ReadAllBytes(path), 0, 128, glyphs);
    }
}
