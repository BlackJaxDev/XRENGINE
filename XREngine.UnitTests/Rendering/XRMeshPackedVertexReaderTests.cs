using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Round trips of the packed mesh buffers through the CPU readers that replace
/// the per-vertex object model: skinning influences, blendshape deltas, the
/// canonical Advanced vertex codec and the scoped vertex view.
/// </summary>
[TestFixture]
public sealed class XRMeshPackedVertexReaderTests
{
    private const float QuantizedWeightTolerance = 1.0f / 255.0f + 1.0e-4f;

    [Test]
    public void SkinningInfluenceReader_DecodesCoreInfluencesInPaletteOrder()
    {
        Transform boneA = new();
        Transform boneB = new();
        List<Vertex> vertices =
        [
            WeightedVertex(new Vector3(0, 0, 0), (boneA, 1.0f)),
            WeightedVertex(new Vector3(1, 0, 0), (boneA, 0.25f), (boneB, 0.75f)),
            WeightedVertex(new Vector3(0, 1, 0), (boneB, 1.0f)),
        ];
        XRMesh mesh = new(vertices, [0, 1, 2]);
        mesh.RebuildSkinningBuffersFromVertices(vertices);

        XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader reader).ShouldBeTrue();
        reader.BoneCount.ShouldBe(mesh.UtilizedBones.Length);
        int[] bones = new int[reader.MaxInfluenceCount];
        float[] weights = new float[reader.MaxInfluenceCount];

