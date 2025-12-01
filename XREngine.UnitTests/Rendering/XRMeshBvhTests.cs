using System;
using System.Numerics;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using SimpleScene.Util.ssBVH;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class XRMeshBvhTests
{
    [Test]
    public void GenerateBVH_BuildsExpectedBoundsAndTriangleCount()
    {
        var mesh = CreateUnitSquareMesh();

        mesh.GenerateBVH();
        var tree = mesh.BVHTree;

        tree.ShouldNotBeNull("BVH generation should produce a tree instance.");
        var root = tree!._rootBVH;
        root.ShouldNotBeNull();

        const float tolerance = 1e-3f;
        root.box.Min.X.ShouldBeLessThanOrEqualTo(0f + tolerance);
        root.box.Min.Y.ShouldBeLessThanOrEqualTo(0f + tolerance);
        root.box.Min.Z.ShouldBeLessThanOrEqualTo(0f + tolerance);
        root.box.Max.X.ShouldBeGreaterThanOrEqualTo(1f - tolerance);
        root.box.Max.Y.ShouldBeGreaterThanOrEqualTo(1f - tolerance);
        root.box.Max.Z.ShouldBeGreaterThanOrEqualTo(0f - tolerance);

        var nodes = tree.Traverse(static _ => true);
        var triangleTotal = 0;
        foreach (BVHNode<Triangle> node in nodes)
            triangleTotal += node.gobjects?.Count ?? 0;

        mesh.Triangles.ShouldNotBeNull();
        triangleTotal.ShouldBe(mesh.Triangles!.Count);
    }

    [Test]
    public void BVHTreeProperty_LazilyGeneratesTree()
    {
        var mesh = CreateUnitSquareMesh();

        mesh.BVHTree.ShouldBeNull();

        bool generated = SpinWait.SpinUntil(() => mesh.BVHTree is not null, TimeSpan.FromSeconds(1));
        generated.ShouldBeTrue("Lazy BVH generation should complete within the timeout.");
        mesh.BVHTree.ShouldNotBeNull();
    }

    [Test]
    public void GenerateBVH_RebuildsTreeAfterVertexMutation()
    {
        var mesh = CreateUnitSquareMesh();
        mesh.GenerateBVH();
        var originalRoot = mesh.BVHTree!._rootBVH;
        var originalBounds = originalRoot.box;

        mesh.SetPosition(0, new Vector3(-1f, -1f, 0f));
        mesh.GenerateBVH();

        var updatedRoot = mesh.BVHTree!._rootBVH;
        const float tolerance = 1e-3f;
        updatedRoot.box.Min.X.ShouldBeLessThanOrEqualTo(-1f + tolerance);
        updatedRoot.box.Min.Y.ShouldBeLessThanOrEqualTo(-1f + tolerance);
        updatedRoot.box.Min.Z.ShouldBeLessThanOrEqualTo(0f + tolerance);
        updatedRoot.box.Max.X.ShouldBeGreaterThanOrEqualTo(1f - tolerance);
        updatedRoot.box.Max.Y.ShouldBeGreaterThanOrEqualTo(1f - tolerance);
        updatedRoot.box.Max.Z.ShouldBeGreaterThanOrEqualTo(0f - tolerance);
        updatedRoot.box.ShouldNotBe(originalBounds);
    }

    private static XRMesh CreateUnitSquareMesh()
    {
        Vector3[] positions =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(0f, 1f, 0f)
        };

        return XRMesh.CreateTriangles(positions);
    }
}
