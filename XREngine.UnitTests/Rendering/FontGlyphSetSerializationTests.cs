using ImageMagick;
using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class FontGlyphSetSerializationTests
{
    [Test]
    public void EmbeddedAtlas_UsesTextureEnvelope_WhenSerializedToYaml()
    {
        FontGlyphSet font = CreateFontWithEmbeddedAtlas();

        string yaml = AssetManager.Serializer.Serialize(font);

        yaml.ShouldContain("Atlas:");
        yaml.ShouldContain("Format: CookedBinary");
    }

    [Test]
    public void EmbeddedAtlas_RoundTripsAtlasPayloadAndOriginalPath()
    {
        FontGlyphSet font = CreateFontWithEmbeddedAtlas();
        XRTexture2D originalAtlas = font.Atlas.ShouldNotBeNull();
        byte[] originalBytes = originalAtlas.Mipmaps[0].Data.ShouldNotBeNull().GetBytes();

        string yaml = AssetManager.Serializer.Serialize(font);
        FontGlyphSet roundTripped = AssetManager.Deserializer.Deserialize<FontGlyphSet>(yaml);

        string fontPath = font.FilePath.ShouldNotBeNull();
        string atlasOriginalPath = originalAtlas.OriginalPath.ShouldNotBeNull();

        roundTripped.FilePath = fontPath;
        roundTripped.OriginalPath = fontPath;
        XRAssetGraphUtility.RefreshAssetGraph(roundTripped);

        XRTexture2D atlas = roundTripped.Atlas.ShouldNotBeNull();
        atlas.OriginalPath.ShouldBe(atlasOriginalPath);
        atlas.FilePath.ShouldBe(fontPath);
        atlas.Mipmaps.Length.ShouldBe(1);
        atlas.Mipmaps[0].HasData().ShouldBeTrue();
        atlas.Mipmaps[0].Data.ShouldNotBeNull().GetBytes().ShouldBe(originalBytes);
    }

    [Test]
    public void GetQuads_WhenAtlasIsMissing_DoesNotThrowAndReturnsNoGlyphs()
    {
        FontGlyphSet font = new()
        {
            Characters = ["A"],
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                ["A"] = new(new Vector2(8.0f, 8.0f), Vector2.Zero)
            },
            LayoutEmSize = 8.0f,
        };

        List<(Vector4 transform, Vector4 uvs)> quads = [];

        Should.NotThrow(() => font.GetQuads("A", quads, 8.0f, float.MaxValue, float.MaxValue));
        quads.ShouldBeEmpty();
    }

    private static FontGlyphSet CreateFontWithEmbeddedAtlas()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "FontGlyphSetSerializationTests");
        string fontPath = Path.Combine(tempRoot, "EmbeddedFont.ttf");
        string atlasPath = Path.Combine(tempRoot, "EmbeddedFont.png");

        using MagickImage image = new(MagickColors.White, 8, 8);
        XRTexture2D atlas = new(image)
        {
            Name = "EmbeddedAtlas",
            FilePath = atlasPath,
            OriginalPath = atlasPath,
            AutoGenerateMipmaps = true,
            Resizable = false,
        };

        var atlasData = atlas.Mipmaps[0].Data;
        if (atlasData is not null)
            atlasData.PreferCompressedYaml = true;

        FontGlyphSet font = new()
        {
            Name = "EmbeddedFont",
            FilePath = fontPath,
            OriginalPath = fontPath,
            Characters = ["A"],
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                ["A"] = new(new Vector2(8.0f, 8.0f), Vector2.Zero)
            },
            Atlas = atlas,
            AtlasType = EFontAtlasType.Bitmap,
            LayoutEmSize = 8.0f,
        };

        XRAssetGraphUtility.RefreshAssetGraph(font);
        return font;
    }
}