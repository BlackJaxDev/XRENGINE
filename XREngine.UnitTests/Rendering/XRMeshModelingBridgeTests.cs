using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Editor;
using XREngine.Modeling;
using XREngine.Rendering;
using XREngine.Rendering.Modeling;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class XRMeshModelingBridgeTests
{
    [Test]
    public void RoundTrip_PreservesPositionsAndTriangleIndices()
    {
        XRMesh sourceMesh = CreateIndexedQuadMesh();
        int[]? sourceIndices = sourceMesh.GetIndices(EPrimitiveType.Triangles);
        sourceIndices.ShouldNotBeNull();

        ModelingMeshDocument document = XRMeshModelingImporter.Import(sourceMesh);
        XRMesh roundTrippedMesh = XRMeshModelingExporter.Export(document);

        roundTrippedMesh.VertexCount.ShouldBe(sourceMesh.VertexCount);
        for (uint i = 0; i < (uint)sourceMesh.VertexCount; i++)
            roundTrippedMesh.GetPosition(i).ShouldBe(sourceMesh.GetPosition(i));

        int[]? roundTripIndices = roundTrippedMesh.GetIndices(EPrimitiveType.Triangles);
        roundTripIndices.ShouldNotBeNull();
        roundTripIndices.ShouldBe(sourceIndices);
    }

    [Test]
    public void Export_RejectsInvalidIndicesWithValidationFailure()
    {
        ModelingMeshDocument document = new()
        {
            Positions =
            [
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)
            ],
            TriangleIndices = [0, 1, 9],
            Metadata = new ModelingMeshMetadata
            {
                SourcePrimitiveType = ModelingPrimitiveType.Triangles
            }
        };

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => XRMeshModelingExporter.Export(document));
        ex.Message.ShouldContain("validation");
        ex.Message.ShouldContain("triangle_index_out_of_range");
    }

    [Test]
    public void Import_HandlesMeshesWithNoNormalsUvColors()
    {
        List<Vertex> vertices =
        [
            new Vertex(new Vector3(0f, 0f, 0f)),
            new Vertex(new Vector3(1f, 0f, 0f)),
            new Vertex(new Vector3(0f, 1f, 0f))
        ];
        List<ushort> indices = [0, 1, 2];
        XRMesh mesh = new(vertices, indices);

        ModelingMeshDocument document = XRMeshModelingImporter.Import(mesh);

        document.Normals.ShouldBeNull();
        document.Tangents.ShouldBeNull();
        document.TexCoordChannels.ShouldBeNull();
        document.ColorChannels.ShouldBeNull();

        document.Metadata.SourceColorChannelCount.ShouldBe(0);
        document.Metadata.SourceTexCoordChannelCount.ShouldBe(0);
    }

    [Test]
    public void SaveContract_InvalidatesIndexCacheAndUpdatesBounds()
    {
        XRMesh mesh = CreateIndexedQuadMesh();
        mesh.GenerateBVH();
        mesh.HasAccelerationCache().ShouldBeTrue();

        _ = mesh.GetIndexBuffer(EPrimitiveType.Triangles, out _);
        mesh.HasCachedIndexBuffer(EPrimitiveType.Triangles).ShouldBeTrue();

        mesh.SetPosition(0, new Vector3(10f, 0f, 0f));
        mesh.Bounds.Max.X.ShouldBeLessThan(10f);

        XRMeshModelingExporter.ApplyExportContract(mesh);

        mesh.HasCachedIndexBuffer(EPrimitiveType.Triangles).ShouldBeFalse();
        mesh.HasAccelerationCache().ShouldBeFalse();
        mesh.Bounds.Max.X.ShouldBeGreaterThanOrEqualTo(10f);
    }

    [Test]
    public void RoundTrip_PreservesNormalsTangentsUvAndColors()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithAttributes();

        ModelingMeshDocument document = XRMeshModelingImporter.Import(sourceMesh);
        document.Normals.ShouldNotBeNull();
        document.Tangents.ShouldNotBeNull();
        document.TexCoordChannels.ShouldNotBeNull();
        document.ColorChannels.ShouldNotBeNull();

        XRMesh roundTrippedMesh = XRMeshModelingExporter.Export(document);
        roundTrippedMesh.HasNormals.ShouldBeTrue();
        roundTrippedMesh.HasTangents.ShouldBeTrue();
        roundTrippedMesh.HasTexCoords.ShouldBeTrue();
        roundTrippedMesh.HasColors.ShouldBeTrue();

        for (uint i = 0; i < (uint)sourceMesh.VertexCount; i++)
        {
            roundTrippedMesh.GetNormal(i).ShouldBe(sourceMesh.GetNormal(i));
            roundTrippedMesh.GetTangent(i).ShouldBe(sourceMesh.GetTangent(i));
            roundTrippedMesh.GetTexCoord(i, 0).ShouldBe(sourceMesh.GetTexCoord(i, 0));
            roundTrippedMesh.GetColor(i, 0).ShouldBe(sourceMesh.GetColor(i, 0));
        }
    }

    [Test]
    public void Export_PreserveDocumentOrder_IsDeterministicAcrossRepeatedRuns()
    {
        ModelingMeshDocument document = CreateScrambledQuadDocumentWithAttributes();
        XRMeshModelingExportOptions options = new()
        {
            OrderingPolicy = XRMeshModelingExportOrderingPolicy.PreserveDocumentOrder
        };

        MeshSnapshot baseline = CaptureMeshSnapshot(XRMeshModelingExporter.Export(document, options));
        for (int i = 0; i < 4; i++)
        {
            MeshSnapshot rerun = CaptureMeshSnapshot(XRMeshModelingExporter.Export(document, options));
            AssertMeshSnapshotsEqual(baseline, rerun);
        }
    }

    [Test]
    public void Export_CanonicalizedOrdering_IsDeterministicAcrossRepeatedRuns()
    {
        ModelingMeshDocument document = CreateScrambledQuadDocumentWithAttributes();
        XRMeshModelingExportOptions options = new()
        {
            OrderingPolicy = XRMeshModelingExportOrderingPolicy.Canonicalized
        };

        MeshSnapshot baseline = CaptureMeshSnapshot(XRMeshModelingExporter.Export(document, options));
        baseline.Positions.ShouldBe(
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f)
        ]);
        baseline.TriangleIndices.ShouldBe([0, 1, 3, 0, 3, 2]);

        for (int i = 0; i < 4; i++)
        {
            MeshSnapshot rerun = CaptureMeshSnapshot(XRMeshModelingExporter.Export(document, options));
            AssertMeshSnapshotsEqual(baseline, rerun);
        }
    }

    [Test]
    public void MeshEditingPawn_SaveToXRMesh_PreservesChannelsAfterPositionEdit()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithAttributes();
        SceneNode node = new("Mesh Editing Pawn Test Node");
        MeshEditingPawnComponent pawn = node.AddComponent<MeshEditingPawnComponent>()
            ?? throw new InvalidOperationException("Failed to create MeshEditingPawnComponent.");

        pawn.LoadFromXRMesh(sourceMesh);
        pawn.SetSelectionMode(PrimitiveSelectionMode.Vertex);
        pawn.SelectSingle(0);
        pawn.TransformSelection(Matrix4x4.CreateTranslation(0.25f, 0f, 0f));

        XRMesh savedMesh = pawn.SaveToXRMesh();
        savedMesh.VertexCount.ShouldBe(sourceMesh.VertexCount);
        savedMesh.GetPosition(0).ShouldNotBe(sourceMesh.GetPosition(0));

        for (uint i = 0; i < (uint)sourceMesh.VertexCount; i++)
        {
            savedMesh.GetNormal(i).ShouldBe(sourceMesh.GetNormal(i));
            savedMesh.GetTangent(i).ShouldBe(sourceMesh.GetTangent(i));
            savedMesh.GetTexCoord(i, 0).ShouldBe(sourceMesh.GetTexCoord(i, 0));
            savedMesh.GetColor(i, 0).ShouldBe(sourceMesh.GetColor(i, 0));
        }
    }

    private static XRMesh CreateIndexedQuadMesh()
    {
        List<Vertex> vertices =
        [
            new Vertex(new Vector3(0f, 0f, 0f)),
            new Vertex(new Vector3(1f, 0f, 0f)),
            new Vertex(new Vector3(1f, 1f, 0f)),
            new Vertex(new Vector3(0f, 1f, 0f))
        ];
        List<ushort> indices = [0, 1, 2, 0, 2, 3];

        return new XRMesh(vertices, indices);
    }

    private static XRMesh CreateIndexedQuadMeshWithAttributes()
    {
        Vector3 normal = new(0f, 0f, 1f);
        Vector3 tangent = new(1f, 0f, 0f);

        List<Vertex> vertices =
        [
            new Vertex(new Vector3(0f, 0f, 0f), normal, new Vector2(0f, 0f), new Vector4(1f, 0f, 0f, 1f)) { Tangent = tangent },
            new Vertex(new Vector3(1f, 0f, 0f), normal, new Vector2(1f, 0f), new Vector4(0f, 1f, 0f, 1f)) { Tangent = tangent },
            new Vertex(new Vector3(1f, 1f, 0f), normal, new Vector2(1f, 1f), new Vector4(0f, 0f, 1f, 1f)) { Tangent = tangent },
            new Vertex(new Vector3(0f, 1f, 0f), normal, new Vector2(0f, 1f), new Vector4(1f, 1f, 1f, 1f)) { Tangent = tangent }
        ];

        List<ushort> indices = [0, 1, 2, 0, 2, 3];
        return new XRMesh(vertices, indices);
    }

    private static ModelingMeshDocument CreateScrambledQuadDocumentWithAttributes()
    {
        return new ModelingMeshDocument
        {
            Positions =
            [
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)
            ],
            TriangleIndices = [0, 2, 1, 0, 1, 3],
            Normals =
            [
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f)
            ],
            Tangents =
            [
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 0f)
            ],
            TexCoordChannels =
            [
                [
                    new Vector2(1f, 1f),
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(0f, 1f)
                ]
            ],
            ColorChannels =
            [
                [
                    new Vector4(1f, 1f, 1f, 1f),
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(0f, 1f, 0f, 1f),
                    new Vector4(0f, 0f, 1f, 1f)
                ]
            ],
            Metadata = new ModelingMeshMetadata
            {
                SourcePrimitiveType = ModelingPrimitiveType.Triangles
            }
        };
    }

    private static MeshSnapshot CaptureMeshSnapshot(XRMesh mesh)
    {
        Vector3[] positions = new Vector3[mesh.VertexCount];
        Vector3[] normals = new Vector3[mesh.VertexCount];
        Vector3[] tangents = new Vector3[mesh.VertexCount];
        Vector2[] texCoords0 = new Vector2[mesh.VertexCount];
        Vector4[] colors0 = new Vector4[mesh.VertexCount];

        for (uint i = 0; i < (uint)mesh.VertexCount; i++)
        {
            positions[i] = mesh.GetPosition(i);
            normals[i] = mesh.GetNormal(i);
            tangents[i] = mesh.GetTangent(i);
            texCoords0[i] = mesh.GetTexCoord(i, 0);
            colors0[i] = mesh.GetColor(i, 0);
        }

        int[]? triangleIndices = mesh.GetIndices(EPrimitiveType.Triangles);
        triangleIndices.ShouldNotBeNull();

        return new MeshSnapshot
        {
            Positions = positions,
            TriangleIndices = triangleIndices,
            Normals = normals,
            Tangents = tangents,
            TexCoords0 = texCoords0,
            Colors0 = colors0
        };
    }

    private static void AssertMeshSnapshotsEqual(MeshSnapshot expected, MeshSnapshot actual)
    {
        actual.Positions.ShouldBe(expected.Positions);
        actual.TriangleIndices.ShouldBe(expected.TriangleIndices);
        actual.Normals.ShouldBe(expected.Normals);
        actual.Tangents.ShouldBe(expected.Tangents);
        actual.TexCoords0.ShouldBe(expected.TexCoords0);
        actual.Colors0.ShouldBe(expected.Colors0);
    }

    private sealed class MeshSnapshot
    {
        public required Vector3[] Positions { get; init; }
        public required int[] TriangleIndices { get; init; }
        public required Vector3[] Normals { get; init; }
        public required Vector3[] Tangents { get; init; }
        public required Vector2[] TexCoords0 { get; init; }
        public required Vector4[] Colors0 { get; init; }
    }
}