        reader.ReadInfluences(1, bones, weights).ShouldBe(2);
        Dictionary<TransformBase, float> decoded = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < 2; i++)
            decoded[mesh.UtilizedBones[bones[i]].tfm] = weights[i];
        decoded[boneA].ShouldBe(0.25f, QuantizedWeightTolerance);
        decoded[boneB].ShouldBe(0.75f, QuantizedWeightTolerance);

        reader.ReadInfluences(0, bones, weights).ShouldBe(1);
        mesh.UtilizedBones[bones[0]].tfm.ShouldBeSameAs(boneA);
        weights[0].ShouldBe(1.0f, QuantizedWeightTolerance);
    }

    [Test]
    public void SkinningInfluenceReader_DecodesSpillInfluences()
    {
        Transform[] bones = [new(), new(), new(), new(), new(), new()];
        (TransformBase, float)[] influences = new (TransformBase, float)[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            influences[i] = (bones[i], 1.0f / bones.Length);
        List<Vertex> vertices =
        [
            WeightedVertex(Vector3.Zero, influences),
            WeightedVertex(Vector3.UnitX, (bones[0], 1.0f)),
            WeightedVertex(Vector3.UnitY, (bones[1], 1.0f)),
        ];
        XRMesh mesh = new(vertices, [0, 1, 2]);
        mesh.RebuildSkinningBuffersFromVertices(vertices);
        mesh.HasSpillInfluences.ShouldBeTrue();

        XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader reader).ShouldBeTrue();
        int[] boneIndices = new int[reader.MaxInfluenceCount];
        float[] weights = new float[reader.MaxInfluenceCount];

        int count = reader.ReadInfluences(0, boneIndices, weights);

        count.ShouldBe(bones.Length);
        float sum = 0.0f;
        for (int i = 0; i < count; i++)
            sum += weights[i];
        sum.ShouldBe(1.0f, QuantizedWeightTolerance);
    }

    [Test]
    public void BlendshapeDeltaTable_GroupsFullPrecisionDeltasByShapeInVertexOrder()
    {
        Vector3 smallDelta = new(0.000123f, -0.5f, 2.25f);
        List<Vertex> vertices =
        [
            BlendshapeVertex(new Vector3(0, 0, 0), ("Smile", new Vector3(0, 0, 0) + smallDelta)),
            BlendshapeVertex(new Vector3(1, 0, 0)),
            BlendshapeVertex(new Vector3(0, 1, 0), ("Blink", new Vector3(0, 1.5f, 0)), ("Smile", new Vector3(0, 1, 1))),
        ];
        XRMesh mesh = new(vertices, [0, 1, 2]) { BlendshapeNames = ["Smile", "Blink"] };
        mesh.RebuildBlendshapeBuffersFromVertices(vertices);

        XRMeshBlendshapeDeltaTable.TryCreate(mesh, out XRMeshBlendshapeDeltaTable table).ShouldBeTrue();

        table.ShapeCount.ShouldBe(2);
        table.GetShapeRecordCount(0).ShouldBe(2);
        int smile = table.GetShapeRecordOffset(0);
        table.RecordVertices[smile].ShouldBe(0);
        table.PositionDeltas[smile].ShouldBe(smallDelta);
        table.RecordVertices[smile + 1].ShouldBe(2);
        table.PositionDeltas[smile + 1].ShouldBe(new Vector3(0, 0, 1));
        table.GetShapeRecordCount(1).ShouldBe(1);
        int blink = table.GetShapeRecordOffset(1);
        table.RecordVertices[blink].ShouldBe(2);
        table.PositionDeltas[blink].ShouldBe(new Vector3(0, 0.5f, 0));
        (table.Flags[blink] & XRMeshBlendshapeDeltaTable.PositionFlag).ShouldNotBe((byte)0);
    }

    [Test]
    public void PackedVertexCodec_PacksSameRecordFromBuffersAsFromVertex()
    {
        List<Vertex> vertices =
        [
            AttributedVertex(new Vector3(1, 2, 3), Vector3.UnitZ, new Vector3(1, 0, 0), -1.0f, new Vector2(0.25f, 0.75f), new Vector4(0.5f, 0.25f, 1, 1)),
            AttributedVertex(new Vector3(-1, 0, 4), Vector3.UnitY, new Vector3(0, 0, 1), 1.0f, new Vector2(1, 0), new Vector4(1, 1, 1, 0.5f)),
            AttributedVertex(new Vector3(0, 5, -2), Vector3.UnitX, new Vector3(0, 1, 0), 1.0f, new Vector2(0.5f, 0.5f), new Vector4(0, 0, 0, 1)),
        ];
        XRMesh mesh = new(vertices, [0, 1, 2]);

        AdvancedPackedVertexCodec.HasReadableAttributes(mesh).ShouldBeTrue();
        for (int i = 0; i < vertices.Count; i++)
            AdvancedPackedVertexCodec.Pack(mesh, (uint)i, (uint)i)
                .ShouldBe(AdvancedPackedVertexCodec.Pack(vertices[i], (uint)i));
    }

    [Test]
    public void VertexView_MaterializesAttributesWeightsAndBlendshapes()
    {
        Transform bone = new();
        List<Vertex> vertices =
        [
            AttributedVertex(new Vector3(1, 2, 3), Vector3.UnitZ, Vector3.UnitX, 1.0f, new Vector2(0.25f, 0.75f), Vector4.One),
            AttributedVertex(new Vector3(4, 5, 6), Vector3.UnitY, Vector3.UnitX, -1.0f, new Vector2(0.5f, 0.5f), Vector4.One),
            AttributedVertex(new Vector3(7, 8, 9), Vector3.UnitX, Vector3.UnitY, 1.0f, new Vector2(1, 1), Vector4.One),
        ];
        vertices[1].Weights = new() { [bone] = (1.0f, Matrix4x4.Identity) };
        vertices[2].Blendshapes = [("Shape", new VertexData { Position = new Vector3(7, 9, 9), Normal = Vector3.UnitX })];
        XRMesh mesh = new(vertices, [0, 1, 2]) { BlendshapeNames = ["Shape"] };
        mesh.RebuildSkinningBuffersFromVertices(vertices);
        mesh.RebuildBlendshapeBuffersFromVertices(vertices);

        using XRMeshVertexView view = XRMeshVertexView.Open(mesh);

        view.Count.ShouldBe(3);
        view.Vertices[0].Position.ShouldBe(new Vector3(1, 2, 3));
        view.Vertices[0].Normal.ShouldBe(Vector3.UnitZ);
        view.Vertices[1].BitangentSign.ShouldBe(-1.0f);
        view.Vertices[1].TextureCoordinateSets![0].ShouldBe(new Vector2(0.5f, 0.5f));
        view.Vertices[0].Weights.ShouldBeNull();
        view.Vertices[1].Weights.ShouldNotBeNull();
        view.Vertices[1].Weights![bone].weight.ShouldBe(1.0f, QuantizedWeightTolerance);
        view.Vertices[2].Blendshapes.ShouldNotBeNull();
        view.Vertices[2].Blendshapes![0].name.ShouldBe("Shape");
        view.Vertices[2].Blendshapes![0].data.Position.ShouldBe(new Vector3(7, 9, 9));
    }

    [Test]
    public void VertexView_MaterializesRequestedRangeOnly()
    {
        List<Vertex> vertices =
        [
            new(new Vector3(0, 0, 0)),
            new(new Vector3(1, 0, 0)),
            new(new Vector3(0, 1, 0)),
            new(new Vector3(0, 0, 1)),
        ];
        XRMesh mesh = new(vertices, [0, 1, 2, 1, 2, 3]);

        using XRMeshVertexView view = XRMeshVertexView.Open(mesh, EXRMeshVertexViewContent.Positions, firstVertex: 2, count: 2);

        view.FirstVertex.ShouldBe(2);
        view.Count.ShouldBe(2);
        view.Vertices[0].Position.ShouldBe(new Vector3(0, 1, 0));
        view.Vertices[1].Position.ShouldBe(new Vector3(0, 0, 1));
    }

    private static Vertex WeightedVertex(Vector3 position, params (TransformBase bone, float weight)[] influences)
    {
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weights = new(ReferenceEqualityComparer.Instance);
        foreach ((TransformBase bone, float weight) in influences)
            weights[bone] = (weight, Matrix4x4.Identity);
        return new Vertex(position) { Weights = weights };
    }

    private static Vertex BlendshapeVertex(Vector3 position, params (string name, Vector3 target)[] shapes)
    {
        Vertex vertex = new(position);
        if (shapes.Length > 0)
        {
            vertex.Blendshapes = [];
            foreach ((string name, Vector3 target) in shapes)
                vertex.Blendshapes.Add((name, new VertexData { Position = target }));
        }
        return vertex;
    }

    private static Vertex AttributedVertex(
        Vector3 position,
        Vector3 normal,
        Vector3 tangent,
        float bitangentSign,
        Vector2 texCoord,
        Vector4 color)
        => new(position)
        {
            Normal = normal,
            Tangent = tangent,
            BitangentSign = bitangentSign,
            TextureCoordinateSets = [texCoord],
            ColorSets = [color],
        };
}
