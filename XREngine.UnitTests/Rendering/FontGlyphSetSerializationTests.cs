using ImageMagick;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
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
    public void EmbeddedAtlas_WithExistingAuxiliaryImage_UsesTextureEnvelope()
    {
        FontGlyphSet font = CreateFontWithEmbeddedAtlas();
        string atlasPath = font.Atlas.ShouldNotBeNull().FilePath.ShouldNotBeNull();
        Directory.CreateDirectory(Path.GetDirectoryName(atlasPath)!);
        File.WriteAllBytes(atlasPath, []);
        XRAssetGraphUtility.RefreshAssetGraph(font);

        string yaml = AssetManager.Serializer.Serialize(font);

        yaml.ShouldContain("Atlas:");
        yaml.ShouldContain("Format: CookedBinary");
        yaml.ShouldNotContain("Atlas:\r\n  ID:");
        yaml.ShouldNotContain("Atlas:\n  ID:");
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

    [Test]
    public void GetQuads_WhenStringContainsSurrogatePairGlyph_ProducesOneQuad()
    {
        const string eyeGlyph = "\U0001F441";
        FontGlyphSet font = CreateFontWithEmbeddedAtlas(eyeGlyph);

        List<(Vector4 transform, Vector4 uvs)> quads = [];

        font.GetQuads(eyeGlyph, quads, 8.0f, float.MaxValue, float.MaxValue);

        quads.Count.ShouldBe(1);
    }

    [Test]
    public void NormalizeBitmapAtlasTextureData_UsesAlphaAsR8Coverage()
    {
        Mipmap2D mipmap = new(2, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, allocateData: false)
        {
            Data = new DataSource(
            [
                255, 255, 255, 0,
                0, 0, 0, 128,
            ]),
        };
        XRTexture2D atlas = new()
        {
            SizedInternalFormat = ESizedInternalFormat.R8,
            Mipmaps = [mipmap],
        };

        try
        {
            FontGlyphSet.NormalizeBitmapAtlasTextureData(atlas).ShouldBeTrue();

            Mipmap2D normalized = atlas.Mipmaps[0];
            normalized.InternalFormat.ShouldBe(EPixelInternalFormat.R8);
            normalized.PixelFormat.ShouldBe(EPixelFormat.Red);
            normalized.PixelType.ShouldBe(EPixelType.UnsignedByte);
            normalized.Data.ShouldNotBeNull().GetBytes().ShouldBe([0, 128]);
        }
        finally
        {
            atlas.Mipmaps[0].Data?.Dispose();
        }
    }

    [Test]
    public void RebuildBitmapAtlasMipChain_BuildsFilteredR8CoverageMips()
    {
        Mipmap2D mipmap = new(4, 4, EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte, allocateData: false)
        {
            Data = new DataSource(
            [
                0, 0, 100, 100,
                0, 0, 100, 100,
                200, 200, 255, 255,
                200, 200, 255, 255,
            ])
            {
                PreferCompressedYaml = true,
            },
        };
        XRTexture2D atlas = new()
        {
            SizedInternalFormat = ESizedInternalFormat.R8,
            Mipmaps = [mipmap],
        };

        try
        {
            FontGlyphSet.RebuildBitmapAtlasMipChain(atlas, force: true).ShouldBeTrue();

            atlas.Mipmaps.Length.ShouldBe(3);
            atlas.Mipmaps[0].Width.ShouldBe(4u);
            atlas.Mipmaps[0].Height.ShouldBe(4u);
            atlas.Mipmaps[0].Data.ShouldNotBeNull().GetBytes().ShouldBe(
            [
                0, 0, 100, 100,
                0, 0, 100, 100,
                200, 200, 255, 255,
                200, 200, 255, 255,
            ]);

            atlas.Mipmaps[1].Width.ShouldBe(2u);
            atlas.Mipmaps[1].Height.ShouldBe(2u);
            atlas.Mipmaps[1].InternalFormat.ShouldBe(EPixelInternalFormat.R8);
            atlas.Mipmaps[1].PixelFormat.ShouldBe(EPixelFormat.Red);
            atlas.Mipmaps[1].PixelType.ShouldBe(EPixelType.UnsignedByte);
            atlas.Mipmaps[1].Data.ShouldNotBeNull().GetBytes().ShouldBe([0, 100, 200, 255]);
            atlas.Mipmaps[1].Data.ShouldNotBeNull().PreferCompressedYaml.ShouldBeTrue();

            atlas.Mipmaps[2].Width.ShouldBe(1u);
            atlas.Mipmaps[2].Height.ShouldBe(1u);
            atlas.Mipmaps[2].Data.ShouldNotBeNull().GetBytes().ShouldBe([139]);
        }
        finally
        {
            DisposeMipmapData(atlas);
        }
    }

    [Test]
    public void RebuildBitmapAtlasMipChain_DownsamplesOddEdgesIntoFinalCoverage()
    {
        Mipmap2D mipmap = new(3, 3, EPixelInternalFormat.R8, EPixelFormat.Red, EPixelType.UnsignedByte, allocateData: false)
        {
            Data = new DataSource(
            [
                0, 0, 255,
                0, 0, 255,
                255, 255, 255,
            ]),
        };
        XRTexture2D atlas = new()
        {
            SizedInternalFormat = ESizedInternalFormat.R8,
            Mipmaps = [mipmap],
        };

        try
        {
            FontGlyphSet.RebuildBitmapAtlasMipChain(atlas, force: true).ShouldBeTrue();

            atlas.Mipmaps.Length.ShouldBe(2);
            atlas.Mipmaps[1].Width.ShouldBe(1u);
            atlas.Mipmaps[1].Height.ShouldBe(1u);
            atlas.Mipmaps[1].Data.ShouldNotBeNull().GetBytes().ShouldBe([142]);
        }
        finally
        {
            DisposeMipmapData(atlas);
        }
    }

    private static FontGlyphSet CreateFontWithEmbeddedAtlas(string glyph = "A")
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "FontGlyphSetSerializationTests", Guid.NewGuid().ToString("N"));
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
            Characters = [glyph],
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                [glyph] = new(new Vector2(8.0f, 8.0f), Vector2.Zero)
            },
            Atlas = atlas,
            AtlasType = EFontAtlasType.Bitmap,
            LayoutEmSize = 8.0f,
        };

        XRAssetGraphUtility.RefreshAssetGraph(font);
        return font;
    }

    private static void DisposeMipmapData(XRTexture2D atlas)
    {
        foreach (Mipmap2D mipmap in atlas.Mipmaps)
            mipmap.Data?.Dispose();
    }
}
