using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

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

    [Test]
    public void PushBoneMatricesToGpu_UsesRenderMatricesForSkinnedBones()
    {
        SceneNode root = new("SkinnedMeshRoot");
        Transform bone = root.SetTransform<Transform>();
        bone.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);

        XRMesh mesh = CreateSingleTriangleMesh();
        mesh.UtilizedBones =
        [
            (bone, Matrix4x4.Identity)
        ];

        XRMeshRenderer renderer = new()
        {
            Mesh = mesh
        };

        renderer.EnsureSkinningBuffers().ShouldBeTrue();
        renderer.BoneMatricesBuffer.ShouldNotBeNull();

        Matrix4x4 renderMatrix = Matrix4x4.CreateTranslation(new Vector3(7.0f, 8.0f, 9.0f));
        bone.SetRenderMatrix(renderMatrix, recalcAllChildRenderMatrices: false).Wait();

        renderer.PushBoneMatricesToGPU();

        renderer.BoneMatricesBuffer!.GetDataRawAtIndex<Matrix4x4>(1u).ShouldBe(renderMatrix);
    }

    [Test]
    public void SetupMeshDeformation_PopulatesVec4InfluenceBuffers()
    {
        var targetRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh(),
            MaxMeshDeformInfluences = 4,
            OptimizeMeshDeformToVec4 = true
        };
        var deformerRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh()
        };

        targetRenderer.SetupMeshDeformation(deformerRenderer,
        [
            [new MeshDeformInfluence(0, 0.75f), new MeshDeformInfluence(1, 0.25f)],
            [new MeshDeformInfluence(2, 1.0f)],
            []
        ]);

        targetRenderer.MeshDeformVertexIndicesBuffer.ShouldNotBeNull();
        targetRenderer.MeshDeformVertexWeightsBuffer.ShouldNotBeNull();
        targetRenderer.MeshDeformIndicesBuffer.ShouldBeNull();
        targetRenderer.MeshDeformWeightsBuffer.ShouldBeNull();

        ReadIndicesVector(targetRenderer.MeshDeformVertexIndicesBuffer!, 0u).ShouldBe(new Vector4(0f, 1f, -1f, -1f));
        targetRenderer.MeshDeformVertexWeightsBuffer!.GetDataRawAtIndex<Vector4>(0u).ShouldBe(new Vector4(0.75f, 0.25f, 0f, 0f));

        ReadIndicesVector(targetRenderer.MeshDeformVertexIndicesBuffer!, 1u).ShouldBe(new Vector4(2f, -1f, -1f, -1f));
        targetRenderer.MeshDeformVertexWeightsBuffer.GetDataRawAtIndex<Vector4>(1u).ShouldBe(new Vector4(1f, 0f, 0f, 0f));

        ReadIndicesVector(targetRenderer.MeshDeformVertexIndicesBuffer!, 2u).ShouldBe(new Vector4(-1f, -1f, -1f, -1f));
        targetRenderer.MeshDeformVertexWeightsBuffer.GetDataRawAtIndex<Vector4>(2u).ShouldBe(Vector4.Zero);
    }

    [Test]
    public void SetupMeshDeformation_PopulatesSsboInfluenceBuffers()
    {
        var targetRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh(),
            MaxMeshDeformInfluences = 8,
            OptimizeMeshDeformToVec4 = false
        };
        var deformerRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh()
        };

        targetRenderer.SetupMeshDeformation(deformerRenderer,
        [
            [new MeshDeformInfluence(1, 0.6f), new MeshDeformInfluence(2, 0.4f)],
            [new MeshDeformInfluence(0, 1.0f)],
            []
        ]);

        targetRenderer.MeshDeformIndicesBuffer.ShouldNotBeNull();
        targetRenderer.MeshDeformWeightsBuffer.ShouldNotBeNull();
        targetRenderer.MeshDeformVertexOffsetBuffer.ShouldNotBeNull();
        targetRenderer.MeshDeformVertexCountBuffer.ShouldNotBeNull();
        targetRenderer.MeshDeformVertexIndicesBuffer.ShouldBeNull();
        targetRenderer.MeshDeformVertexWeightsBuffer.ShouldBeNull();

        ReadScalar(targetRenderer.MeshDeformVertexOffsetBuffer!, 0u).ShouldBe(0f);
        ReadScalar(targetRenderer.MeshDeformVertexCountBuffer!, 0u).ShouldBe(2f);
        ReadScalar(targetRenderer.MeshDeformVertexOffsetBuffer!, 1u).ShouldBe(2f);
        ReadScalar(targetRenderer.MeshDeformVertexCountBuffer!, 1u).ShouldBe(1f);
        ReadScalar(targetRenderer.MeshDeformVertexOffsetBuffer!, 2u).ShouldBe(3f);
        ReadScalar(targetRenderer.MeshDeformVertexCountBuffer!, 2u).ShouldBe(0f);

        targetRenderer.MeshDeformIndicesBuffer!.GetDataRawAtIndex<int>(0u).ShouldBe(1);
        targetRenderer.MeshDeformWeightsBuffer!.GetDataRawAtIndex<float>(0u).ShouldBe(0.6f);
        targetRenderer.MeshDeformIndicesBuffer.GetDataRawAtIndex<int>(1u).ShouldBe(2);
        targetRenderer.MeshDeformWeightsBuffer.GetDataRawAtIndex<float>(1u).ShouldBe(0.4f);
        targetRenderer.MeshDeformIndicesBuffer.GetDataRawAtIndex<int>(2u).ShouldBe(0);
        targetRenderer.MeshDeformWeightsBuffer.GetDataRawAtIndex<float>(2u).ShouldBe(1.0f);
    }

    [Test]
    public void UpdateDeformerPositions_CopiesSkinnedSourceBufferData()
    {
        var deformerRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh()
        };
        deformerRenderer.SkinnedPositionsBuffer = CreateVector4Buffer("SkinnedPositions", new[]
        {
            new Vector4(10f, 11f, 12f, 1f),
            new Vector4(20f, 21f, 22f, 1f),
            new Vector4(30f, 31f, 32f, 1f)
        });

        var targetRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh()
        };

        targetRenderer.SetupMeshDeformation(deformerRenderer,
        [
            [new MeshDeformInfluence(0, 1f)],
            [new MeshDeformInfluence(1, 1f)],
            [new MeshDeformInfluence(2, 1f)]
        ]);

        targetRenderer.UpdateDeformerPositions();

        targetRenderer.DeformerPositionsBuffer.ShouldNotBeNull();
        targetRenderer.DeformerPositionsBuffer!.GetVector4(0u).ShouldBe(new Vector4(10f, 11f, 12f, 1f));
        targetRenderer.DeformerPositionsBuffer.GetVector4(1u).ShouldBe(new Vector4(20f, 21f, 22f, 1f));
        targetRenderer.DeformerPositionsBuffer.GetVector4(2u).ShouldBe(new Vector4(30f, 31f, 32f, 1f));
    }

    [Test]
    public void UpdateDeformerNormalsAndTangents_CopySkinnedSourceBufferData()
    {
        var deformerRenderer = new XRMeshRenderer
        {
            Mesh = CreateAttributedTriangleMesh()
        };
        deformerRenderer.SkinnedNormalsBuffer = CreateVector4Buffer("SkinnedNormals", new[]
        {
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f)
        });
        deformerRenderer.SkinnedTangentsBuffer = CreateVector4Buffer("SkinnedTangents", new[]
        {
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(0f, 1f, 0f, -1f),
            new Vector4(0f, 0f, 1f, 1f)
        });

        var targetRenderer = new XRMeshRenderer
        {
            Mesh = CreateAttributedTriangleMesh()
        };

        targetRenderer.SetupMeshDeformation(deformerRenderer,
        [
            [new MeshDeformInfluence(0, 1f)],
            [new MeshDeformInfluence(1, 1f)],
            [new MeshDeformInfluence(2, 1f)]
        ]);

        targetRenderer.UpdateDeformerNormals();
        targetRenderer.UpdateDeformerTangents();

        targetRenderer.DeformerNormalsBuffer.ShouldNotBeNull();
        targetRenderer.DeformerTangentsBuffer.ShouldNotBeNull();
        targetRenderer.DeformerNormalsBuffer!.GetVector4(0u).ShouldBe(new Vector4(0f, 1f, 0f, 0f));
        targetRenderer.DeformerNormalsBuffer.GetVector4(1u).ShouldBe(new Vector4(1f, 0f, 0f, 0f));
        targetRenderer.DeformerTangentsBuffer!.GetVector4(1u).ShouldBe(new Vector4(0f, 1f, 0f, -1f));
        targetRenderer.DeformerTangentsBuffer.GetVector4(2u).ShouldBe(new Vector4(0f, 0f, 1f, 1f));
    }

    [Test]
    public void UpdateDeformerBuffers_CopyInterleavedSkinnedSourceData()
    {
        var deformerRenderer = new XRMeshRenderer
        {
            Mesh = CreateAttributedTriangleMesh()
        };
        deformerRenderer.Mesh!.InterleavedStride = 40u;
        deformerRenderer.Mesh.PositionOffset = 0u;
        deformerRenderer.Mesh.NormalOffset = 12u;
        deformerRenderer.Mesh.TangentOffset = 24u;
        deformerRenderer.SkinnedInterleavedBuffer = CreateInterleavedSkinnedBuffer(new[]
        {
            (new Vector3(10f, 11f, 12f), new Vector3(0f, 1f, 0f), new Vector4(1f, 0f, 0f, 1f)),
            (new Vector3(20f, 21f, 22f), new Vector3(1f, 0f, 0f), new Vector4(0f, 1f, 0f, -1f)),
            (new Vector3(30f, 31f, 32f), new Vector3(0f, 0f, 1f), new Vector4(0f, 0f, 1f, 1f))
        });

        var targetRenderer = new XRMeshRenderer
        {
            Mesh = CreateAttributedTriangleMesh()
        };

        targetRenderer.SetupMeshDeformation(deformerRenderer,
        [
            [new MeshDeformInfluence(0, 1f)],
            [new MeshDeformInfluence(1, 1f)],
            [new MeshDeformInfluence(2, 1f)]
        ]);

        targetRenderer.UpdateDeformerPositions();
        targetRenderer.UpdateDeformerNormals();
        targetRenderer.UpdateDeformerTangents();

        targetRenderer.DeformerPositionsBuffer!.GetVector4(0u).ShouldBe(new Vector4(10f, 11f, 12f, 1f));
        targetRenderer.DeformerNormalsBuffer!.GetVector4(1u).ShouldBe(new Vector4(1f, 0f, 0f, 0f));
        targetRenderer.DeformerTangentsBuffer!.GetVector4(2u).ShouldBe(new Vector4(0f, 0f, 1f, 1f));
        targetRenderer.MeshDeformSourcePath.ShouldBe("ComputeSkinnedInterleavedFallbackCopy");
    }

    [Test]
    public void ClearMeshDeformation_RemovesMeshDeformBuffersFromCollection()
    {
        var deformerRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh()
        };
        var targetRenderer = new XRMeshRenderer
        {
            Mesh = CreateSingleTriangleMesh()
        };

        targetRenderer.SetupMeshDeformation(deformerRenderer,
        [
            [new MeshDeformInfluence(0, 1f)],
            [new MeshDeformInfluence(1, 1f)],
            [new MeshDeformInfluence(2, 1f)]
        ]);

        targetRenderer.Buffers.ContainsKey("DeformerPositionsBuffer").ShouldBeTrue();
        targetRenderer.ClearMeshDeformation();

        targetRenderer.Buffers.ContainsKey("DeformerPositionsBuffer").ShouldBeFalse();
        targetRenderer.Buffers.ContainsKey("DeformerRestPositionsBuffer").ShouldBeFalse();
        targetRenderer.DeformerPositionsBuffer.ShouldBeNull();
        targetRenderer.MeshDeformValidationSummary.ShouldBe("Inactive");
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

    private static XRMesh CreateAttributedTriangleMesh()
        => XRMesh.Create(new VertexTriangle(
            new Vertex(new Vector3(0f, 0f, 0f), Vector3.UnitZ, Vector3.UnitX, Vector2.Zero, Vector4.One),
            new Vertex(new Vector3(1f, 0f, 0f), Vector3.UnitZ, Vector3.UnitX, Vector2.UnitX, Vector4.One),
            new Vertex(new Vector3(0f, 1f, 0f), Vector3.UnitZ, Vector3.UnitX, Vector2.UnitY, Vector4.One)));

    private static XRDataBuffer CreateVector4Buffer(string name, IReadOnlyList<Vector4> values)
    {
        var buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, (uint)values.Count, EComponentType.Float, 4, false, false)
        {
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false
        };

        for (uint i = 0; i < values.Count; i++)
            buffer.SetVector4(i, values[(int)i]);

        return buffer;
    }

    private static XRDataBuffer CreateInterleavedSkinnedBuffer(IReadOnlyList<(Vector3 position, Vector3 normal, Vector4 tangent)> values)
    {
        var buffer = new XRDataBuffer("InterleavedSkinned", EBufferTarget.ShaderStorageBuffer, (uint)(values.Count * 10), EComponentType.Float, 1, false, false)
        {
            Usage = EBufferUsage.DynamicDraw,
            DisposeOnPush = false
        };

        for (uint i = 0; i < values.Count; i++)
        {
            uint baseOffset = i * 40u;
            buffer.SetVector3AtOffset(baseOffset, values[(int)i].position);
            buffer.SetVector3AtOffset(baseOffset + 12u, values[(int)i].normal);
            buffer.SetVector4AtOffset(baseOffset + 24u, values[(int)i].tangent);
        }

        return buffer;
    }

    private static Vector4 ReadIndicesVector(XRDataBuffer buffer, uint index)
    {
        if (buffer.ComponentType == EComponentType.Int)
        {
            var value = buffer.GetDataRawAtIndex<IVector4>(index);
            return new Vector4(value.X, value.Y, value.Z, value.W);
        }

        return buffer.GetDataRawAtIndex<Vector4>(index);
    }

    private static float ReadScalar(XRDataBuffer buffer, uint index)
    {
        if (buffer.ComponentType == EComponentType.Int)
            return buffer.GetDataRawAtIndex<int>(index);

        return buffer.GetDataRawAtIndex<float>(index);
    }
}
