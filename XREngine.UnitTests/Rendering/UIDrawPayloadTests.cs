using XREngine.Components;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIDrawPayloadTests
{
    private UITestShaderScope _scope = null!;
    [SetUp] public void InstallHost() => _scope = new UITestShaderScope();
    [TearDown] public void RestoreHost() => _scope.Dispose();

    private static RenderCommandMesh2D CreateDraw()
        => new(0)
        {
            Mesh = new XRMeshRenderer(XRMesh.Create(VertexQuad.PosZ(1, true, 0, false)),
                new XRMaterial([new ShaderFloat(2, "Value")], new XRShader(EShaderType.Fragment, "uniform float Value;\nuniform float Items[1];\nvoid main() {}"))),
        };

    [Test]
    public void PublishedDrawKeepsOwnedValuesAfterAuthoringMutation()
    {
        RenderCommandMesh2D source = CreateDraw();
        XRMaterial material = source.Mesh!.Material!;
        ShaderArrayValueHandler<ShaderFloat> array = new() { Values = [new ShaderFloat(3, "Item")] };
        material.Parameters = [material.Parameters[0], new ShaderArray<ShaderFloat>(array, "Items", null)];
        material.RenderOptions.StencilTest.FrontFace.Reference = 7;
        string originalSource = material.Shaders[0].GetResolvedSource();
        using UIDrawPayloadBuilder builder = new();
        builder.TryCapture(source, false, out UIDrawPayload? payload, out string? reason).ShouldBeTrue(reason);
        payload.ShouldNotBeNull();
        payload.Retain();
        try
        {
            ((ShaderFloat)material.Parameters[0]).Value = 20;
            array.Values![0].Value = 30;
            material.RenderOptions.StencilTest.FrontFace.Reference = 70;
            source.Mesh.Mesh!.Triangles![0].ReadFromString("2 1 0");
            material.Shaders[0].Source.Text = "void main() { discard; }";

            ((ShaderFloat)payload.Material.Parameters[0]).Value.ShouldBe(2);
            ((ShaderFloat)((ShaderArrayBase)payload.Material.Parameters[1]).CaptureValues![0]).Value.ShouldBe(3);
            payload.Material.RenderOptions.StencilTest.FrontFace.Reference.ShouldBe(7);
            payload.Renderer.Mesh!.Triangles![0].Point0.ShouldBe(0);
            payload.Material.Shaders[0].Source.Text.ShouldBe(originalSource);
            payload.Renderer.HasSettingUniformsHandlers.ShouldBeFalse();
            payload.Renderer.HasRenderDataPreparation.ShouldBeFalse();
            payload.Material.HasSettingUniformsHandlers.ShouldBeFalse();
            builder.TryCapture(source, false, out UIDrawPayload? replacement, out reason).ShouldBeTrue(reason);
            replacement.ShouldNotBeSameAs(payload);
            payload.IsRetired.ShouldBeFalse();
        }
        finally { payload.Release(); }
    }

    [Test]
    public void SourceObserversNotifyChangesAndDetachAfterReplacementAndDispose()
    {
        RenderCommandMesh2D first = CreateDraw();
        RenderCommandMesh2D second = CreateDraw();
        using UIDrawPayloadBuilder builder = new();
        int changes = 0;
        builder.SourceChanged += () => changes++;
        builder.TryCapture(first, false, out _, out string? reason).ShouldBeTrue(reason);
        ((ShaderFloat)first.Mesh!.Material!.Parameters[0]).Value = 5;
        changes.ShouldBeGreaterThan(0);
        int afterUniform = changes;
        first.Mesh.Material.Shaders[0].Source.Text += "\n// changed";
        changes.ShouldBeGreaterThan(afterUniform);
        int afterShader = changes;
        first.Mesh.Material.RenderOptions.StencilTest.FrontFace.Reference++;
        changes.ShouldBeGreaterThan(afterShader);
        builder.TryCapture(second, false, out _, out reason).ShouldBeTrue(reason);
        int afterReplacement = changes;
        ((ShaderFloat)first.Mesh.Material.Parameters[0]).Value = 7;
        changes.ShouldBe(afterReplacement);
        ((ShaderFloat)second.Mesh!.Material!.Parameters[0]).Value = 9;
        changes.ShouldBeGreaterThan(afterReplacement);
        builder.Dispose();
        int afterDispose = changes;
        ((ShaderFloat)second.Mesh.Material.Parameters[0]).Value = 11;
        changes.ShouldBe(afterDispose);
    }

    [Test]
    public void TextRefreshUsesReplacementAtlasAndCurrentFontPaintValues()
    {
        XRTexture2D first = new();
        XRTexture2D second = new();
        FontGlyphSet font = new()
        {
            Glyphs = new Dictionary<string, FontGlyphSet.Glyph>(),
            Atlas = first,
            AtlasType = EFontAtlasType.Mtsdf,
        };
        XREngine.Scene.SceneNode root = new("Text material", new UIBoundableTransform()) { IsActiveSelf = false };
        try
        {
            UITextMaterialRefreshProbe text = root.AddComponent<UITextMaterialRefreshProbe>()!;
            text.Font = font;
            text.RefreshMaterial();
            XRMaterial original = text.Mesh!.Material!;
            original.Textures[0].ShouldBeSameAs(first);
            font.Atlas = second;
            font.DistanceRange = 9;
            font.DistanceRangeMiddle = 0.25f;
            text.RefreshMaterial();
            text.Mesh!.Material.ShouldBeSameAs(original);
            original.Textures[0].ShouldBeSameAs(second);
            original.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRange").Value.ShouldBe(9);
            original.Parameters.OfType<ShaderFloat>().Single(x => x.Name == "MsdfDistanceRangeMiddle").Value.ShouldBe(0.25f);
            font.AtlasType = font.AtlasType == EFontAtlasType.Mtsdf ? EFontAtlasType.Msdf : EFontAtlasType.Mtsdf;
            text.RefreshMaterial();
            text.Mesh!.Material.ShouldNotBeSameAs(original);
            text.Mesh.Material!.Textures[0].ShouldBeSameAs(second);
        }
        finally
        {
            root.Destroy(true);
            font.Destroy(true);
            first.Destroy(true);
            second.Destroy(true);
        }
    }

    [Test]
    public void DisabledCommandEligibilityStillInvalidatesItsCanvas()
    {
        UICanvasTransform transform = new() { Width = 100, Height = 100 };
        XREngine.Scene.SceneNode root = new("Disabled draw", transform);
        root.SetWorldContext(new XREngine.UnitTests.Physics.TestWorldContext());
        try
        {
            root.AddComponent<UICanvasComponent>();
            UIPublicationMutationProbe component = root.NewChild("Draw").AddComponent<UIPublicationMutationProbe>()!;
            component.RenderCommand2D.Enabled = false;
            long disabledRevision = transform.ChangeJournal.Revision;
            component.RenderCommand2D.Enabled = true;
            transform.ChangeJournal.Revision.ShouldBeGreaterThan(disabledRevision);
            component.RenderCommand2D.Instances = 0;
            long emptyRevision = transform.ChangeJournal.Revision;
            component.RenderCommand2D.Instances = 1;
            transform.ChangeJournal.Revision.ShouldBeGreaterThan(emptyRevision);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CallbackCaptureRejectsWholeDrawWithoutRunningSourceHandlers()
    {
        RenderCommandMesh2D source = CreateDraw();
        int callbacks = 0;
        source.Mesh!.PreparingRenderData += () => callbacks++;
        using UIDrawPayloadBuilder builder = new();

        builder.TryCapture(source, false, out UIDrawPayload? payload, out string? reason).ShouldBeFalse();

        payload.ShouldBeNull();
        (reason ?? string.Empty).ShouldContain("CallbackAdapterRequired");
        callbacks.ShouldBe(0);
    }

    [Test]
    public void GlyphBufferCopyIsIndependentAndUsesRequiredFixedResources()
    {
        RenderCommandMesh2D source = CreateDraw();
        XRDataBuffer<Vector4> glyphs = new("GlyphTransformsBuffer", EBufferTarget.ShaderStorageBuffer, 1u)
        {
            BindingIndexOverride = 0,
        };
        glyphs.SetDataRaw<Vector4>([new Vector4(1, 2, 3, 4)]);
        source.Mesh!.Buffers.Add(glyphs.AttributeName, glyphs);
        using UIDrawPayloadBuilder builder = new();
        builder.TryCapture(source, false, out UIDrawPayload? payload, out string? reason).ShouldBeTrue(reason);
        glyphs.SetDataRaw<Vector4>([new Vector4(20)]);

        payload!.Renderer.Buffers[glyphs.AttributeName].Get<Vector4>(0).ShouldBe(new Vector4(1, 2, 3, 4));
        payload.Renderer.BindingPublishers[0].ShouldBeAssignableTo<IRenderResourceBindingPublisher>();
        ((IRenderResourceBindingPublisher)payload.Renderer.BindingPublishers[0]).RequiresReadyDescriptorResources.ShouldBeTrue();
    }

    [Test]
    public void UnchangedCaptureReusesPayloadAndLastClaimControlsRetirement()
    {
        RenderCommandMesh2D source = CreateDraw();
        UIDrawPayloadBuilder builder = new();
        builder.TryCapture(source, false, out UIDrawPayload? first, out string? reason).ShouldBeTrue(reason);
        builder.TryCapture(source, false, out UIDrawPayload? second, out reason).ShouldBeTrue(reason);
        second.ShouldBeSameAs(first);
        first!.Retain();
        builder.Dispose();
        first.IsRetired.ShouldBeFalse();
        first.Release();
        first.IsRetired.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(first.Release);
    }

    [Test]
    public void BoundedCaptureFailureKeepsPreviousPayloadAlive()
    {
        RenderCommandMesh2D source = CreateDraw();
        using UIDrawPayloadBuilder builder = new();
        builder.TryCapture(source, false, out UIDrawPayload? first, out string? reason).ShouldBeTrue(reason);
        first!.Retain();
        try
        {
            source.Mesh!.Material!.Parameters =
            [new ShaderArray<ShaderFloat>(new ShaderArrayValueHandler<ShaderFloat>(65537), "TooLarge", null)];

            builder.TryCapture(source, false, out UIDrawPayload? failed, out reason).ShouldBeFalse();

            failed.ShouldBeNull();
            (reason ?? string.Empty).ShouldContain("capture limit");
            first.IsRetired.ShouldBeFalse();
            ((ShaderFloat)first.Material.Parameters[0]).Value.ShouldBe(2);
        }
        finally { first.Release(); }
    }

    [Test]
    public void PixelInvalidationCreatesNewTextureSnapshotAndPreservesOldPixels()
    {
        XRTexture2D source = new(1, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true);
        source.Mipmaps[0].DataBytes = [1, 2, 3, 4];
        UITextureSnapshot.TryAcquire(source, out UITextureSnapshot? first, out string? reason).ShouldBeTrue(reason);
        UITextureSnapshot.TryAcquire(source, out UITextureSnapshot? reused, out reason).ShouldBeTrue(reason);
        reused.ShouldBeSameAs(first);
        reused!.Release();
        int sourceChanges = 0;
        first!.SourceChanged += () => sourceChanges++;
        source.Mipmaps[0].DataBytes = [10, 20, 30, 40];
        source.Mipmaps[0].Invalidate();
        sourceChanges.ShouldBeGreaterThan(0);
        UITextureSnapshot.TryAcquire(source, out UITextureSnapshot? replacement, out reason).ShouldBeTrue(reason);
        try
        {
            replacement.ShouldNotBeSameAs(first);
            first!.Texture.Mipmaps[0].DataBytes.ShouldBe(new byte[] { 1, 2, 3, 4 });
            replacement!.Texture.Mipmaps[0].DataBytes.ShouldBe(new byte[] { 10, 20, 30, 40 });
            source.PushData();
            replacement.Matches(source).ShouldBeFalse();
        }
        finally
        {
            first!.Release();
            replacement!.Release();
            source.Destroy(true);
        }
    }

    [Test]
    public void ProducerLeaseSubstitutesTextureAndRemainsPinnedByPayloadClaim()
    {
        RenderCommandMesh2D source = CreateDraw();
        XRTexture2D authored = new(1, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true);
        var producer = new TestProducerLease();
        source.Mesh!.Material!.Textures = [authored];
        UIDrawPayloadBuilder builder = new() { ProducerLease = producer };
        builder.TryCapture(source, false, out UIDrawPayload? payload, out string? reason).ShouldBeTrue(reason);
        payload!.Material.Textures[0].ShouldBeSameAs(producer.Texture);
        source.Mesh.Material.Textures[0].ShouldBeSameAs(authored);
        payload.Retain();
        builder.Dispose();
        producer.Claims.ShouldBe(1);
        payload.Release();
        producer.Claims.ShouldBe(0);
    }

    [Test]
    public void NativeRetirementVetoKeepsProducerAndBufferClaimsQuarantined()
    {
        RenderCommandMesh2D source = CreateDraw();
        var producer = new TestProducerLease();
        source.Mesh!.Material!.Textures = [producer.Texture];
        UIDrawPayloadBuilder builder = new() { ProducerLease = producer };
        builder.TryCapture(source, false, out UIDrawPayload? payload, out string? reason).ShouldBeTrue(reason);
        payload!.Renderer.Destroying += static _ => false;

        builder.Dispose();

        producer.Claims.ShouldBe(1);
        payload.IsRetired.ShouldBeFalse();
        payload.Renderer.IsDestroyed.ShouldBeFalse();
        payload.Material.IsDestroyed.ShouldBeFalse();
        payload.Renderer.Mesh!.Buffers.Count.ShouldBeGreaterThan(0);
    }

    private sealed class TestProducerLease : IUIDrawProducerLease
    {
        public ulong Generation => 1;
        public XRTexture2D Texture { get; } = new(1, 1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte);
        public int Claims { get; private set; }
        public void Retain() => Claims++;
        public void Release() => Claims--;
    }
}
