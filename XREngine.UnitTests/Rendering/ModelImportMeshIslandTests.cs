using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ModelImportMeshIslandTests
{
    [Test]
    public void SeparateTriangleIslands_SplitsDisconnectedTriangles_AndConnectsDuplicatedPositions()
    {
        XRMesh mesh = new(
            [
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(10.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(11.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(10.0f, 1.0f, 0.0f)),
            ],
            new List<ushort> { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            Name = "GroupedMesh",
        };

        IReadOnlyList<XRMesh> islands = mesh.SeparateTriangleIslands();

        islands.Count.ShouldBe(2);
        islands[0].Name.ShouldBe("GroupedMesh Island 0");
        islands[0].IndexCount.ShouldBe(6);
        islands[0].Bounds.Min.X.ShouldBe(0.0f);
        islands[0].Bounds.Max.X.ShouldBe(1.0f);
        islands[1].Name.ShouldBe("GroupedMesh Island 1");
        islands[1].IndexCount.ShouldBe(3);
        islands[1].Bounds.Min.X.ShouldBe(10.0f);
        islands[1].Bounds.Max.X.ShouldBe(11.0f);
    }

    [Test]
    public void SeparateTriangleIslands_ReturnsOriginalMesh_WhenAlreadyConnected()
    {
        XRMesh mesh = new(
            [
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 1.0f, 0.0f)),
            ],
            new List<ushort> { 0, 1, 2, 1, 3, 2 });

        IReadOnlyList<XRMesh> islands = mesh.SeparateTriangleIslands();

        islands.Count.ShouldBe(1);
        islands[0].ShouldBeSameAs(mesh);
    }

    [Test]
    public void SplitSubMesh_PreservesMaterialAndRenderSettings_ForSeparatedIslands()
    {
        XRMaterial material = new() { Name = "SharedMaterial" };
        XRMesh mesh = new(
            [
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(5.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(6.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(5.0f, 1.0f, 0.0f)),
            ],
            new List<ushort> { 0, 1, 2, 3, 4, 5 });

        SubMesh subMesh = new(new SubMeshLOD(material, mesh, 42.0f)
        {
            GenerateAsync = true,
        })
        {
            Name = "GroupedSubMesh",
        };

        IReadOnlyList<SubMesh> separated = ModelImportMeshIslandSplitter.SplitSubMesh(subMesh, separateMeshIslands: true);

        separated.Count.ShouldBe(2);
        separated[0].Name.ShouldBe("GroupedSubMesh Island 0");
        separated[0].LODs.Min!.Material.ShouldBeSameAs(material);
        separated[0].LODs.Min!.GenerateAsync.ShouldBeTrue();
        separated[0].LODs.Min!.MaxVisibleDistance.ShouldBe(42.0f);
        separated[1].Name.ShouldBe("GroupedSubMesh Island 1");
        separated[1].LODs.Min!.Material.ShouldBeSameAs(material);
    }
}
