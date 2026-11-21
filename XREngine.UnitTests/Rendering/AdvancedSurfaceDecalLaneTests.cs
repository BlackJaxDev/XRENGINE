using System.Collections.Generic;
using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Contracts of the Advanced mesh surface decal lane: importer classification, coverage
/// routing, and the bin predicates that keep decals out of the visibility and shadow lanes.
/// </summary>
[TestFixture]
public sealed class AdvancedSurfaceDecalLaneTests
{
    [Test]
    public void ClassifySurfaceDecal_MaskedDecalMaterialWithOpacityMap_IsSurfaceDecal()
        => ModelAssetImporter.ClassifySurfaceDecal(
                "dirt_decal",
                ETransparencyMode.Masked,
                DecalSlots(),
                EDecalMaterialMode.Auto)
            .ShouldBeTrue();

    [Test]
    public void ClassifySurfaceDecal_NameMatchIgnoresCase()
        => ModelAssetImporter.ClassifySurfaceDecal(
                "Wall_DECAL_01",
                ETransparencyMode.Masked,
                DecalSlots(),
                EDecalMaterialMode.Auto)
            .ShouldBeTrue();

    [Test]
    public void ClassifySurfaceDecal_MaterialWithoutDecalName_IsNotSurfaceDecal()
        => ModelAssetImporter.ClassifySurfaceDecal(
                "leaf",
                ETransparencyMode.Masked,
                DecalSlots(),
                EDecalMaterialMode.Auto)
            .ShouldBeFalse();

    [Test]
    public void ClassifySurfaceDecal_DecalWithoutOpacityMap_IsNotSurfaceDecal()
        => ModelAssetImporter.ClassifySurfaceDecal(
                "dirt_decal",
                ETransparencyMode.Masked,
                [Slot("dirt.png", TextureType.Diffuse)],
                EDecalMaterialMode.Auto)
            .ShouldBeFalse();

    [Test]
    public void ClassifySurfaceDecal_BlendedDecal_IsNotSurfaceDecal()
        => ModelAssetImporter.ClassifySurfaceDecal(
                "dirt_decal",
                ETransparencyMode.WeightedBlendedOit,
                DecalSlots(),
                EDecalMaterialMode.Auto)
            .ShouldBeFalse();

    [Test]
    public void ClassifySurfaceDecal_DisabledMode_KeepsMaskedMaterial()
        => ModelAssetImporter.ClassifySurfaceDecal(
                "dirt_decal",
                ETransparencyMode.Masked,
                DecalSlots(),
                EDecalMaterialMode.Disabled)
            .ShouldBeFalse();

    [Test]
    public void ResolveCoverage_SurfaceDecalFlag_SelectsDecalCoverage()
    {
        DrawMetadata command = new()
        {
            Flags = (uint)GPUIndirectRenderFlags.SurfaceDecal,
            StateClassID = (uint)EGpuMaterialStateClass.AlphaTested,
        };

        AdvancedPreparationExtractor.ResolveCoverage(in command)
            .ShouldBe(EAdvancedMaterialCoverageMode.Decal);
    }

    [Test]
    public void ResolveCoverage_MaskedDrawWithoutDecalFlag_StaysMasked()
    {
        DrawMetadata command = new()
        {
            StateClassID = (uint)EGpuMaterialStateClass.AlphaTested,
        };

        AdvancedPreparationExtractor.ResolveCoverage(in command)
            .ShouldBe(EAdvancedMaterialCoverageMode.Masked);
    }

    [Test]
    public void ResolveCoverage_TransparentFlag_WinsOverDecalFlag()
    {
        DrawMetadata command = new()
        {
            Flags = (uint)(GPUIndirectRenderFlags.Transparent | GPUIndirectRenderFlags.SurfaceDecal),
        };

        AdvancedPreparationExtractor.ResolveCoverage(in command)
            .ShouldBe(EAdvancedMaterialCoverageMode.Transparent);
    }

    [Test]
    public void StableBinHeader_DecalCoverage_IsOnlyForTheDecalLane()
    {
        VulkanPreparedStableBinHeader decal = Header(EAdvancedMaterialCoverageMode.Decal);
        VulkanPreparedStableBinHeader masked = Header(EAdvancedMaterialCoverageMode.Masked);

        decal.IsSurfaceDecal.ShouldBeTrue();
        masked.IsSurfaceDecal.ShouldBeFalse();
        // The visibility pipeline preparation skips decal bins, so they never hold a raster
        // pipeline: the visibility raster and both shadow lanes cannot draw them.
        decal.IsRasterReady.ShouldBeFalse();
        decal.IsDirectionalShadowReady.ShouldBeFalse();
        // Without a prepared blended pipeline the decal lane skips the bin as well.
        decal.IsDecalBufferReady.ShouldBeFalse();
        masked.IsDecalBufferReady.ShouldBeFalse();
    }

    private static VulkanPreparedStableBinHeader Header(EAdvancedMaterialCoverageMode coverage)
        => default(VulkanPreparedStableBinHeader) with
        {
            IndirectRange = new AdvancedIndirectRange(
                new AdvancedIndirectRangeKey(default, 0u, coverage, 1u, 0u, EAdvancedGeometryProducer.IndirectIndexed, false),
                0u, 1u, 0u, 0u, true),
        };

    private static List<TextureSlot> DecalSlots()
        => [Slot("dirt_decal.png", TextureType.Diffuse), Slot("dirt_decal_mask.png", TextureType.Opacity)];

    private static TextureSlot Slot(string filePath, TextureType textureType)
        => new(filePath, textureType, 0, default, 0, 1.0f, default, default, default, 0);
}
