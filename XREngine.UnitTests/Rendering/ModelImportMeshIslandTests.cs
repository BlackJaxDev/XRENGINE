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
    public void PartitionTrianglesSpatially_ProducesBoundedSpatialDrawUnits()
    {
        XRMesh mesh = new(
            [
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(10.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(11.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(10.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(20.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(21.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(20.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(30.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(31.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(30.0f, 1.0f, 0.0f)),
            ],
            new List<ushort> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 });

        IReadOnlyList<XRMesh> partitions = mesh.PartitionTrianglesSpatially(2);

        partitions.Count.ShouldBeInRange(2, 4);
        partitions.Sum(static partition => partition.IndexCount).ShouldBe(mesh.IndexCount);
        partitions.ShouldAllBe(static partition => partition.IndexCount <= 6);
        partitions[0].Bounds.Max.X.ShouldBeLessThan(partitions[1].Bounds.Min.X);
    }

    [Test]
    public void PartitionTrianglesSpatially_SplitsSceneWideDisconnectedGeometry_BelowTriangleLimit()
    {
        XRMesh mesh = new(
            [
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(0.2f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.2f, 0.0f, 0.0f)),
                new Vertex(new Vector3(0.2f, 1.0f, 0.0f)),
                new Vertex(new Vector3(100.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(101.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(100.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(100.2f, 0.0f, 0.0f)),
                new Vertex(new Vector3(101.2f, 0.0f, 0.0f)),
                new Vertex(new Vector3(100.2f, 1.0f, 0.0f)),
            ],
            new List<ushort> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 });

        IReadOnlyList<XRMesh> partitions = mesh.PartitionTrianglesSpatially(maxTrianglesPerPartition: 8);

        partitions.Count.ShouldBe(2);
        partitions.Sum(static partition => partition.IndexCount).ShouldBe(mesh.IndexCount);
        partitions[0].Bounds.Max.X.ShouldBeLessThan(10.0f);
        partitions[1].Bounds.Min.X.ShouldBeGreaterThan(90.0f);
    }

    [Test]
    public void PartitionTrianglesSpatially_PreservesMirroredTangentHandedness()
    {
        static Vertex CreateVertex(Vector3 position, float bitangentSign)
            => new(position)
            {
                Normal = Vector3.UnitZ,
                Tangent = Vector3.UnitX,
                BitangentSign = bitangentSign,
            };

        XRMesh mesh = new(
            [
                CreateVertex(new Vector3(0.0f, 0.0f, 0.0f), 1.0f),
                CreateVertex(new Vector3(1.0f, 0.0f, 0.0f), 1.0f),
                CreateVertex(new Vector3(0.0f, 1.0f, 0.0f), 1.0f),
                CreateVertex(new Vector3(0.0f, 0.0f, 0.0f), -1.0f),
                CreateVertex(new Vector3(1.0f, 0.0f, 0.0f), -1.0f),
                CreateVertex(new Vector3(0.0f, 1.0f, 0.0f), -1.0f),
                CreateVertex(new Vector3(100.0f, 0.0f, 0.0f), 1.0f),
                CreateVertex(new Vector3(101.0f, 0.0f, 0.0f), 1.0f),
                CreateVertex(new Vector3(100.0f, 1.0f, 0.0f), 1.0f),
                CreateVertex(new Vector3(100.0f, 0.0f, 0.0f), -1.0f),
                CreateVertex(new Vector3(101.0f, 0.0f, 0.0f), -1.0f),
                CreateVertex(new Vector3(100.0f, 1.0f, 0.0f), -1.0f),
            ],
            new List<ushort> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 });

        IReadOnlyList<XRMesh> partitions = mesh.PartitionTrianglesSpatially(maxTrianglesPerPartition: 8);

        partitions.Count.ShouldBe(2);
        partitions.ShouldAllBe(static partition =>
            partition.Vertices.Any(static vertex => vertex.BitangentSign > 0.0f) &&
            partition.Vertices.Any(static vertex => vertex.BitangentSign < 0.0f));

        Vertex positive = mesh.Vertices[0];
        Vertex negative = positive.HardCopy();
        negative.BitangentSign = -1.0f;
        positive.Equals(negative).ShouldBeFalse();
        new HashSet<Vertex> { positive, negative }.Count.ShouldBe(2);
        positive.HardCopy().BitangentSign.ShouldBe(1.0f);
    }

    [Test]
    public void PartitionTrianglesSpatially_BoundsLeafCountForPathologicalOutliers()
    {
        const int triangleCount = 40;
        const int maxTrianglesPerPartition = 10;
        List<Vertex> vertices = new(triangleCount * 3);
        List<ushort> indices = new(triangleCount * 3);
        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            float x = MathF.Pow(2.0f, triangleIndex);
            float size = MathF.Max(0.25f, x * 0.0001f);
            ushort vertexIndex = checked((ushort)vertices.Count);
            vertices.Add(new Vertex(new Vector3(x, 0.0f, 0.0f)));
            vertices.Add(new Vertex(new Vector3(x + size, 0.0f, 0.0f)));
            vertices.Add(new Vertex(new Vector3(x, size, 0.0f)));
            indices.Add(vertexIndex);
            indices.Add((ushort)(vertexIndex + 1));
            indices.Add((ushort)(vertexIndex + 2));
        }

        XRMesh mesh = new(vertices, indices);
        IReadOnlyList<XRMesh> partitions = mesh.PartitionTrianglesSpatially(maxTrianglesPerPartition);

        int mandatoryLeafCount = (triangleCount + maxTrianglesPerPartition - 1) / maxTrianglesPerPartition;
        int optionalLeafLimit = mandatoryLeafCount * 4;
        partitions.Count.ShouldBeInRange(mandatoryLeafCount, optionalLeafLimit);
        partitions.Sum(static partition => partition.IndexCount).ShouldBe(mesh.IndexCount);
        partitions.ShouldAllBe(partition => partition.IndexCount <= maxTrianglesPerPartition * 3);
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

    [Test]
    public void SplitSubMesh_SpatialPartitionPreservesMaterialAndTriangleCount()
    {
        XRMaterial material = new() { Name = "PartitionedMaterial" };
        XRMesh mesh = new(
            [
                new Vertex(new Vector3(0.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(1.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(0.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(10.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(11.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(10.0f, 1.0f, 0.0f)),
                new Vertex(new Vector3(20.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(21.0f, 0.0f, 0.0f)),
                new Vertex(new Vector3(20.0f, 1.0f, 0.0f)),
            ],
            new List<ushort> { 0, 1, 2, 3, 4, 5, 6, 7, 8 });
        SubMesh subMesh = new(new SubMeshLOD(material, mesh, 24.0f))
        {
            Name = "WideSubMesh",
        };

        IReadOnlyList<SubMesh> partitions = ModelImportMeshIslandSplitter.SplitSubMesh(
            subMesh,
            separateMeshIslands: false,
            spatialPartitionMaxTriangles: 2);

        partitions.Count.ShouldBeInRange(2, 3);
        partitions.Sum(static partition => partition.LODs.Min!.Mesh!.IndexCount).ShouldBe(mesh.IndexCount);
        partitions.ShouldAllBe(static partition => partition.LODs.Min!.Mesh!.IndexCount <= 6);
        partitions.ShouldAllBe(partition => partition.LODs.Min!.Material == material);
        partitions.ShouldAllBe(static partition => partition.LODs.Min!.MaxVisibleDistance == 24.0f);
    }
}
