using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedClusteredLightingContractTests
{
    [Test]
    public void StructSizes_MatchGpuLayoutContracts()
    {
        Unsafe.SizeOf<AdvancedFroxelRecord>().ShouldBe(16);
    }

    [Test]
    public void FroxelDimensions_CalculatesGridAndCapacities()
    {
        // 1920x1080 with 16x16 tiles and 24 depth slices
        uint tilesX = AdvancedFroxelGridDimensions.CalculateTilesX(1920u);
        uint tilesY = AdvancedFroxelGridDimensions.CalculateTilesY(1080u);
        tilesX.ShouldBe(120u);
        tilesY.ShouldBe(68u);

        uint totalFroxels1080p = AdvancedFroxelGridDimensions.CalculateTotalFroxels(1920u, 1080u, 24u);
        totalFroxels1080p.ShouldBe(120u * 68u * 24u); // 195,840
        totalFroxels1080p.ShouldBeLessThanOrEqualTo(AdvancedRenderPipeline.DefaultFroxelCapacity);

        // Verify stereo capacity bounds
        uint stereoFroxels = AdvancedFroxelGridDimensions.CalculateTotalFroxels(1920u, 1080u, 24u, viewCount: 2u);
        stereoFroxels.ShouldBe(195840u * 2u);
    }

    [Test]
    public void DepthSlicing_IsMonotonicAndClamped()
    {
        float nearPlane = 0.1f;
        float farPlane = 1000.0f;
        uint slices = 24u;

        // Near plane clamped to 0
        AdvancedFroxelGridDimensions.CalculateDepthSlice(0.05f, nearPlane, farPlane, slices).ShouldBe(0u);
        AdvancedFroxelGridDimensions.CalculateDepthSlice(0.1f, nearPlane, farPlane, slices).ShouldBe(0u);

        // Far plane clamped to slices - 1
        AdvancedFroxelGridDimensions.CalculateDepthSlice(1000.0f, nearPlane, farPlane, slices).ShouldBe(slices - 1u);
        AdvancedFroxelGridDimensions.CalculateDepthSlice(5000.0f, nearPlane, farPlane, slices).ShouldBe(slices - 1u);

        // Monotonic progression
        uint prevSlice = 0u;
        for (float d = 0.2f; d < 1000.0f; d *= 1.5f)
        {
            uint currentSlice = AdvancedFroxelGridDimensions.CalculateDepthSlice(d, nearPlane, farPlane, slices);
            currentSlice.ShouldBeGreaterThanOrEqualTo(prevSlice);
            currentSlice.ShouldBeLessThan(slices);
            prevSlice = currentSlice;
        }
    }

    [Test]
    public void FroxelIndex_ComputesUniqueIndices()
    {
        uint tilesX = 120u;
        uint tilesY = 68u;
        uint depthSlices = 24u;

        uint idx0 = AdvancedFroxelGridDimensions.GetFroxelIndex(0u, 0u, 0u, 0u, tilesX, tilesY, depthSlices);
        idx0.ShouldBe(0u);

        uint idx1 = AdvancedFroxelGridDimensions.GetFroxelIndex(1u, 0u, 0u, 0u, tilesX, tilesY, depthSlices);
        idx1.ShouldBe(1u);

        uint idxY = AdvancedFroxelGridDimensions.GetFroxelIndex(0u, 1u, 0u, 0u, tilesX, tilesY, depthSlices);
        idxY.ShouldBe(tilesX);

        uint idxZ = AdvancedFroxelGridDimensions.GetFroxelIndex(0u, 0u, 1u, 0u, tilesX, tilesY, depthSlices);
        idxZ.ShouldBe(tilesX * tilesY);

        uint idxView = AdvancedFroxelGridDimensions.GetFroxelIndex(0u, 0u, 0u, 1u, tilesX, tilesY, depthSlices);
        idxView.ShouldBe(tilesX * tilesY * depthSlices);
    }

    [Test]
    public void ResourceNames_ProduceConsistentSlotIdentifiers()
    {
        AdvancedClusteredLightingResourceNames.FroxelGrid(0u).ShouldBe("AdvancedClusteredLighting.FroxelGrid.Slot0");
        AdvancedClusteredLightingResourceNames.LightIndexList(1u).ShouldBe("AdvancedClusteredLighting.LightIndexList.Slot1");
        AdvancedClusteredLightingResourceNames.LightingCounters(2u).ShouldBe("AdvancedClusteredLighting.Counters.Slot2");

        AdvancedShadingResourceNames.OpaqueHdr.ShouldBe("AdvancedShading.OpaqueHdr");
        AdvancedShadingResourceNames.DenseVelocity.ShouldBe("AdvancedShading.DenseVelocity");
        AdvancedShadingResourceNames.ReactiveMask.ShouldBe("AdvancedShading.ReactiveMask");
    }
}
