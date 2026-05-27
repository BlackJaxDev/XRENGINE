using Assimp;
using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MeshBoundsCalculationTests
{
    [Test]
    public void SubMeshBounds_OffsetMesh_DoesNotIncludeOrigin()
    {
        XRMesh mesh = XRMesh.CreateTriangles(
            new Vector3(10.0f, 2.0f, -6.0f),
            new Vector3(12.0f, 5.0f, -4.0f),
            new Vector3(11.0f, 3.0f, -2.0f));

        SubMesh subMesh = new(new SubMeshLOD(material: null, mesh, maxVisibleDistance: 0.0f));

        subMesh.Bounds.Min.ShouldBe(mesh.Bounds.Min);
        subMesh.Bounds.Max.ShouldBe(mesh.Bounds.Max);
        subMesh.Bounds.Min.X.ShouldBe(10.0f);
        subMesh.Bounds.Max.Z.ShouldBe(-2.0f);
    }

    [Test]
    public unsafe void AssimpMeshBounds_OffsetMesh_DoesNotIncludeOrigin()
    {
        Mesh assimpMesh = new(PrimitiveType.Triangle);
        assimpMesh.Vertices.Add(new Vector3(20.0f, 3.0f, -2.0f));
        assimpMesh.Vertices.Add(new Vector3(24.0f, 7.0f, 1.0f));
        assimpMesh.Vertices.Add(new Vector3(22.0f, 4.0f, -1.0f));
        assimpMesh.Faces.Add(new Face([0, 1, 2]));

        using AssimpContext assimp = new();
        XRMesh mesh = new(assimpMesh, assimp, new Dictionary<string, List<SceneNode>>(), Matrix4x4.Identity);

        mesh.Bounds.Min.ShouldBe(new Vector3(20.0f, 3.0f, -2.0f));
        mesh.Bounds.Max.ShouldBe(new Vector3(24.0f, 7.0f, 1.0f));
    }
}
