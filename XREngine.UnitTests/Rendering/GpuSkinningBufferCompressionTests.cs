using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class GpuSkinningBufferCompressionTests
{
    [Test]
    public void RebuildSkinningBuffers_PacksCore4x8AndSpillDeterministically()
    {
        Transform[] bones = CreateBones(8);
        XRMesh mesh = CreateWeightedMesh(bones, new Dictionary<int, float>
        {
            [2] = 0.4f,
            [1] = 0.2f,
            [0] = 0.2f,
            [4] = 0.1f,
            [3] = 0.1f,
        });

        mesh.RebuildSkinningBuffersFromVertices();

        mesh.SkinningInfluenceEncoding.ShouldBe(SkinningInfluenceEncoding.Core4Spill);
        mesh.SkinningCoreIndexFormat.ShouldBe(SkinningCoreIndexFormat.Core4x8);
        mesh.HasSpillInfluences.ShouldBeTrue();
        mesh.MaxSpillInfluenceCount.ShouldBe(1);
        mesh.MaxWeightCount.ShouldBe(5);

        DecodedVertex decoded = DecodeVertex(mesh, 0);
        decoded.CoreIndices.ShouldBe([3, 1, 2, 4]);
        decoded.CoreWeights.ShouldBe([101, 51, 51, 26]);
        decoded.SpillIndices.ShouldBe([(ushort)5]);
        decoded.SpillWeights.ShouldBe([26]);
    }

    [Test]
    public void RebuildSkinningBuffers_DropsTinyTailInsteadOfWritingZeroWeightSpill()
    {
        Transform[] bones = CreateBones(5);
        XRMesh mesh = CreateWeightedMesh(bones, new Dictionary<int, float>
        {
            [0] = 0.4f,
            [1] = 0.3f,
            [2] = 0.2f,
            [3] = 0.099f,
            [4] = 0.001f,
        });

        mesh.RebuildSkinningBuffersFromVertices();

        mesh.SkinningInfluenceEncoding.ShouldBe(SkinningInfluenceEncoding.Core4NoSpill);
        mesh.HasSpillInfluences.ShouldBeFalse();
        mesh.MaxSpillInfluenceCount.ShouldBe(0);
        mesh.BoneInfluenceSpillHeaders.ShouldBeNull();
        mesh.BoneInfluenceSpillEntries.ShouldBeNull();

        DecodedVertex decoded = DecodeVertex(mesh, 0);
        decoded.CoreIndices.ShouldBe([1, 2, 3, 4]);
        decoded.CoreWeights.ShouldBe([102, 77, 51, 25]);
        decoded.SpillIndices.ShouldBeEmpty();
        decoded.CoreWeights.Sum(x => x).ShouldBe(255);
    }

    [Test]
    public void RebuildSkinningBuffers_UsesCore4x16Above255UtilizedBones()
    {
        Transform[] bones = CreateBones(256);
        XRMesh mesh = CreateWeightedMesh(bones, new Dictionary<int, float>
        {
            [255] = 1.0f,
        });

        mesh.RebuildSkinningBuffersFromVertices();

        mesh.SkinningInfluenceEncoding.ShouldBe(SkinningInfluenceEncoding.Core4NoSpill);
        mesh.SkinningCoreIndexFormat.ShouldBe(SkinningCoreIndexFormat.Core4x16);
        DecodeVertex(mesh, 0).CoreIndices.ShouldBe([256, 0, 0, 0]);
    }

    [Test]
    public void RebuildSkinningBuffers_RefusesSkeletonsAbove16BitPalette()
    {
        Transform[] bones = CreateBones(ushort.MaxValue + 1);
        XRMesh mesh = CreateWeightedMesh(bones, new Dictionary<int, float>
        {
            [0] = 1.0f,
        });

        Should.Throw<NotSupportedException>(() => mesh.RebuildSkinningBuffersFromVertices())
            .Message.ShouldContain("at most 65535 utilized bones");
    }

    [Test]
    public void RebuildSkinningBuffers_ZeroInfluenceVertexUsesSentinels()
    {
        Transform[] bones = CreateBones(1);
        XRMesh mesh = CreateWeightedMesh(bones, []);

        mesh.RebuildSkinningBuffersFromVertices();

        mesh.SkinningInfluenceEncoding.ShouldBe(SkinningInfluenceEncoding.Core4NoSpill);
        mesh.HasSpillInfluences.ShouldBeFalse();
        DecodedVertex decoded = DecodeVertex(mesh, 0);
        decoded.CoreIndices.ShouldBe([0, 0, 0, 0]);
        decoded.CoreWeights.ShouldBe([0, 0, 0, 0]);
        decoded.SpillIndices.ShouldBeEmpty();
        decoded.SpillWeights.ShouldBeEmpty();
    }

    [Test]
    public void EnsureComputeSkinningBuffers_RebuildsCore4ForWeightedSourceMesh()
    {
        Transform[] bones = CreateBones(2);
        XRMesh mesh = CreateWeightedMesh(bones, new Dictionary<int, float>
        {
            [0] = 0.75f,
            [1] = 0.25f,
        });

        mesh.SupportsComputeSkinning.ShouldBeFalse();

        mesh.EnsureComputeSkinningBuffers();

        mesh.SupportsComputeSkinning.ShouldBeTrue();
        mesh.SkinningInfluenceEncoding.ShouldBe(SkinningInfluenceEncoding.Core4NoSpill);
        DecodedVertex decoded = DecodeVertex(mesh, 0);
        decoded.CoreIndices.ShouldBe([1, 2, 0, 0]);
        decoded.CoreWeights.Sum(x => x).ShouldBe(255);
    }

    [Test]
    public void RuntimeCookedBinarySerializer_RejectsSkinnedMeshWithoutCanonicalCore4Buffers()
    {
        Transform[] bones = CreateBones(1);
        XRMesh mesh = new()
        {
            Name = "InvalidSkinnedCook",
            UtilizedBones = CreateUtilizedBones(bones),
        };

        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
            RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => RuntimeCookedBinarySerializer.Serialize(mesh)));
        ex.Message.ShouldContain("Core4 compute-skinning runtime format");
        ex.Message.ShouldContain("Recook or reimport");
    }

    [Test]
    public void SkinPaletteMatrix_StoresRowsForShaderDotLayout()
    {
        Matrix4x4 matrix =
            Matrix4x4.CreateScale(2.0f, 3.0f, 4.0f) *
            Matrix4x4.CreateTranslation(5.0f, 6.0f, 7.0f);
        Vector3 position = new(1.0f, 2.0f, 3.0f);
        Vector3 expected = Vector3.Transform(position, matrix);

        SkinPaletteMatrix palette = SkinPaletteMatrix.FromRowVectorMatrix(matrix);
        Vector4 p = new(position, 1.0f);
        Vector3 actual = new(
            Vector4.Dot(palette.Row0, p),
            Vector4.Dot(palette.Row1, p),
            Vector4.Dot(palette.Row2, p));

        actual.X.ShouldBe(expected.X, 1e-5f);
        actual.Y.ShouldBe(expected.Y, 1e-5f);
        actual.Z.ShouldBe(expected.Z, 1e-5f);
    }

    private static XRMesh CreateWeightedMesh(Transform[] bones, Dictionary<int, float> weights)
    {
        Vector3 normal = Vector3.UnitZ;
        List<Vertex> vertices =
        [
            new Vertex(Vector3.Zero, null, normal),
            new Vertex(Vector3.UnitX, null, normal),
            new Vertex(Vector3.UnitY, null, normal),
        ];

        if (weights.Count > 0)
        {
            var vertexWeights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(weights.Count);
            foreach (var pair in weights)
                vertexWeights.Add(bones[pair.Key], (pair.Value, Matrix4x4.Identity));

            vertices[0].Weights = vertexWeights;
        }

        XRMesh mesh = new(vertices, new List<ushort> { 0, 1, 2 })
        {
            UtilizedBones = CreateUtilizedBones(bones),
        };
        return mesh;
    }

    private static Transform[] CreateBones(int count)
    {
        Transform[] bones = new Transform[count];
        for (int i = 0; i < count; i++)
            bones[i] = new Transform();
        return bones;
    }

    private static (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] CreateUtilizedBones(Transform[] bones)
    {
        var utilized = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            utilized[i] = (bones[i], Matrix4x4.Identity);
        return utilized;
    }

    private static DecodedVertex DecodeVertex(XRMesh mesh, int vertexIndex)
    {
        mesh.BoneInfluenceCoreIndices.ShouldNotBeNull();
        mesh.BoneInfluenceCoreWeights.ShouldNotBeNull();

        ushort[] coreIndices = new ushort[4];
        byte[] indexBytes = mesh.BoneInfluenceCoreIndices.GetRawBytes();
        if (mesh.SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x8)
        {
            int baseByte = vertexIndex * 4;
            for (int i = 0; i < 4; i++)
                coreIndices[i] = indexBytes[baseByte + i];
        }
        else
        {
            int baseByte = vertexIndex * 8;
            for (int i = 0; i < 4; i++)
                coreIndices[i] = BitConverter.ToUInt16(indexBytes, baseByte + i * sizeof(ushort));
        }

        byte[] weightBytes = mesh.BoneInfluenceCoreWeights.GetRawBytes();
        byte[] coreWeights = new byte[4];
        Array.Copy(weightBytes, vertexIndex * 4, coreWeights, 0, 4);

        uint spillHeader = mesh.BoneInfluenceSpillHeaders?.GetDataRawAtIndex<uint>((uint)vertexIndex) ?? 0u;
        uint spillOffset = spillHeader & 0x00FF_FFFFu;
        uint spillCount = spillHeader >> 24;
        ushort[] spillIndices = new ushort[spillCount];
        byte[] spillWeights = new byte[spillCount];
        for (uint i = 0; i < spillCount; i++)
        {
            mesh.BoneInfluenceSpillEntries.ShouldNotBeNull();
            uint entry = mesh.BoneInfluenceSpillEntries.GetDataRawAtIndex<uint>(spillOffset + i);
            spillIndices[i] = (ushort)(entry & 0xFFFFu);
            spillWeights[i] = (byte)((entry >> 16) & 0xFFu);
        }

        return new DecodedVertex(coreIndices, coreWeights, spillIndices, spillWeights);
    }

    private readonly record struct DecodedVertex(
        ushort[] CoreIndices,
        byte[] CoreWeights,
        ushort[] SpillIndices,
        byte[] SpillWeights);
}
