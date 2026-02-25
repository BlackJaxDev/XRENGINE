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
using XREngine.Scene.Transforms;

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

    [Test]
    public void MeshEditingPawn_TopologyOperations_ProduceValidTopologyAndReprojectChannelsOnSave()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithSkinningAndBlendshapes();
        SceneNode node = new("Mesh Editing Pawn Topology Operations Node");
        MeshEditingPawnComponent pawn = node.AddComponent<MeshEditingPawnComponent>()
            ?? throw new InvalidOperationException("Failed to create MeshEditingPawnComponent.");

        pawn.LoadFromXRMesh(sourceMesh);

        pawn.SetSelectionMode(PrimitiveSelectionMode.Face);
        pawn.SelectSingle(0);
        List<int> extruded = pawn.ExtrudeSelectedFaces(0.2f);
        extruded.Count.ShouldBeGreaterThan(0);

        pawn.SetSelectionMode(PrimitiveSelectionMode.Edge);
        pawn.SelectSingle(0);
        List<int> loopCut = pawn.LoopCutSelectedEdge(0.5f);
        loopCut.Count.ShouldBeGreaterThan(0);

        TopologyValidationReport topology = pawn.ValidateTopology();
        topology.HasErrors.ShouldBeFalse();

        XRMeshModelingExportOptions options = new()
        {
            SkinningBlendshapeFallbackPolicy = XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveNearestSourceVertexReproject
        };

        XRMesh saved = pawn.SaveToXRMesh(options);
        saved.VertexCount.ShouldBeGreaterThan(sourceMesh.VertexCount);
        saved.HasSkinning.ShouldBeTrue();
        saved.HasBlendshapes.ShouldBeTrue();
        pawn.LastSaveDiagnostics.Any(x => x.Code == "skinning_blendshape_channels_reprojected").ShouldBeTrue();
    }

    [Test]
    public void MeshEditingPawn_TopologyOperation_PushesUndoEntryPerOperation()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithAttributes();
        SceneNode node = new("Mesh Editing Pawn Undo Node");
        MeshEditingPawnComponent pawn = node.AddComponent<MeshEditingPawnComponent>()
            ?? throw new InvalidOperationException("Failed to create MeshEditingPawnComponent.");

        Undo.ClearHistory();
        pawn.LoadFromXRMesh(sourceMesh);

        int before = Undo.PendingUndo.Count;

        pawn.SetSelectionMode(PrimitiveSelectionMode.Edge);
        pawn.SelectSingle(0);
        int splitVertex = pawn.SplitSelectedEdge(0.5f);
        splitVertex.ShouldBeGreaterThanOrEqualTo(0);

        int afterSplit = Undo.PendingUndo.Count;
        afterSplit.ShouldBeGreaterThan(before);

        pawn.SetSelectionMode(PrimitiveSelectionMode.Edge);
        pawn.SelectSingle(0);
        bool collapsed = pawn.CollapseSelectedEdge();
        collapsed.ShouldBeTrue();

        Undo.PendingUndo.Count.ShouldBeGreaterThan(afterSplit);
    }

    [Test]
    public void RoundTrip_PreservesSkinningAndBlendshapeChannels()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithSkinningAndBlendshapes();

        ModelingMeshDocument document = XRMeshModelingImporter.Import(sourceMesh);
        document.SkinBones.ShouldNotBeNull();
        document.SkinWeights.ShouldNotBeNull();
        document.BlendshapeChannels.ShouldNotBeNull();

        XRMesh roundTrippedMesh = XRMeshModelingExporter.Export(document);
        roundTrippedMesh.UtilizedBones.Length.ShouldBe(sourceMesh.UtilizedBones.Length);
        roundTrippedMesh.BlendshapeNames.ShouldBe(sourceMesh.BlendshapeNames);

        for (int i = 0; i < sourceMesh.VertexCount; i++)
        {
            Vertex sourceVertex = sourceMesh.Vertices[i];
            Vertex roundTripVertex = roundTrippedMesh.Vertices[i];

            (sourceVertex.Weights?.Count ?? 0).ShouldBe(roundTripVertex.Weights?.Count ?? 0);
            if (sourceVertex.Weights is { Count: > 0 } sourceWeights)
            {
                sourceWeights.Values.Select(x => x.weight).OrderBy(x => x).ToArray()
                    .ShouldBe(roundTripVertex.Weights!.Values.Select(x => x.weight).OrderBy(x => x).ToArray());
            }

            sourceVertex.Blendshapes?.Select(x => x.name).ToArray().ShouldBe(roundTripVertex.Blendshapes?.Select(x => x.name).ToArray());
            if (sourceVertex.Blendshapes is { Count: > 0 })
            {
                for (int blendshapeIndex = 0; blendshapeIndex < sourceVertex.Blendshapes.Count; blendshapeIndex++)
                {
                    sourceVertex.Blendshapes[blendshapeIndex].data.Position
                        .ShouldBe(roundTripVertex.Blendshapes![blendshapeIndex].data.Position);
                }
            }
        }
    }

    [Test]
    public void Export_RejectsInvalidSkinWeightBoneIndex()
    {
        ModelingMeshDocument document = CreateScrambledQuadDocumentWithAttributes();
        document.SkinBones =
        [
            new ModelingSkinBone
            {
                Name = "BoneA",
                InverseBindMatrix = Matrix4x4.Identity
            }
        ];
        document.SkinWeights =
        [
            [new ModelingSkinWeight(0, 1f)],
            [new ModelingSkinWeight(9, 1f)],
            [new ModelingSkinWeight(0, 1f)],
            [new ModelingSkinWeight(0, 1f)]
        ];

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => XRMeshModelingExporter.Export(document));
        ex.Message.ShouldContain("skin_weight_bone_index_out_of_range");
    }

    [Test]
    public void MeshEditingPawn_SaveToXRMesh_StrictFallback_ThrowsOnTopologyChange()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithSkinningAndBlendshapes();
        SceneNode node = new("Strict Fallback Topology Change Node");
        MeshEditingPawnComponent pawn = node.AddComponent<MeshEditingPawnComponent>()
            ?? throw new InvalidOperationException("Failed to create MeshEditingPawnComponent.");

        pawn.LoadFromXRMesh(sourceMesh);
        pawn.SetSelectionMode(PrimitiveSelectionMode.Edge);
        pawn.SelectSingle(0);
        _ = pawn.InsertVertexOnSelection(new Vector3(0.5f, 0.0f, 0.0f));

        XRMeshModelingExportOptions options = new()
        {
            SkinningBlendshapeFallbackPolicy = XRMeshModelingSkinningBlendshapeFallbackPolicy.Strict
        };

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => pawn.SaveToXRMesh(options));
        ex.Message.ShouldContain("Strict skinning/blendshape fallback policy");
        pawn.LastSaveDiagnostics.Any(x => x.Code == "skinning_blendshape_strict_topology_changed").ShouldBeTrue();
    }

    [Test]
    public void MeshEditingPawn_SaveToXRMesh_PermissiveDrop_RemovesSkinningAndBlendshapesOnTopologyChange()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithSkinningAndBlendshapes();
        SceneNode node = new("Permissive Drop Topology Change Node");
        MeshEditingPawnComponent pawn = node.AddComponent<MeshEditingPawnComponent>()
            ?? throw new InvalidOperationException("Failed to create MeshEditingPawnComponent.");

        pawn.LoadFromXRMesh(sourceMesh);
        pawn.SetSelectionMode(PrimitiveSelectionMode.Edge);
        pawn.SelectSingle(0);
        _ = pawn.InsertVertexOnSelection(new Vector3(0.5f, 0.0f, 0.0f));

        XRMeshModelingExportOptions options = new()
        {
            SkinningBlendshapeFallbackPolicy = XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveDropChannels
        };

        XRMesh savedMesh = pawn.SaveToXRMesh(options);
        savedMesh.VertexCount.ShouldBeGreaterThan(sourceMesh.VertexCount);
        savedMesh.HasSkinning.ShouldBeFalse();
        savedMesh.HasBlendshapes.ShouldBeFalse();
        pawn.LastSaveDiagnostics.Any(x => x.Code == "skinning_blendshape_channels_dropped").ShouldBeTrue();
    }

    [Test]
    public void MeshEditingPawn_SaveToXRMesh_PermissiveReproject_PreservesSkinningAndBlendshapeChannelsOnTopologyChange()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithSkinningAndBlendshapes();
        SceneNode node = new("Permissive Reproject Topology Change Node");
        MeshEditingPawnComponent pawn = node.AddComponent<MeshEditingPawnComponent>()
            ?? throw new InvalidOperationException("Failed to create MeshEditingPawnComponent.");

        pawn.LoadFromXRMesh(sourceMesh);
        pawn.SetSelectionMode(PrimitiveSelectionMode.Edge);
        pawn.SelectSingle(0);
        _ = pawn.InsertVertexOnSelection(new Vector3(0.5f, 0.0f, 0.0f));

        XRMeshModelingExportOptions options = new()
        {
            SkinningBlendshapeFallbackPolicy = XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveNearestSourceVertexReproject
        };

        XRMesh savedMesh = pawn.SaveToXRMesh(options);
        savedMesh.VertexCount.ShouldBeGreaterThan(sourceMesh.VertexCount);
        savedMesh.HasSkinning.ShouldBeTrue();
        savedMesh.HasBlendshapes.ShouldBeTrue();
        savedMesh.BlendshapeNames.ShouldBe(sourceMesh.BlendshapeNames);
        pawn.LastSaveDiagnostics.Any(x => x.Code == "skinning_blendshape_channels_reprojected").ShouldBeTrue();
    }

    [Test]
    public void Export_SaveContract_RemainsValidWithSkinningAndBlendshapeChannels()
    {
        XRMesh sourceMesh = CreateIndexedQuadMeshWithSkinningAndBlendshapes();
        ModelingMeshDocument document = XRMeshModelingImporter.Import(sourceMesh);
        document.Positions[0] = new Vector3(8f, 0f, 0f);

        XRMesh exported = XRMeshModelingExporter.Export(document, new XRMeshModelingExportOptions
        {
            SkinningBlendshapeFallbackPolicy = XRMeshModelingSkinningBlendshapeFallbackPolicy.PermissiveNearestSourceVertexReproject
        });

        exported.HasSkinning.ShouldBeTrue();
        exported.HasBlendshapes.ShouldBeTrue();
        exported.Bounds.Max.X.ShouldBeGreaterThanOrEqualTo(8f);
        exported.HasCachedIndexBuffer(EPrimitiveType.Triangles).ShouldBeFalse();
        exported.HasAccelerationCache().ShouldBeFalse();
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

    private static XRMesh CreateIndexedQuadMeshWithSkinningAndBlendshapes()
    {
        XRMesh mesh = CreateIndexedQuadMeshWithAttributes();

        Transform boneA = new() { Name = "BoneA", InverseBindMatrix = Matrix4x4.Identity };
        Transform boneB = new() { Name = "BoneB", InverseBindMatrix = Matrix4x4.CreateTranslation(0f, 0f, -1f) };
        mesh.UtilizedBones =
        [
            (boneA, boneA.InverseBindMatrix),
            (boneB, boneB.InverseBindMatrix)
        ];

        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Vertex vertex = mesh.Vertices[i];
            vertex.Weights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>
            {
                [boneA] = (0.75f, boneA.InverseBindMatrix),
                [boneB] = (0.25f, boneB.InverseBindMatrix)
            };

            vertex.Blendshapes =
            [
                (
                    "Smile",
                    new VertexData
                    {
                        Position = vertex.Position + new Vector3(0f, 0.05f, 0f),
                        Normal = vertex.Normal,
                        Tangent = vertex.Tangent
                    }
                )
            ];
        }

        mesh.BlendshapeNames = ["Smile"];
        return mesh;
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
