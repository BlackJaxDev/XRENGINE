using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Trees;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class OctreeGpuOverflowTests
{
    private sealed class DummyItem : IOctreeItem
    {
        public AABB? LocalCullingVolume => null;
        public Matrix4x4 CullingOffsetMatrix => Matrix4x4.Identity;
        public OctreeNodeBase? OctreeNode { get; set; }
        public bool ShouldRender => true;
        public IRenderableBase? Owner => null;
    }

    [Test]
    public void CapacityCheckFailsWhenObjectCountExceedsMax()
    {
        var octree = new OctreeGPU<DummyItem>();
        uint objectCount = OctreeGPU<DummyItem>.MaxObjects + 1;
        uint nodeCount = objectCount * 2u - 1u;

        octree.TryValidateCapacityForTests(objectCount, nodeCount, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("MAX_OBJECTS");
    }

    [Test]
    public void CapacityCheckFailsWhenQueueWouldOverflow()
    {
        var octree = new OctreeGPU<DummyItem>();
        uint objectCount = (OctreeGPU<DummyItem>.QueueCapacity / 2u) + 10u;
        uint nodeCount = objectCount * 2u - 1u;

        octree.TryValidateCapacityForTests(objectCount, nodeCount, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("queue capacity");
    }

    [Test]
    public void CapacityCheckSucceedsWhenWithinLimits()
    {
        var octree = new OctreeGPU<DummyItem>();
        const uint objectCount = 4;
        uint nodeCount = objectCount * 2u - 1u;

        octree.TryValidateCapacityForTests(objectCount, nodeCount, out var reason).ShouldBeTrue();
        reason.ShouldBeNull();
    }
}
