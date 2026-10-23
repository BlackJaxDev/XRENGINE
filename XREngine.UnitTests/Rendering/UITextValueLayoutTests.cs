using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UITextValueLayoutTests
{
    private UITestShaderScope _scope = null!;
    [SetUp] public void InstallHost() => _scope = new UITestShaderScope();
    [TearDown] public void RestoreHost() => _scope.Dispose();

    private static FontGlyphSet CreateFont()
        => new()
        {
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>
            {
                ["A"] = new(new Vector2(8), Vector2.Zero),
            },
            Atlas = new XRTexture2D(8, 8, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true),
            AtlasType = EFontAtlasType.Mtsdf,
            LayoutEmSize = 8,
        };

    private static void Destroy(UIText text, FontGlyphSet font)
    {
        text.OnPoolableDestroyed();
        font.Atlas?.Destroy(true);
        font.Destroy(true);
    }

    [Test]
    public void PaintAndPlacementReuseAcceptedLayoutAndGlyphRevisions()
    {
        FontGlyphSet font = CreateFont();
        UIText text = new() { Font = font, FontSize = 8, Text = "AA" };
        try
        {
            text.PushGlyphDataIfNeeded();
            UITextLayoutResult original = text.LayoutResult.ShouldNotBeNull();
            UITextLayoutInput input = text.CaptureLayoutInput().ShouldNotBeNull();
            XRDataBuffer transforms = text.Mesh!.Buffers["GlyphTransformsBuffer"];
            XRDataBuffer uvs = text.Mesh.Buffers["GlyphTexCoordsBuffer"];
            ulong transformRevision = transforms.Revision;
            ulong uvRevision = uvs.Revision;
            Vector4 originalTransform = original.Glyphs.Span[0].Transform;
            text.Color = new(1.0f, 0.0f, 0.0f, 1.0f);
            text.OutlineThickness = 0.5f;
            text.LocalTranslation = Vector3.One;
            text.PushGlyphDataIfNeeded();
            text.LayoutResult.ShouldBeSameAs(original);
            text.CaptureLayoutInput().ShouldBeSameAs(input);
            transforms.Revision.ShouldBe(transformRevision);
            uvs.Revision.ShouldBe(uvRevision);
            font.DistanceRange = 7;
            text.PushGlyphDataIfNeeded();
            text.LayoutResult.ShouldBeSameAs(original);
            text.Mesh.Material!.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(7);
            var glyphs = font.Glyphs.ShouldNotBeNull();
            glyphs["A"].Size = new Vector2(4, 8);
            font.Glyphs = glyphs;
            text.LayoutResult.ShouldBeSameAs(original);
            text.PushGlyphDataIfNeeded();
            text.LayoutResult.ShouldNotBeSameAs(original);
            original.Glyphs.Span[0].Transform.ShouldBe(originalTransform);
            transforms.Revision.ShouldBeGreaterThan(transformRevision);
        }
        finally { Destroy(text, font); }
    }

    [Test]
    public void AnimationUsesAcceptedOverridesAndOnlyCommitsChangedUsedSpans()
    {
        FontGlyphSet font = CreateFont();
        UIText text = new() { Font = font, FontSize = 8, Text = "AA", AnimatableTransforms = true };
        try
        {
            text.PushGlyphDataIfNeeded();
            XRDataBuffer transforms = text.Mesh!.Buffers["GlyphTransformsBuffer"];
            XRDataBuffer uvs = text.Mesh.Buffers["GlyphTexCoordsBuffer"];
            XRDataBuffer rotations = text.Mesh.Buffers["GlyphRotationsBuffer"];
            Vector4 first = transforms.Get<Vector4>(0) ?? throw new InvalidOperationException("Missing glyph transform.");
            Vector4 second = transforms.Get<Vector4>(16) ?? throw new InvalidOperationException("Missing glyph transform.");
            ulong uvRevision = uvs.Revision;
            text.GlyphRelativeTransforms[1] = (new Vector2(2, 3), Vector2.One, 0.25f);
            text.MarkGlyphTransformsChanged();
            text.GlyphRelativeTransforms[1] = (new Vector2(99), Vector2.One, 1.0f);
            text.PushGlyphDataIfNeeded();
            transforms.Get<Vector4>(0).ShouldBe(first);
            transforms.Get<Vector4>(16).ShouldBe(second + new Vector4(2, 3, 0, 0));
            rotations.Get<float>(4).ShouldBe(0.25f);
            uvs.Revision.ShouldBe(uvRevision);
            var transformRange = transforms.GetDirtyRangesSnapshot().ShouldHaveSingleItem();
            transformRange.OffsetBytes.ShouldBe(16u);
            transformRange.LengthBytes.ShouldBe(16u);
            var rotationRange = rotations.GetDirtyRangesSnapshot().ShouldHaveSingleItem();
            rotationRange.OffsetBytes.ShouldBe(4u);
            rotationRange.LengthBytes.ShouldBe(4u);
            ulong revision = transforms.Revision;
            for (int index = 0; index < 10; index++)
                text.PushGlyphDataIfNeeded();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++)
                text.PushGlyphDataIfNeeded();
            (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
            transforms.Revision.ShouldBe(revision);
            text.AnimatableTransforms = false;
            text.PushGlyphDataIfNeeded();
            text.Mesh!.Buffers.ContainsKey("GlyphRotationsBuffer").ShouldBeFalse();
        }
        finally { Destroy(text, font); }
    }

    [Test]
    public void FontNotificationsWaitForRenderOwnerAndCannotRestoreAnOlderTextCapture()
    {
        FontGlyphSet font = CreateFont();
        XRTexture2D originalAtlas = font.Atlas!;
        XRTexture2D replacementAtlas = new(16, 16, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true);
        UIText text = new() { Font = font, FontSize = 8, Text = "A" };
        using ManualResetEventSlim ownerReady = new();
        using ManualResetEventSlim consume = new();
        Task<UITextLayoutResult>? owner = null;
        try
        {
            text.PushGlyphDataIfNeeded();
            UITextLayoutResult original = text.LayoutResult.ShouldNotBeNull();
            XRMaterial material = text.Mesh!.Material!;
            owner = Task.Run(() =>
            {
                ownerReady.Set();
                consume.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                text.Text = "AA";
                text.PushGlyphDataIfNeeded();
                return text.LayoutResult.ShouldNotBeNull();
            });
            ownerReady.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            font.Atlas = replacementAtlas;
            font.DistanceRange = 6;
            // The notifying thread cannot install a new atlas beside the accepted old UVs.
            text.LayoutResult.ShouldBeSameAs(original);
            material.Textures[0].ShouldBeSameAs(originalAtlas);
            consume.Set();
            UITextLayoutResult accepted = owner.GetAwaiter().GetResult();
            accepted.Glyphs.Length.ShouldBe(2);
            text.CaptureLayoutInput().ShouldNotBeNull().Text.ShouldBe("AA");
            material.Textures[0].ShouldBeSameAs(replacementAtlas);
            accepted.Glyphs.Span[0].Uvs.Z.ShouldBe(0.5f);
            font.DistanceRange = 7;
            // An equal Text assignment cannot hide or undo a pending font refresh.
            text.Text = "AA";
            Task.Run(text.PushGlyphDataIfNeeded).GetAwaiter().GetResult();
            text.LayoutResult.ShouldBeSameAs(accepted);
            material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(7);
        }
        finally
        {
            consume.Set();
            owner?.GetAwaiter().GetResult();
            Destroy(text, font);
            originalAtlas.Destroy(true);
        }
    }

    [Test]
    public void PoolReleaseDetachesFontChangesAndResetCapturesCurrentValues()
    {
        FontGlyphSet font = CreateFont();
        UIText text = new() { Font = font, FontSize = 8, Text = "A" };
        try
        {
            text.PushGlyphDataIfNeeded();
            UITextLayoutResult original = text.LayoutResult.ShouldNotBeNull();
            XRMaterial material = text.Mesh!.Material!;
            float originalDistance = material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value;
            text.OnPoolableReleased();
            font.DistanceRange = originalDistance + 3;
            text.LayoutResult.ShouldBeNull();
            material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(originalDistance);
            text.OnPoolableReset();
            text.LayoutResult.ShouldNotBeNull();
            text.LayoutResult.ShouldNotBeSameAs(original);
            material.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(originalDistance + 3);
            text.PushGlyphDataIfNeeded();
            XRDataBuffer buffer = text.Mesh!.Buffers["GlyphTransformsBuffer"];
            buffer.ClientSideSource = new XREngine.Data.DataSource(4u);
            text._dataChanged = true;
            Should.Throw<InvalidOperationException>(text.PushGlyphDataIfNeeded).Message.ShouldContain("GlyphStoragePending");
            Should.Throw<InvalidOperationException>(text.PushGlyphDataIfNeeded);
        }
        finally { Destroy(text, font); }
    }
}
