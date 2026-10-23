using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIBatchCollectionTests
{
    private UITestShaderScope _shaderScope = null!;
    [SetUp] public void SetUpShaderAssets() => _shaderScope = new UITestShaderScope();
    [TearDown] public void RestoreShaderAssets() => _shaderScope.Dispose();

    [Test]
    public void MaterialRunsSplitAtPainterOrderBoundaries()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = CreateCollection(pass);
        using UIBatchCollector collector = new();

        AddSolid(collector, commands, pass, 4);
        AddSolid(collector, commands, pass, 4);
        AddSolid(collector, commands, pass, 5);
        AddSolid(collector, commands, pass, 4);

        commands.GetUpdatingPassCommandCount(pass).ShouldBe(3);
    }

    [Test]
    public void UnchangedPainterOrderRunKeepsOneMarker()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = CreateCollection(pass);
        using UIBatchCollector collector = new();

        AddSolid(collector, commands, pass, 2);
        AddSolid(collector, commands, pass, 2);

        commands.GetUpdatingPassCommandCount(pass).ShouldBe(1);
    }

    [Test]
    public void DiscardedCandidateDoesNotKeepItsRunsOrMarkers()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = CreateCollection(pass);
        using UIBatchCollector collector = new();

        AddSolid(collector, commands, pass, 1);
        AddSolid(collector, commands, pass, 2);
        commands.DiscardUpdatingCommands();
        collector.DiscardCollectedCandidate();

        AddSolid(collector, commands, pass, 3);
        commands.GetUpdatingPassCommandCount(pass).ShouldBe(1);
        collector.SwapBuffers();
        collector.HasBatchData.ShouldBeTrue();
    }

    [Test]
    public void IdenticalFrozenRenderStorageReusesPublishedRuns()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = CreateCollection(pass);
        UIRenderRecord[] records =
        [
            new UIRenderRecord
            {
                RenderInfo = null!, MembershipId = 7,
                Identity = new UIElementIdentity(7, 1),
                WorldMatrix = Matrix4x4.Identity,
                Bounds = new Vector4(0, 0, 16, 16),
                RenderPass = pass, ZIndex = 3, Material = null,
                QuadColor = Vector4.One, Visible = true, SupportsBatch = true,
            }
        ];
        UIPublication publication = new(1, 1, 1, 1, 1,
            ReadOnlyMemory<UILayoutNodeOutput>.Empty, ReadOnlyMemory<UIElementIdentity>.Empty,
            [], records, new BoundingRectangleF(0, 0, 100, 100),
            Matrix4x4.Identity, Matrix4x4.Identity);
        using UIBatchCollector collector = new();

        collector.CollectPublishedRuns(publication, commands, _ => Assert.Fail("Unexpected fallback"));
        collector.SwapBuffers();
        commands.SwapBuffers();
        collector.CollectPublishedRuns(publication, commands, _ => Assert.Fail("Unexpected fallback"));

        commands.GetUpdatingPassCommandCount(pass).ShouldBe(1);
        collector.RetainedRunReuseCount.ShouldBe(1ul);
        collector.SwapBuffers();
        collector.HasBatchData.ShouldBeTrue();
    }

    [Test]
    public void RecycledMembershipSlotChangesGeneration()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        RenderCommandCollection commands = CreateCollection(pass);
        using UIBatchCollector collector = new();
        UIRenderRecord first = new()
        {
            RenderInfo = null!, MembershipId = 12,
            Identity = new UIElementIdentity(12, 1), WorldMatrix = Matrix4x4.Identity,
            Bounds = new Vector4(0, 0, 10, 10), RenderPass = pass, ZIndex = 0,
            Material = null, Visible = true, SupportsBatch = true, QuadColor = Vector4.One,
        };
        collector.CollectPublishedRuns(CreatePublication([first], 1), commands, _ => Assert.Fail());
        ulong oldHandle = collector.GetRetainedPrimitiveHandle(12);
        oldHandle.ShouldNotBe(0ul);
        commands.DiscardUpdatingCommands();
        collector.DiscardCollectedCandidate();

        collector.CollectPublishedRuns(CreatePublication([], 2), commands, _ => Assert.Fail());
        collector.GetRetainedPrimitiveHandle(12).ShouldBe(0ul);
        commands.DiscardUpdatingCommands();
        collector.DiscardCollectedCandidate();

        UIRenderRecord replacement = first with
        {
            MembershipId = 13,
            Identity = new UIElementIdentity(13, 1),
        };
        collector.CollectPublishedRuns(CreatePublication([replacement], 3), commands, _ => Assert.Fail());
        ulong newHandle = collector.GetRetainedPrimitiveHandle(13);
        ((uint)newHandle).ShouldBe((uint)oldHandle);
        newHandle.ShouldNotBe(oldHandle);
    }

    [Test]
    public void ActiveMembershipKeepsItsHandleAcrossInvisiblePublications()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = CreateCollection(pass);
        UIElementIdentity identity = new(31, 1);
        ulong handle = collector.RegisterPrimitiveMembership(31, identity);

        collector.CollectPublishedRuns(CreatePublication([], 1), commands, _ => Assert.Fail());
        collector.GetRetainedPrimitiveHandle(31).ShouldBe(handle);
        collector.RegisterPrimitiveMembership(31, identity).ShouldBe(handle);
        commands.DiscardUpdatingCommands();
        collector.DiscardCollectedCandidate();
        collector.CollectPublishedRuns(CreatePublication([], 2), commands, _ => Assert.Fail());
        collector.GetRetainedPrimitiveHandle(31).ShouldBe(handle);

        collector.UnregisterPrimitiveMembership(31, handle);
        collector.GetRetainedPrimitiveHandle(31).ShouldBe(0ul);
        ulong replacement = collector.RegisterPrimitiveMembership(32, new UIElementIdentity(32, 1));
        ((uint)replacement).ShouldBe((uint)handle);
        replacement.ShouldNotBe(handle);
    }

    [Test]
    public void ClearedCollectorRequiresFreshActivationHandle()
    {
        using UIBatchCollector collector = new();
        UIElementIdentity identity = new(41, 1);
        ulong oldHandle = collector.RegisterPrimitiveMembership(41, identity);

        collector.Clear();
        ulong newHandle = collector.RegisterPrimitiveMembership(41, identity);
        newHandle.ShouldNotBe(oldHandle);
        collector.UnregisterPrimitiveMembership(41, oldHandle);
        collector.GetRetainedPrimitiveHandle(41).ShouldBe(newHandle);
    }

    [Test]
    public void OlderPublicationCannotReplaceNewAttachmentHandle()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = CreateCollection(pass);
        UIElementIdentity oldIdentity = new(51, 1);
        UIElementIdentity newIdentity = new(51, 2);
        ulong oldHandle = collector.RegisterPrimitiveMembership(51, oldIdentity);
        collector.UnregisterPrimitiveMembership(51, oldHandle);
        ulong newHandle = collector.RegisterPrimitiveMembership(51, newIdentity);
        UIRenderRecord stale = new()
        {
            RenderInfo = null!, MembershipId = 51, Identity = oldIdentity,
            WorldMatrix = Matrix4x4.Identity, Bounds = new Vector4(0, 0, 10, 10),
            RenderPass = pass, ZIndex = 0, Material = null,
            Visible = true, SupportsBatch = true, QuadColor = Vector4.One,
        };

        collector.CollectPublishedRuns(CreatePublication([stale], 1), commands, _ => Assert.Fail());

        collector.GetRetainedPrimitiveHandle(51).ShouldBe(newHandle);
        collector.GetRetainedPrimitiveHandle(51, newIdentity).ShouldBe(newHandle);
        collector.GetRetainedPrimitiveHandle(51, oldIdentity).ShouldNotBe(newHandle);
    }

    [Test]
    public void PublishedMaterialQuadScalesUnitGeometryByFrozenLayoutSize()
    {
        PublishedQuadProbe component = new();
        UILayoutNodeOutput output = new() { ActualSize = new Vector2(120, 35) };
        Matrix4x4 canvasMatrix = Matrix4x4.CreateTranslation(5, 7, 0);

        Matrix4x4 model = component.Compose(in output, in canvasMatrix);

        Vector3.Transform(new Vector3(1, 1, 0), model).ShouldBe(new Vector3(125, 42, 0));
    }

    [Test]
    public void CustomMaterialUniformCallbackDisqualifiesSharedBatch()
    {
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        UIMaterialComponent component = new();
        component.SetBatchSolidColor(Vector4.One);
        XRMaterial material = component.Material!;
        material.HasEngineSemantic.ShouldBeFalse();
        material.Parameter<ShaderVector4>("MatColor").ShouldNotBeNull();
        UIBatchCollector.HasWebGpuRasterProfile(material.RenderOptions).ShouldBeTrue();
        Assert.That(component.SupportsBatchedRendering, Is.True,
            $"Default desktop UI quad must batch: semantic={material.EngineSemantic}, " +
            $"shaders={material.Shaders.Count}, textures={material.Textures.Count}, " +
            $"raster={UIBatchCollector.HasWebGpuRasterProfile(material.RenderOptions)}");
        Action<XRMaterialBase, XRRenderProgram> customHandler = static (_, _) => { };

        material.SettingUniforms += customHandler;
        component.SupportsBatchedRendering.ShouldBeFalse();

        material.SettingUniforms -= customHandler;
        component.SupportsBatchedRendering.ShouldBeTrue();
    }

    [Test]
    public void DerivedMaterialComponentRequiresExplicitBatchContract()
    {
        new PublishedQuadProbe().SupportsBatchedRendering.ShouldBeFalse();
    }

    [Test]
    public void CommonQuadColorsStayIndependentAcrossComponents()
    {
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        UIMaterialComponent first = new();
        UIMaterialComponent second = new();
        Vector4 red = new(1, 0, 0, 1);
        Vector4 blue = new(0, 0, 1, 1);

        first.SetQuadColor("MatColor", red);
        second.SetQuadColor("MatColor", blue);

        first.Mesh.ShouldBeNull();
        second.Mesh.ShouldBeNull();
        first.SupportsBatchedRendering.ShouldBeTrue();
        second.SupportsBatchedRendering.ShouldBeTrue();
        first.Material.ShouldNotBeSameAs(second.Material);
        first.Material!.Parameter<ShaderVector4>("MatColor")!.Value.ShouldBe(red);
        second.Material!.Parameter<ShaderVector4>("MatColor")!.Value.ShouldBe(blue);
        first.SupportsBatchedRendering.ShouldBeTrue();
        second.SupportsBatchedRendering.ShouldBeTrue();
    }

    [Test]
    public void IntrinsicAtlasImageKeepsPerInstanceUvAndConditionalUnbind()
    {
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        XRTexture2D atlas = new()
        {
            Mipmaps = [new Mipmap2D(2, 2, new byte[16])],
            SizedInternalFormat = ESizedInternalFormat.Rgba8,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };
        UIMaterialComponent first = new();
        UIMaterialComponent second = new();
        UIMaterialComponent escaped = new();
        UIMaterialComponent vetoedImage = new();
        UIMaterialComponent vetoedMaterial = new();
        UIMaterialComponent customBorrowing = new();
        UIMaterialComponent customUnrelated = new();
        XRTexture2D unrelated = new();
        XRMaterial? borrowedCustom = null;
        XRMaterial? unrelatedCustom = null;
        bool vetoImageChange = true;
        bool vetoMaterialChange = true;
        Vector4 uv = new(0, 0, 0.5f, 0.5f);

        try
        {
        first.SetBatchImage(atlas, new Vector4(1, 0, 0, 1), uv);
        second.SetBatchImage(atlas, new Vector4(0, 0, 1, 1), new Vector4(0.5f, 0, 1, 0.5f));

        first.Mesh.ShouldBeNull();
        second.Mesh.ShouldBeNull();
        first.SupportsBatchedRendering.ShouldBeTrue();
        second.SupportsBatchedRendering.ShouldBeTrue();
        first.BatchImageRegion.ShouldBe(uv);
        Should.Throw<NotSupportedException>(() => _ = first.Material);
        first.ClearBatchImage(unrelated).ShouldBeFalse();
        first.BatchImageRegion.ShouldBe(uv);
        first.ClearBatchImage(atlas).ShouldBeTrue();
        first.BatchImageRegion.ShouldBe(new Vector4(0, 0, 1, 1));
        second.BatchImageRegion.ShouldBe(new Vector4(0.5f, 0, 1, 0.5f));
        Should.Throw<ArgumentOutOfRangeException>(() => second.SetBatchImageRegion(new Vector4(0.8f, 0, 0.2f, 1)));

        escaped.SetBatchImage(atlas, Vector4.One, new Vector4(0, 0, 1, 1));
        escaped.Material!.Textures[0].ShouldBeSameAs(atlas);
        escaped.ClearBatchImage(atlas).ShouldBeTrue();
        escaped.Mesh.ShouldBeNull();
        escaped.SupportsBatchedRendering.ShouldBeTrue();

        vetoedImage.SetBatchImage(atlas, Vector4.One, new Vector4(0, 0, 1, 1));
        vetoedImage.PropertyChanging += (_, args) =>
        {
            if (vetoImageChange && args is XRPropertyChangingEventArgs<XRTexture2D?> change &&
                args.PropertyName == nameof(UIMaterialComponent.SetBatchSolidColor) &&
                change.NewValue is null)
                change.AllowChange = false;
        };
        Should.Throw<InvalidOperationException>(() => vetoedImage.ClearBatchImage(atlas));

        vetoedMaterial.SetBatchImage(atlas, Vector4.One, new Vector4(0, 0, 1, 1));
        XRMaterial borrowed = vetoedMaterial.Material!;
        vetoedMaterial.PropertyChanging += (_, args) =>
        {
            if (vetoMaterialChange && args is XRPropertyChangingEventArgs<XRMaterial?> change &&
                args.PropertyName == nameof(UIMaterialComponent.Material) &&
                change.NewValue is null)
                change.AllowChange = false;
        };
        Should.Throw<InvalidOperationException>(() => vetoedMaterial.ClearBatchImage(atlas));
        vetoedMaterial.Mesh.ShouldNotBeNull();
        vetoedMaterial.Material.ShouldBeSameAs(borrowed);

        customBorrowing.SetBatchImage(atlas, Vector4.One, new Vector4(0, 0, 1, 1));
        borrowedCustom = UIMaterialComponent.CreateImageMaterial(atlas, Vector4.One);
        customBorrowing.Material = borrowedCustom;
        Should.Throw<InvalidOperationException>(() => customBorrowing.ClearBatchImage(atlas));
        customBorrowing.Material.ShouldBeSameAs(borrowedCustom);

        customUnrelated.SetBatchImage(atlas, Vector4.One, new Vector4(0, 0, 1, 1));
        unrelatedCustom = UIMaterialComponent.CreateImageMaterial(unrelated, Vector4.One);
        customUnrelated.Material = unrelatedCustom;
        customUnrelated.ClearBatchImage(atlas).ShouldBeTrue();
        customUnrelated.Material.ShouldBeSameAs(unrelatedCustom);
        customUnrelated.Material = null;
        customUnrelated.Material!.Textures.ShouldBeEmpty();
        }
        finally
        {
            vetoImageChange = false;
            vetoMaterialChange = false;
            customBorrowing.Material = null;
            foreach (UIMaterialComponent component in new[] { first, second, escaped, vetoedImage, vetoedMaterial, customBorrowing, customUnrelated })
            {
                component.ClearBatchImage(atlas);
                component.Destroy(true);
            }
            borrowedCustom?.Destroy(true);
            unrelatedCustom?.Destroy(true);
            unrelated.Destroy(true);
            atlas.Destroy(true);
        }
    }

    [Test]
    public void CanvasWorldCollectionSuppressionRestoresAuthoredVisibility()
    {
        UIMaterialComponent component = new();
        component.RenderInfo3D.IsVisible = true;

        component.SetCanvasWorldCollectionSuppressed(true);
        component.RenderInfo3D.IsVisible.ShouldBeFalse();
        component.SetCanvasWorldCollectionSuppressed(false);
        component.RenderInfo3D.IsVisible.ShouldBeTrue();

        component.RenderInfo3D.IsVisible = false;
        component.SetCanvasWorldCollectionSuppressed(true);
        component.SetCanvasWorldCollectionSuppressed(false);
        component.RenderInfo3D.IsVisible.ShouldBeFalse();
    }

    [Test]
    public void CanonicalDesktopImageIsEligibleForOwnedTextureBatch()
    {
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        XRTexture2D texture = new()
        {
            Mipmaps = [new Mipmap2D(2, 2, new byte[16])],
            SizedInternalFormat = ESizedInternalFormat.Rgba8,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
        };
        XRMaterial image = UIMaterialComponent.CreateImageMaterial(texture, Vector4.One);
        UIMaterialComponent component = new(image);

        component.SupportsBatchedRendering.ShouldBeTrue();
        image.SettingUniforms += static (_, _) => { };
        component.SupportsBatchedRendering.ShouldBeFalse();
    }

    [Test]
    public void DesktopImageColorSpaceRequiresMatchingGpuStorage()
    {
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(
            EngineMaterialConstructionTarget.DesktopGlsl);
        XRTexture2D texture = new()
        {
            Mipmaps = [new Mipmap2D(2, 2, new byte[16])],
            SizedInternalFormat = ESizedInternalFormat.Srgb8Alpha8,
            ImportedColorSpace = ETextureColorSpace.Srgb,
            Resizable = false,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
        };

        UIMaterialComponent component = new(UIMaterialComponent.CreateImageMaterial(texture, Vector4.One));
        component.SupportsBatchedRendering.ShouldBeTrue();

        texture.SizedInternalFormat = ESizedInternalFormat.Rgba8;
        UIMaterialComponent.TryGetDesktopImageProfile(texture, true, out string? reason).ShouldBeFalse();
        Assert.That(reason, Does.Contain("sRGB binding"));
        UIMaterialComponent.CreateImageMaterial(texture, Vector4.One).ShouldNotBeNull();
    }

    [Test]
    public void ResizableDesktopSrgbImageRequiresMatchingVulkanMipFormat()
    {
        XRTexture2D texture = new()
        {
            Mipmaps = [new Mipmap2D(2, 2, new byte[16])],
            SizedInternalFormat = ESizedInternalFormat.Srgb8Alpha8,
            ImportedColorSpace = ETextureColorSpace.Srgb,
            Resizable = true,
            MinFilter = ETexMinFilter.Linear,
            MagFilter = ETexMagFilter.Linear,
        };

        UIMaterialComponent.TryGetDesktopImageProfile(texture, true, out string? reason).ShouldBeFalse();
        Assert.That(reason, Does.Contain("first mip format"));
    }

    [Test]
    public void ChangingOneTextLengthKeepsOtherGlyphStorageStable()
    {
        RetainedGlyphRanges ranges = new();
        ranges.BeginFrame();
        uint first = ranges.Reserve(101, 0, 3).Offset;
        uint second = ranges.Reserve(202, 1, 5).Offset;
        ranges.EndFrame();

        ranges.BeginFrame();
        uint changed = ranges.Reserve(101, 0, 12).Offset;
        uint unchanged = ranges.Reserve(202, 1, 5).Offset;
        ranges.EndFrame();

        unchanged.ShouldBe(second);
        changed.ShouldNotBe(first);
    }

    [Test]
    public void ConsumedReceiptRetirementProofRemainsValidForEveryCohort()
    {
        UIReceiptRetirementLatch sharedReceipt = new();
        int backendQueries = 0;

        bool FirstCohortCanRetire()
        {
            if (sharedReceipt.ShouldQuery)
            {
                backendQueries++;
                sharedReceipt.Observe(true); // Backend consumes its one-shot proof.
            }
            return sharedReceipt.IsRetired;
        }

        FirstCohortCanRetire().ShouldBeTrue();
        FirstCohortCanRetire().ShouldBeTrue();
        backendQueries.ShouldBe(1);
    }

    private sealed class PublishedQuadProbe : UIMaterialComponent
    {
        public Matrix4x4 Compose(in UILayoutNodeOutput output, in Matrix4x4 canvasMatrix)
            => CapturePublicationModelMatrix(in output, in canvasMatrix);
    }

    private static UIPublication CreatePublication(UIRenderRecord[] records, ulong generation)
        => new(generation, 1, 1, 1, (long)generation,
            ReadOnlyMemory<UILayoutNodeOutput>.Empty, ReadOnlyMemory<UIElementIdentity>.Empty,
            [], records, new BoundingRectangleF(0, 0, 100, 100),
            Matrix4x4.Identity, Matrix4x4.Identity);

    private static void AddSolid(UIBatchCollector collector, RenderCommandCollection commands, int pass, int zIndex)
    {
        Matrix4x4 world = Matrix4x4.Identity;
        Vector4 color = Vector4.One;
        Vector4 bounds = new(0, 0, 16, 16);
        collector.AddMaterialQuad(pass, zIndex, commands, in world, in color, in bounds);
    }

    private static RenderCommandCollection CreateCollection(int pass)
        => new(new Dictionary<int, IComparer<RenderCommand>?>
        {
            [pass] = new NearToFarRenderCommandSorter()
        });
}
