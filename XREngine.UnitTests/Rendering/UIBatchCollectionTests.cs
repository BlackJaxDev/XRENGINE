using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
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
    public void EarlierPainterRunInsertionKeepsLaterGpuRunIdentities()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = CreateCollection(pass);
        static UIRenderRecord Quad(long id, int z) => new()
        {
            RenderInfo = null!, MembershipId = id, Identity = new UIElementIdentity(id, 1),
            WorldMatrix = Matrix4x4.Identity, Bounds = new Vector4(0, 0, 8, 8),
            RenderPass = pass, ZIndex = z, Material = null, Visible = true,
            SupportsBatch = true, QuadColor = Vector4.One,
        };
        collector.CollectPublishedRuns(CreatePublication([Quad(1, 1), Quad(2, 2)], 1), commands, _ => Assert.Fail());
        ulong[] before = ReadCollectedMaterialRunIds(collector, pass);
        collector.SwapBuffers();
        commands.DiscardUpdatingCommands();
        collector.CollectPublishedRuns(CreatePublication([Quad(3, 0), Quad(1, 1), Quad(2, 2)], 2), commands,
            _ => Assert.Fail());
        ulong[] after = ReadCollectedMaterialRunIds(collector, pass);

        after.Length.ShouldBe(3);
        after[0].ShouldNotBe(before[0]);
        after[1].ShouldBe(before[0]);
        after[2].ShouldBe(before[1]);
    }

    [Test]
    public void AlternatingRunInsertionReconcilesWithLinearHandleVisits()
    {
        const int pass = (int)EDefaultRenderPass.TransparentForward;
        const int count = 256;
        using UIBatchCollector collector = new();
        RenderCommandCollection commands = CreateCollection(pass);
        static UIRenderRecord Quad(long id, int z) => new()
        {
            RenderInfo = null!, MembershipId = id, Identity = new UIElementIdentity(id, 1),
            WorldMatrix = Matrix4x4.Identity, Bounds = new Vector4(0, 0, 8, 8),
            RenderPass = pass, ZIndex = z, Material = null, Visible = true,
            SupportsBatch = true, QuadColor = Vector4.One,
        };
        UIRenderRecord[] original = Enumerable.Range(0, count)
            .Select(index => Quad(index + 1, index & 1)).ToArray();
        collector.CollectPublishedRuns(CreatePublication(original, 1), commands, _ => Assert.Fail());
        ulong[] before = ReadCollectedMaterialRunIds(collector, pass);
        before.Length.ShouldBe(count);
        collector.SwapBuffers();
        commands.DiscardUpdatingCommands();

        UIRenderRecord[] inserted = [Quad(1000, 2), .. original];
        collector.CollectPublishedRuns(CreatePublication(inserted, 2), commands, _ => Assert.Fail());
        ulong[] after = ReadCollectedMaterialRunIds(collector, pass);
        for (int index = 0; index < count; index++)
            after[index + 1].ShouldBe(before[index]);
        collector.LastRunReconcileVisitCount.ShouldBeLessThanOrEqualTo(count * 2 + 1);

        MethodInfo buildActive = typeof(UIBatchCollector).GetMethod("BuildActiveGpuRunKeys",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var active = (HashSet<(int Pass, ulong RunId)>)typeof(UIBatchCollector)
            .GetField("_activeMaterialRunKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(collector)!;
        collector.SwapBuffers();
        buildActive.Invoke(collector, null);
        collector.LastActiveRunBuildVisitCount.ShouldBe(count + 1);
        active.Count.ShouldBe(count + 1);
        foreach (ulong id in after)
            active.Contains((pass, id)).ShouldBeTrue();

        commands.DiscardUpdatingCommands();
        collector.CollectPublishedRuns(CreatePublication(original, 3), commands, _ => Assert.Fail());
        collector.SwapBuffers();
        buildActive.Invoke(collector, null);
        collector.LastActiveRunBuildVisitCount.ShouldBe(count);
        active.Count.ShouldBe(count);
        active.Contains((pass, after[0])).ShouldBeFalse();
    }

    [Test]
    public void QuadSlotsKeepPayloadAddressesAcrossPainterReorderAndRecycling()
    {
        RetainedQuadSlots slots = new();
        slots.BeginFrame();
        uint first = slots.Reserve(101, 0).Index;
        uint second = slots.Reserve(202, 1).Index;
        uint third = slots.Reserve(303, 2).Index;
        slots.EndFrame();

        slots.BeginFrame();
        uint reorderedThird = slots.Reserve(303, 0).Index;
        uint inserted = slots.Reserve(404, 1).Index;
        uint reorderedFirst = slots.Reserve(101, 2).Index;
        uint reorderedSecond = slots.Reserve(202, 3).Index;
        slots.EndFrame();

        reorderedThird.ShouldBe(third);
        reorderedFirst.ShouldBe(first);
        reorderedSecond.ShouldBe(second);
        inserted.ShouldNotBe(first);
        inserted.ShouldNotBe(second);
        inserted.ShouldNotBe(third);

        slots.BeginFrame();
        slots.Reserve(303, 0).Index.ShouldBe(third);
        slots.Reserve(101, 1).Index.ShouldBe(first);
        slots.Reserve(404, 2).Index.ShouldBe(inserted);
        slots.EndFrame(); // Membership 202 is removed only after the current draw stream is known.

        slots.BeginFrame();
        uint recycled = slots.Reserve(505, 0).Index;
        slots.Reserve(303, 1).Index.ShouldBe(third);
        slots.Reserve(101, 2).Index.ShouldBe(first);
        slots.Reserve(404, 3).Index.ShouldBe(inserted);
        slots.EndFrame();
        recycled.ShouldBe(second);
        slots.HighWater.ShouldBe(4u);

        // A different GPU cohort keeps its own address space and cannot inherit this upload state.
        RetainedQuadSlots anotherCohort = new();
        anotherCohort.BeginFrame();
        anotherCohort.Reserve(505, 0).Index.ShouldBe(0u);
        anotherCohort.EndFrame();
    }

    [Test]
    public void FullQuadUploadInvalidatesClearedHoleBeforeIdenticalPayloadReusesIt()
    {
        using UIBatchCollector collector = new();
        using XRDataBuffer<Vector4> transforms = new("QuadTransformBuffer", EBufferTarget.ShaderStorageBuffer, 256);
        using XRDataBuffer<Vector4> colors = new("QuadColorBuffer", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<Vector4> bounds = new("QuadBoundsBuffer", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<Vector4> borderColors = new("QuadUvBuffer", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<Vector4> widths = new("QuadBorderWidthBuffer", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<uint> drawIndices = new("QuadDrawIndexBuffer", EBufferTarget.ShaderStorageBuffer, 64);
        Type gpuType = typeof(UIBatchCollector).GetNestedType("MaterialGPUResources", BindingFlags.NonPublic)!;
        object gpu = Activator.CreateInstance(gpuType, nonPublic: true)!;
        Type collectorType = typeof(UIBatchCollector);
        void Bind(string field, object value)
            => collectorType.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(collector, value);
        Bind("_matQuadTransformBuf", transforms);
        Bind("_matQuadColorBuf", colors);
        Bind("_matQuadBoundsBuf", bounds);
        Bind("_matQuadUvBuf", borderColors);
        Bind("_matQuadBorderWidthBuf", widths);
        Bind("_matQuadDrawIndexBuf", drawIndices);
        Bind("_matQuadCapacity", 64u);
        MethodInfo upload = collectorType.GetMethod("UploadRetainedMaterialData", BindingFlags.Instance | BindingFlags.NonPublic)!;

        static UIBatchCollector.MaterialQuadEntry Entry(ulong handle, int payload) => new()
        {
            PrimitiveHandle = handle,
            WorldMatrix = Matrix4x4.CreateTranslation(payload, 2, 3),
            Color = new Vector4(payload, 1, 1, 1),
            UIXYWH = new Vector4(payload, 2, 3, 4),
            BorderColor = new Vector4(payload, 3, 2, 1),
            BorderWidth = payload,
        };

        void Upload(List<UIBatchCollector.MaterialQuadEntry> entries)
        {
            upload.Invoke(collector, [entries, false, true, gpu]);
            gpuType.GetField("HasUploadedEntries")!.SetValue(gpu, true);
            gpuType.GetField("UploadedVariant")!.SetValue(gpu, 2);
        }

        List<UIBatchCollector.MaterialQuadEntry> entries = [];
        for (int value = 1; value <= 64; value++)
            entries.Add(Entry((ulong)value, value));
        Upload(entries);
        colors.GetDataRawAtIndex<Vector4>(31).ShouldBe(Entry(32, 32).Color);

        entries.RemoveAt(31);
        entries.Add(Entry(65, 65));
        Upload(entries); // New slot 64 forces growth; former slot 31 is cleared.
        colors.GetDataRawAtIndex<Vector4>(31).ShouldBe(Vector4.Zero);
        borderColors.GetDataRawAtIndex<Vector4>(31).ShouldBe(Vector4.Zero);
        widths.GetDataRawAtIndex<Vector4>(31).ShouldBe(Vector4.Zero);

        entries.Add(Entry(66, 32)); // Identical payload to the member removed from slot 31.
        Upload(entries);
        colors.GetDataRawAtIndex<Vector4>(31).ShouldBe(Entry(66, 32).Color);
        bounds.GetDataRawAtIndex<Vector4>(31).ShouldBe(Entry(66, 32).UIXYWH);
        borderColors.GetDataRawAtIndex<Vector4>(31).ShouldBe(Entry(66, 32).BorderColor);
        widths.GetDataRawAtIndex<Vector4>(31).X.ShouldBe(32f);
        transforms.GetDataRawAtIndex<Vector4>(31u * 4u + 3u).X.ShouldBe(32f);
        drawIndices.GetDataRawAtIndex<uint>(64).ShouldBe(31u);
    }

    [Test]
    public void TextInsertionAndGlyphGrowthKeepExistingInstanceAndGlyphAddresses()
    {
        using XRDataBuffer<Vector4> transforms = new("GlyphTransforms", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<Vector4> uvs = new("GlyphUvs", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<Vector4> instances = new("TextInstances", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<uint> textIndices = new("GlyphTextIndices", EBufferTarget.ShaderStorageBuffer, 64);
        using XRDataBuffer<uint> drawIndices = new("GlyphDrawIndices", EBufferTarget.ShaderStorageBuffer, 64);
        Type collectorType = typeof(UIBatchCollector);
        Type gpuType = collectorType.GetNestedType("TextGPUResources", BindingFlags.NonPublic)!;
        Type batchType = collectorType.GetNestedType("TextBatchData", BindingFlags.NonPublic)!;
        object gpu = Activator.CreateInstance(gpuType, nonPublic: true)!;
        object batch = Activator.CreateInstance(batchType, nonPublic: true)!;
        void Set(string name, object value) => gpuType.GetField(name)!.SetValue(gpu, value);
        Set("GlyphTransformsBuf", transforms);
        Set("GlyphTexCoordsBuf", uvs);
        Set("TextInstanceBuf", instances);
        Set("GlyphTextIndexBuf", textIndices);
        Set("GlyphDrawIndexBuf", drawIndices);
        Set("GlyphCapacity", 64u);
        Set("DrawCapacity", 64u);
        Set("TextCapacity", 8u);
        Set("NeedsPush", true);
        XRBufferWriter<Vector4> untouchedCapacity = instances.AllocAt<Vector4>(24, 1);
        untouchedCapacity.Span[0] = new Vector4(99, 98, 97, 96);
        untouchedCapacity.Commit();
        var entries = (List<UIBatchCollector.TextEntry>)batchType.GetField("Entries")!.GetValue(batch)!;
        MethodInfo upload = collectorType.GetMethod("UploadRetainedTextData", BindingFlags.Static | BindingFlags.NonPublic)!;
        static UIBatchCollector.TextEntry Entry(ulong handle, int glyphs) => new()
        {
            PrimitiveHandle = handle,
            WorldMatrix = Matrix4x4.CreateTranslation((float)handle, 0, 0),
            TextColor = new Vector4((float)handle, 1, 1, 1),
            GlyphCount = glyphs,
            Layout = new UITextLayoutResult(Vector2.One,
                Enumerable.Range(0, glyphs).Select(index => new UITextLayoutGlyph(
                    new Vector4((float)handle, index, 1, 1), Vector4.One, index, 1, 0)).ToArray(),
                [], 1, 1),
        };
        entries.Add(Entry(10, 2));
        entries.Add(Entry(20, 2));
        upload.Invoke(null, [batch, gpu]);
        var slots = (List<uint>)gpuType.GetField("ActiveTextSlots")!.GetValue(gpu)!;
        uint firstSlot = slots[0], secondSlot = slots[1];
        var ranges = (List<RetainedGlyphRanges.Allocation?>)gpuType.GetField("ActiveGlyphRanges")!.GetValue(gpu)!;
        uint secondGlyphOffset = ranges[1]!.Offset;
        instances.GetDataRawAtIndex<Vector4>(24).ShouldBe(new Vector4(99, 98, 97, 96));

        bool previousCounting = UIRuntimeCounters.Enabled;
        try
        {
            UIRuntimeCounters.Enabled = true;
            long beforeRanges = UIRuntimeCounters.Read(UIWorkCounter.BufferCommitRanges);
            entries[0] = Entry(10, 2); // Both adjacent layouts change together.
            entries[1] = Entry(20, 2);
            upload.Invoke(null, [batch, gpu]);
            (UIRuntimeCounters.Read(UIWorkCounter.BufferCommitRanges) - beforeRanges).ShouldBe(2L);
        }
        finally
        {
            UIRuntimeCounters.Enabled = previousCounting;
        }

        entries.Insert(0, Entry(30, 1));
        upload.Invoke(null, [batch, gpu]);
        slots[1].ShouldBe(firstSlot);
        slots[2].ShouldBe(secondSlot);
        ranges[2]!.Offset.ShouldBe(secondGlyphOffset);
        textIndices.GetDataRawAtIndex<uint>(secondGlyphOffset).ShouldBe(secondSlot);
        instances.GetDataRawAtIndex<Vector4>(secondSlot * 8u + 4u).X.ShouldBe(20f);

        entries[1] = Entry(10, 9); // Reallocate only this glyph range.
        upload.Invoke(null, [batch, gpu]);
        slots[2].ShouldBe(secondSlot);
        ranges[2]!.Offset.ShouldBe(secondGlyphOffset);
        textIndices.GetDataRawAtIndex<uint>(secondGlyphOffset).ShouldBe(secondSlot);
        transforms.GetDataRawAtIndex<Vector4>(secondGlyphOffset).X.ShouldBe(20f);
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

    private static ulong[] ReadCollectedMaterialRunIds(UIBatchCollector collector, int pass)
    {
        var pools = (IDictionary)typeof(UIBatchCollector)
            .GetField("_collectMaterialGroups", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(collector)!;
        object pool = pools[pass]!;
        int count = (int)pool.GetType().GetField("Count")!.GetValue(pool)!;
        var items = (IList)pool.GetType().GetField("Items")!.GetValue(pool)!;
        ulong[] ids = new ulong[count];
        for (int index = 0; index < count; index++)
            ids[index] = (ulong)items[index]!.GetType().GetField("RunId")!.GetValue(items[index])!;
        return ids;
    }

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
