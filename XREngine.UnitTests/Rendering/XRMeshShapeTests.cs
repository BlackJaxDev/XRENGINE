using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class XRMeshShapeTests
{
    [Test]
    public void SolidCapsule_GeneratesClosedUnitNormalGeometryAlongRequestedAxis()
    {
        XRMesh mesh = XRMesh.Shapes.SolidCapsule(
            new Vector3(1.0f, 2.0f, 3.0f),
            Vector3.UnitZ,
            radius: 0.5f,
            halfHeight: 1.25f,
            segments: 12,
            hemisphereRings: 4);

        mesh.VertexCount.ShouldBe(98);
        mesh.Triangles.ShouldNotBeNull().Count.ShouldBe(192);
        Vector3.Distance(mesh.Bounds.Min, new Vector3(0.5f, 1.5f, 1.25f))
            .ShouldBeLessThan(0.0001f);
        Vector3.Distance(mesh.Bounds.Max, new Vector3(1.5f, 2.5f, 4.75f))
            .ShouldBeLessThan(0.0001f);

        foreach (var vertex in mesh.Vertices)
        {
            Vector3 normal = vertex.Normal.ShouldNotBeNull();
            normal.Length().ShouldBe(1.0f, 0.0001f);
        }
    }

    [Test]
    public void FromVolume_SolidCapsulePreservesCapsuleExtents()
    {
        Capsule capsule = new(Vector3.Zero, Vector3.UnitY, radius: 0.4f, halfHeight: 0.8f);

        XRMesh mesh = XRMesh.Shapes.FromVolume(capsule, wireframe: false).ShouldNotBeNull();

        Vector3.Distance(mesh.Bounds.Min, new Vector3(-0.4f, -1.2f, -0.4f))
            .ShouldBeLessThan(0.0001f);
        Vector3.Distance(mesh.Bounds.Max, new Vector3(0.4f, 1.2f, 0.4f))
            .ShouldBeLessThan(0.0001f);
    }
}
