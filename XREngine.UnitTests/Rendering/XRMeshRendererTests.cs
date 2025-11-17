using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class XRMeshRendererTests
{
    [Test]
    public void UpdateIndirectDrawBuffer_WritesCommandsPerSubmesh()
    {
        var meshA = CreateSingleTriangleMesh();
        var meshB = CreateOffsetTriangleMesh();

        meshA.GenerateBVH();
        meshB.GenerateBVH();
        meshA.BVHTree.ShouldNotBeNull();
        meshB.BVHTree.ShouldNotBeNull();

        var renderer = new XRMeshRenderer();
        renderer.Submeshes = new EventList<XRMeshRenderer.SubMesh>();

        renderer.Submeshes.Add(new XRMeshRenderer.SubMesh
        {
            Mesh = meshA,
            InstanceCount = 2
        });
        renderer.Submeshes.Add(new XRMeshRenderer.SubMesh
        {
            Mesh = meshB,
            InstanceCount = 1
        });

        renderer.IndirectDrawBuffer.ShouldNotBeNull();
        var buffer = renderer.IndirectDrawBuffer!;
        var stride = (uint)Marshal.SizeOf<DrawElementsIndirectCommand>();

        var first = buffer.Get<DrawElementsIndirectCommand>(0);
        first.ShouldNotBeNull();
        first.Value.Count.ShouldBe((uint)meshA.IndexCount);
        first.Value.InstanceCount.ShouldBe(2u);
        first.Value.FirstIndex.ShouldBe(0u);
        first.Value.BaseVertex.ShouldBe(0);
        first.Value.BaseInstance.ShouldBe(0u);

        var second = buffer.Get<DrawElementsIndirectCommand>(stride);
        second.ShouldNotBeNull();
        second.Value.Count.ShouldBe((uint)meshB.IndexCount);
        second.Value.InstanceCount.ShouldBe(1u);
        second.Value.FirstIndex.ShouldBe((uint)meshA.IndexCount);
        second.Value.BaseVertex.ShouldBe(meshA.VertexCount);
        second.Value.BaseInstance.ShouldBe(1u);

        meshA.BVHTree.ShouldNotBeNull();
        meshB.BVHTree.ShouldNotBeNull();
    }

    [Test]
    public void GenerateCombinedIndexBuffer_ReturnsNullWhenNoSubmeshes()
    {
        var renderer = new XRMeshRenderer();
        renderer.Submeshes = new EventList<XRMeshRenderer.SubMesh>();

        renderer.GenerateCombinedIndexBuffer().ShouldBeNull();
    }

    private static XRMesh CreateSingleTriangleMesh()
    {
        Vector3[] positions =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f)
        };
        return XRMesh.CreateTriangles(positions);
    }

    private static XRMesh CreateOffsetTriangleMesh()
    {
        Vector3[] positions =
        {
            new Vector3(0f, 0f, 1f),
            new Vector3(1f, 0f, 1f),
            new Vector3(0f, 1f, 1f)
        };
        return XRMesh.CreateTriangles(positions);
    }
}
