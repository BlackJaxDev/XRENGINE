using NUnit.Framework;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class UIStaticPrimitiveContractTests
{
    private XRMesh _mesh = null!;
    private XRMeshRenderer _renderer = null!;

    [SetUp]
    public void SetUp()
    {
        _mesh = new XRMesh();
        _renderer = new XRMeshRenderer();
    }

    [TearDown]
    public void TearDown()
    {
        _renderer.Destroy(now: true);
        _mesh.Destroy(now: true);
    }

    [Test]
    public void BorderContract_RequiresExactCommittedBuffersAndVariant()
    {
        _renderer.Mesh = _mesh;
        XRDataBuffer<Vector4> color = BorderBuffer("QuadUvBuffer", 3);
        XRDataBuffer<Vector4> width = BorderBuffer("QuadBorderWidthBuffer", 4);
        _renderer.Buffers["QuadUvBuffer"] = color;
        _renderer.Buffers["QuadBorderWidthBuffer"] = width;
        UIIndexedDrawIntent intent = new(_renderer, _mesh.GeometryRevision,
            EPrimitiveType.Triangles, IndexSize.TwoBytes, 6, 1, 0);
        UIStaticDrawContract border = new(1,
            new UICompositionOwner(new UIElementIdentity(2, 1),
                EUICompositionLayerKind.StaticChrome),
            3, 4, 5, 6, intent)
        {
            CapturedMesh = _mesh,
            IsBorder = true,
            BorderColorBuffer = color,
            BorderColorRevision = color.Revision,
            BorderWidthBuffer = width,
            BorderWidthRevision = width.Revision,
        };
        Assert.That(border.IsDefined && border.MatchesIntent(_renderer, 1), Is.True);
        Assert.That((border with { IsBorder = false }).IsDefined, Is.False);
        UIStaticDrawContract solid = border with
        {
            IsBorder = false,
            BorderColorBuffer = null,
            BorderColorRevision = 0,
            BorderWidthBuffer = null,
            BorderWidthRevision = 0,
        };
        Assert.That(solid.IsDefined, Is.True);
        Assert.That(solid == border, Is.False);
        Commit(width, new Vector4(2));
        Assert.That(border.MatchesIntent(_renderer, 1), Is.False);
        border = border with { BorderWidthRevision = width.Revision };
        Assert.That(border.MatchesIntent(_renderer, 1), Is.True);
        Commit(color, new Vector4(3));
        Assert.That(border.MatchesIntent(_renderer, 1), Is.False);
        border = border with { BorderColorRevision = color.Revision };
        Assert.That(border.MatchesIntent(_renderer, 1), Is.True);
        width.BindingIndexOverride = 3;
        Assert.That(border.MatchesIntent(_renderer, 1), Is.False);
        width.BindingIndexOverride = 4;
        Assert.That(border.MatchesIntent(_renderer, 1), Is.True);
        XRDataBuffer<Vector4> replacement = BorderBuffer("QuadUvBuffer", 3);
        _renderer.Buffers["QuadUvBuffer"] = replacement;
        Assert.That(border.MatchesIntent(_renderer, 1), Is.False);
        color.Destroy(true);
    }

    [Test]
    public void BindfulImageContract_UsesPrivateSnapshotAndExactUvRevision()
    {
        UIImageAtlas atlas = new([new UIImageAtlasSource("a", 1, 1,
            new byte[] { 1, 2, 3, 255 })], 4);
        XRTexture2D source = atlas.CreateTexture();
        Assert.That(UITextureSnapshot.TryAcquire(source, out UITextureSnapshot? snapshot,
            out string? failure), Is.True, failure);
        XRMaterial material = new(Array.Empty<ShaderVar>(), [snapshot!.Texture],
            Array.Empty<XRShader>());
        XRDataBuffer<Vector4> uv = BorderBuffer("QuadUvBuffer", 3);
        try
        {
            _renderer.Mesh = _mesh;
            _renderer.Material = material;
            _renderer.Buffers["QuadUvBuffer"] = uv;
            UIIndexedDrawIntent intent = new(_renderer, _mesh.GeometryRevision,
                EPrimitiveType.Triangles, IndexSize.TwoBytes, 6, 1, 0);
            UIStaticDrawContract image = new(1,
                new UICompositionOwner(new UIElementIdentity(2, 1),
                    EUICompositionLayerKind.StaticChrome),
                3, 4, 5, 6, intent)
            {
                CapturedMesh = _mesh,
                IsImage = true,
                ImageSnapshot = snapshot,
                ImageTexture = snapshot.Texture,
                ImageSrgb = true,
                ImageUvBuffer = uv,
                ImageUvRevision = uv.Revision,
            };
            Assert.That(image.IsDefined && image.MatchesIntent(_renderer, 1), Is.True);
            source.Destroy(true);
            Assert.That(image.MatchesIntent(_renderer, 1), Is.True,
                "A retained private image must outlive changes to its authoring source.");
            Assert.That((image with { ImageSrgb = false }) == image, Is.False);
            Commit(uv, new Vector4(2));
            Assert.That(image.MatchesIntent(_renderer, 1), Is.False);
            image = image with { ImageUvRevision = uv.Revision };
            Assert.That(image.MatchesIntent(_renderer, 1), Is.True);
            Assert.That((image with { ImageSnapshot = null }).IsDefined, Is.False);
        }
        finally
        {
            snapshot!.Release();
            material.Destroy(true);
            source.Destroy(true);
        }
    }

    [Test]
    public void StaticTextContract_RequiresPrivateAtlasAndEveryCommittedGlyphBuffer()
    {
        UIImageAtlas atlas = new([new UIImageAtlasSource("glyph", 1, 1,
            new byte[] { 255, 255, 255, 255 })], 4);
        XRTexture2D source = atlas.CreateTexture();
        Assert.That(UITextureSnapshot.TryAcquire(source,
            out UITextureSnapshot? snapshot, out string? failure), Is.True, failure);
        XRMaterial material = new(new ShaderVar[]
            {
                new ShaderInt(0, "TextAtlasType"),
                new ShaderFloat(0, "MsdfDistanceRange"),
                new ShaderFloat(0.5f, "MsdfDistanceRangeMiddle"),
                new ShaderFloat(0.5f, "MsdfFillBias"),
                new ShaderInt(0, "TextDebugMode"),
                new ShaderInt(0, "TextRenderLayer"),
                new ShaderInt(0, "TextRenderLayer_VTX"),
            }, [snapshot!.Texture], Array.Empty<XRShader>());
        XRDataBuffer<Vector4> transforms = BorderBuffer("GlyphTransformsBuffer", 0);
        XRDataBuffer<Vector4> texCoords = BorderBuffer("GlyphTexCoordsBuffer", 1);
        XRDataBuffer<Vector4> instances = BorderBuffer("TextInstanceBuffer", 2);
        XRDataBuffer<uint> glyphIndices = TextIndexBuffer("GlyphTextIndexBuffer", 3);
        XRDataBuffer<uint> drawIndices = TextIndexBuffer("GlyphDrawIndexBuffer", 4);
        try
        {
            _renderer.Mesh = _mesh;
            _renderer.Material = material;
            _renderer.Buffers["GlyphTransformsBuffer"] = transforms;
            _renderer.Buffers["GlyphTexCoordsBuffer"] = texCoords;
            _renderer.Buffers["TextInstanceBuffer"] = instances;
            _renderer.Buffers["GlyphTextIndexBuffer"] = glyphIndices;
            _renderer.Buffers["GlyphDrawIndexBuffer"] = drawIndices;
            UIIndexedDrawIntent intent = new(_renderer, _mesh.GeometryRevision,
                EPrimitiveType.Triangles, IndexSize.TwoBytes, 6, 2, 0);
            UIStaticDrawContract text = new(1,
                new UICompositionOwner(new UIElementIdentity(2, 1),
                    EUICompositionLayerKind.StaticChrome),
                3, 4, 5, 6, intent)
            {
                CapturedMesh = _mesh,
                IsText = true,
                TextAtlasSnapshot = snapshot,
                TextAtlas = snapshot.Texture,
                TextDistanceRangeMiddle = 0.5f,
                TextMsdfFillBias = 0.5f,
                TextGlyphTransforms = transforms,
                TextGlyphTransformsRevision = transforms.Revision,
                TextGlyphTexCoords = texCoords,
                TextGlyphTexCoordsRevision = texCoords.Revision,
                TextInstances = instances,
                TextInstancesRevision = instances.Revision,
                TextGlyphIndices = glyphIndices,
                TextGlyphIndicesRevision = glyphIndices.Revision,
                TextDrawIndices = drawIndices,
                TextDrawIndicesRevision = drawIndices.Revision,
            };
            Assert.That(text.MatchesIntent(_renderer, 2), Is.True);
            material.SetFloat("MsdfFillBias", 0.75f);
            Assert.That(text.MatchesIntent(_renderer, 2), Is.False);
            material.SetFloat("MsdfFillBias", 0.5f);
            Assert.That(text.MatchesIntent(_renderer, 2), Is.True);
            Assert.That((text with { TextAtlasType = 1 }) == text, Is.False);
            Assert.That((text with { TextDistanceRange = 4.0f }) == text, Is.False);
            source.Destroy(true);
            Assert.That(text.MatchesIntent(_renderer, 2), Is.True,
                "The published private atlas remains valid after source retirement.");
            foreach (XRDataBuffer changed in new XRDataBuffer[]
                { transforms, texCoords, instances, glyphIndices, drawIndices })
            {
                UIStaticDrawContract candidate = text;
                if (changed is XRDataBuffer<Vector4> vec4Buffer)
                    Commit(vec4Buffer, new Vector4(2));
                else if (changed is XRDataBuffer<uint> indexBuffer)
                {
                    XRBufferWriter<uint> writer = indexBuffer.Alloc<uint>(1);
                    writer.Span[0] = 2;
                    writer.Commit();
                }
                Assert.That(candidate.MatchesIntent(_renderer, 2), Is.False,
                    changed.AttributeName);
                text = changed.AttributeName switch
                {
                    "GlyphTransformsBuffer" => text with
                        { TextGlyphTransformsRevision = changed.Revision },
                    "GlyphTexCoordsBuffer" => text with
                        { TextGlyphTexCoordsRevision = changed.Revision },
                    "TextInstanceBuffer" => text with
                        { TextInstancesRevision = changed.Revision },
                    "GlyphTextIndexBuffer" => text with
                        { TextGlyphIndicesRevision = changed.Revision },
                    _ => text with { TextDrawIndicesRevision = changed.Revision },
                };
                Assert.That(text.MatchesIntent(_renderer, 2), Is.True);
            }
            XRDataBuffer<uint> replacementDrawIndices =
                TextIndexBuffer("GlyphDrawIndexBuffer", 4);
            _renderer.Buffers["GlyphDrawIndexBuffer"] = replacementDrawIndices;
            Assert.That(text.MatchesIntent(_renderer, 2), Is.False,
                "An equal-revision buffer replacement must not reuse a glyph run.");
            Assert.That((text with { TextAtlasSnapshot = null }).IsDefined, Is.False);
            Assert.That((text with { IsText = false }).IsDefined, Is.False);
            Assert.That((text with { Owner = new UICompositionOwner(
                new UIElementIdentity(2, 1), EUICompositionLayerKind.Overlay) }).IsDefined,
                Is.False, "Overlay text must use the dynamic draw path.");
        }
        finally
        {
            snapshot!.Release();
            material.Destroy(true);
            source.Destroy(true);
        }
    }

    private static XRDataBuffer<uint> TextIndexBuffer(string name, uint binding)
    {
        XRDataBuffer<uint> buffer = new(name, EBufferTarget.ShaderStorageBuffer,
            1, integral: true) { BindingIndexOverride = binding };
        XRBufferWriter<uint> writer = buffer.Alloc<uint>(1);
        writer.Span[0] = 1;
        writer.Commit();
        return buffer;
    }

    private static XRDataBuffer<Vector4> BorderBuffer(string name, uint binding)
    {
        XRDataBuffer<Vector4> buffer = new(name, EBufferTarget.ShaderStorageBuffer, 1)
        {
            BindingIndexOverride = binding,
        };
        Commit(buffer, Vector4.One);
        return buffer;
    }

    private static void Commit(XRDataBuffer<Vector4> buffer, Vector4 value)
    {
        XRBufferWriter<Vector4> writer = buffer.Alloc<Vector4>(1);
        writer.Span[0] = value;
        writer.Commit();
    }
}
