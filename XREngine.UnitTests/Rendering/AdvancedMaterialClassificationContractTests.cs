using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedMaterialClassificationContractTests
{
    [Test]
    public void StructSizes_MatchGpuLayoutContracts()
    {
        Unsafe.SizeOf<AdvancedActiveTileRecord>().ShouldBe(16);
        Unsafe.SizeOf<AdvancedKernelTileRecord>().ShouldBe(16);
        Unsafe.SizeOf<AdvancedClassificationDispatchArguments>().ShouldBe(16);
        Unsafe.SizeOf<AdvancedClassificationGpuCounters>().ShouldBe(32);
    }

    [Test]
    public void TileDimensions_CalculatesGridCorrectly()
    {
        // 1920x1080 with 16x16 tiles
        uint tilesX = AdvancedClassificationTileDimensions.CalculateTilesX(1920u);
        uint tilesY = AdvancedClassificationTileDimensions.CalculateTilesY(1080u);
        tilesX.ShouldBe(120u);
        tilesY.ShouldBe(68u); // ceil(1080 / 16) = 68
        AdvancedClassificationTileDimensions.CalculateTotalTiles(1920u, 1080u).ShouldBe(120u * 68u);

        // 3840x2160 (4K) stereo with 16x16 tiles
        uint tiles4kX = AdvancedClassificationTileDimensions.CalculateTilesX(3840u);
        uint tiles4kY = AdvancedClassificationTileDimensions.CalculateTilesY(2160u);
        tiles4kX.ShouldBe(240u);
        tiles4kY.ShouldBe(135u);
        uint stereo4kTiles = AdvancedClassificationTileDimensions.CalculateTotalTiles(3840u, 2160u, viewCount: 2u);
        stereo4kTiles.ShouldBe(240u * 135u * 2u); // 64,800 <= DefaultActiveTileCapacity (65,536)
        stereo4kTiles.ShouldBeLessThanOrEqualTo(AdvancedRenderPipeline.DefaultActiveTileCapacity);
    }

    [Test]
    public void TileCoord_PackingAndUnpackingRoundTrips()
    {
        uint testX = 145u;
        uint testY = 89u;
        uint packed = AdvancedClassificationTileDimensions.PackTileCoord(testX, testY);
        (uint unpackedX, uint unpackedY) = AdvancedClassificationTileDimensions.UnpackTileCoord(packed);
        unpackedX.ShouldBe(testX);
        unpackedY.ShouldBe(testY);

        AdvancedActiveTileRecord record = new(testX, testY, viewIndex: 1u, activePixelCount: 200u, primaryKernelId: 5u);
        record.TileCoord.TileX.ShouldBe(testX);
        record.TileCoord.TileY.ShouldBe(testY);
        record.ViewIndex.ShouldBe(1u);
        record.ActivePixelCount.ShouldBe(200u);
        record.PrimaryKernelId.ShouldBe(5u);
    }

    [Test]
    public void ClassificationKey_EqualityAndHashDistribution()
    {
        AdvancedClassificationKey key1 = new(shadingKernelId: 3u, materialLayoutHash: 0x12345678UL, coverageClass: 1u, derivativeMode: 2u, viewMode: 0u);
        AdvancedClassificationKey key2 = new(shadingKernelId: 3u, materialLayoutHash: 0x12345678UL, coverageClass: 1u, derivativeMode: 2u, viewMode: 0u);
        AdvancedClassificationKey keyDifferent = new(shadingKernelId: 4u, materialLayoutHash: 0x12345678UL, coverageClass: 1u, derivativeMode: 2u, viewMode: 0u);

        (key1 == key2).ShouldBeTrue();
        (key1 != keyDifferent).ShouldBeTrue();
        key1.GetHashCode().ShouldBe(key2.GetHashCode());
        key1.Equals(key2).ShouldBeTrue();
        key1.Equals(keyDifferent).ShouldBeFalse();
    }

    [Test]
    public void ResourceNames_ProduceConsistentSlotIdentifiers()
    {
        AdvancedClassificationResourceNames.ActiveTiles(0u).ShouldBe("AdvancedClassification.ActiveTiles.Slot0");
        AdvancedClassificationResourceNames.KernelTiles(1u).ShouldBe("AdvancedClassification.KernelTiles.Slot1");
        AdvancedClassificationResourceNames.Counters(2u).ShouldBe("AdvancedClassification.Counters.Slot2");
        AdvancedClassificationResourceNames.DispatchArgs(3u).ShouldBe("AdvancedClassification.DispatchArgs.Slot3");
        AdvancedClassificationResourceNames.DebugOutput.ShouldBe("AdvancedClassification.DebugOutput");
    }

    [Test]
    public void SynchronizationContract_ValidatesUsageFlags()
    {
        (AdvancedClassificationSynchronizationContract.VisibilityInputUsage & RenderPipelineResourceUsage.SampledTexture)
            .ShouldBe(RenderPipelineResourceUsage.SampledTexture);

        (AdvancedClassificationSynchronizationContract.ClassificationOutputUsage & RenderPipelineResourceUsage.StorageBuffer)
            .ShouldBe(RenderPipelineResourceUsage.StorageBuffer);

        (AdvancedClassificationSynchronizationContract.ClassificationOutputUsage & RenderPipelineResourceUsage.IndirectBuffer)
            .ShouldBe(RenderPipelineResourceUsage.IndirectBuffer);
    }

    [Test]
    public void GpuCounters_OverflowDetection()
    {
        AdvancedClassificationGpuCounters counters = default;
        counters.HasOverflow.ShouldBeFalse();

        counters.OverflowFlags = AdvancedClassificationGpuCounters.OverflowActiveTiles;
        counters.HasOverflow.ShouldBeTrue();
    }
}
