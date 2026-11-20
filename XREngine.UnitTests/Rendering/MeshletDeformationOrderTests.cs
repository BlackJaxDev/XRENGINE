using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The deformation order lets a far skinned instance deform only the vertices that its coarsest
/// allowed level reads. These tests check the order and prefix counts on a small hand-built
/// hierarchy, and the round trip of the tables through the payload serializer.
/// </summary>
[TestFixture]
public sealed class MeshletDeformationOrderTests
{
    /// <summary>
    /// The hand-built hierarchy: nine vertices, three levels.
    /// <list type="bullet">
    /// <item>Meshlet 0: group 0 (level 0, error 0.5), vertices 0, 1, 2, 3.</item>
    /// <item>Meshlet 1: group 1 (level 0, error 0.7), vertices 4, 5, 6, 7.</item>
    /// <item>Meshlet 2: group 2 (level 1, error 2.0), made from group 0, vertices 0, 2, 3.</item>
    /// <item>Meshlet 3: group 2, made from group 1, vertices 4, 7.</item>
    /// <item>Meshlet 4: group 3 (level 2, root), made from group 2, vertices 0, 4, 7.</item>
    /// </list>
    /// Vertex 8 is in no meshlet.
    /// </summary>
    private static readonly uint[][] MeshletVertices =
    [
        [0u, 1u, 2u, 3u],
        [4u, 5u, 6u, 7u],
        [0u, 2u, 3u],
        [4u, 7u],
        [0u, 4u, 7u],
    ];

    private static readonly MeshletClusterLodRecord[] ClusterLods =
    [
        new(0, -1),
        new(1, -1),
        new(2, 0),
        new(2, 1),
        new(3, 2),
    ];

    private static readonly MeshletLodGroupRecord[] Groups =
    [
        new(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), 0.5f, 0),
        new(new Vector4(2.0f, 0.0f, 0.0f, 1.0f), 0.7f, 0),
        new(new Vector4(1.0f, 0.0f, 0.0f, 2.0f), 2.0f, 1),
        new(new Vector4(1.0f, 0.0f, 0.0f, 3.0f), float.MaxValue, 2),
    ];

    private const int VertexCount = 9;

    [Test]
    public void Build_PutsCoarseVerticesFirstAndUnusedVerticesLast()
    {
        MeshletDeformationOrderBuilder.Result result = BuildOrder();

        // Root vertices (0, 4, 7 are read by the root meshlet), then level 1 (2, 3), then level 0
        // (1, 5, 6), then the unused vertex 8. Source order is kept inside one key.
        result.VertexOrder.ShouldBe([0u, 4u, 7u, 2u, 3u, 1u, 5u, 6u, 8u]);
        result.PrefixCounts.ShouldBe([8u, 5u, 3u]);
        result.LevelMaxErrors.ShouldBe([0.7f, 2.0f, 0.0f]);
    }

    [Test]
    public void Build_EveryMeshletOfLevelLOrCoarserReadsOnlyPrefixVertices()
    {
        MeshletDeformationOrderBuilder.Result result = BuildOrder();

        for (int level = 0; level < result.PrefixCounts.Length; level++)
        {
            HashSet<uint> prefix = [.. result.VertexOrder.Take((int)result.PrefixCounts[level])];
            for (int m = 0; m < MeshletVertices.Length; m++)
            {
                MeshletLodGroupRecord group = Groups[ClusterLods[m].Group];
                bool drawableAtLevel = group.Depth >= level || group.SimplifiedError >= float.MaxValue;
                if (!drawableAtLevel)
                    continue;
                foreach (uint vertex in MeshletVertices[m])
                    prefix.ShouldContain(vertex, $"meshlet {m} at clamp level {level}");
            }
        }
    }

    [Test]
    public void Build_KeepsRootVerticesOfAnEndedLevel0GroupInEveryPrefix()
    {
        // Group 1 ended its branch at level 0, so its meshlet is a root of the cut and can draw at
        // every distance. Its vertices must be in the coarsest prefix.
        MeshletLodGroupRecord[] groups = [.. Groups];
        groups[1] = groups[1] with { SimplifiedError = float.MaxValue };
        MeshletClusterLodRecord[] clusterLods =
        [
            new(0, -1),
            new(1, -1),
            new(2, 0),
            new(3, 2),
        ];
        uint[][] meshletVertices =
        [
            [0u, 1u, 2u, 3u],
            [4u, 5u, 6u, 7u],
            [0u, 2u, 3u],
            [0u, 3u],
        ];

        MeshletDeformationOrderBuilder.Result result = Build(meshletVertices, clusterLods, groups);

        HashSet<uint> coarsest = [.. result.VertexOrder.Take((int)result.PrefixCounts[^1])];
        coarsest.ShouldBe(new HashSet<uint> { 0u, 3u, 4u, 5u, 6u, 7u }, ignoreOrder: true);
        result.LevelMaxErrors[0].ShouldBe(0.5f);
    }

    [Test]
    public void Build_RejectsAVertexOutOfRange()
    {
        uint[][] meshletVertices = [[0u, 1u, 99u]];
        MeshletClusterLodRecord[] clusterLods = [new(0, -1)];
        MeshletLodGroupRecord[] groups = [new(Vector4.UnitW, float.MaxValue, 0)];

        Should.Throw<InvalidDataException>(() => Build(meshletVertices, clusterLods, groups));
    }

    [Test]
    public void Payload_RoundTripsTheDeformationOrderTables()
    {
        MeshletPayload payload = CreateSkinnedHierarchyPayload(BuildOrder());

        byte[] bytes = XRMesh.SerializeMeshletPayloadToBytes(payload);
        MeshletPayload? read = XRMesh.DeserializeMeshletPayloadFromBytes(bytes);

        read.ShouldNotBeNull();
        read.PayloadVersion.ShouldBe(MeshletPayload.DeformationOrderPayloadVersion);
        read.HasDeformationOrder.ShouldBeTrue();
        read.DeformationVertexOrder.ToArray().ShouldBe(payload.DeformationVertexOrder.ToArray());
        read.DeformationPrefixCounts.ToArray().ShouldBe(payload.DeformationPrefixCounts.ToArray());
        read.LevelMaxErrors.ToArray().ShouldBe(payload.LevelMaxErrors.ToArray());
    }

    [Test]
    public void Payload_RejectsAnOrderThatIsNotAPermutation()
    {
        MeshletDeformationOrderBuilder.Result order = BuildOrder();
        uint[] broken = (uint[])order.VertexOrder.Clone();
        broken[1] = broken[0];

        Should.Throw<InvalidDataException>(() => CreateSkinnedHierarchyPayload(order with { VertexOrder = broken }));
    }

    [Test]
    public void Payload_RejectsPrefixCountsThatGrowWithTheLevel()
    {
        MeshletDeformationOrderBuilder.Result order = BuildOrder();
        uint[] broken = [5u, 8u, 3u];

        Should.Throw<InvalidDataException>(() => CreateSkinnedHierarchyPayload(order with { PrefixCounts = broken }));
    }

    private static MeshletDeformationOrderBuilder.Result BuildOrder()
        => Build(MeshletVertices, ClusterLods, Groups);

    private static MeshletDeformationOrderBuilder.Result Build(
        uint[][] meshletVertices,
        MeshletClusterLodRecord[] clusterLods,
        MeshletLodGroupRecord[] groups)
    {
        (CpuMeshletDescriptor[] descriptors, uint[] vertexIndices, _) = CreateStreams(meshletVertices);
        return MeshletDeformationOrderBuilder.Build(descriptors, vertexIndices, clusterLods, groups, VertexCount);
    }

    /// <summary>
    /// Creates the meshlet streams for the given meshlet vertex lists. Each meshlet gets one triangle
    /// of its first three local vertices (or of its first vertex repeated), which is enough for the
    /// payload validation.
    /// </summary>
    private static (CpuMeshletDescriptor[] Descriptors, uint[] VertexIndices, byte[] TriangleIndices) CreateStreams(uint[][] meshletVertices)
    {
        List<CpuMeshletDescriptor> descriptors = [];
        List<uint> vertexIndices = [];
        List<byte> triangles = [];
        foreach (uint[] vertices in meshletVertices)
        {
            uint vertexOffset = (uint)vertexIndices.Count;
            uint triangleOffset = (uint)triangles.Count;
            vertexIndices.AddRange(vertices);
            byte last = (byte)(vertices.Length - 1);
            triangles.Add(0);
            triangles.Add(Math.Min((byte)1, last));
            triangles.Add(Math.Min((byte)2, last));
            while ((triangles.Count & 3) != 0)
                triangles.Add(0);
            descriptors.Add(new CpuMeshletDescriptor(
                new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                vertexOffset,
                triangleOffset,
                (uint)vertices.Length,
                1u,
                Vector4.Zero,
                Vector4.Zero,
                0u));
        }
        return ([.. descriptors], [.. vertexIndices], [.. triangles]);
    }

    private static MeshletPayload CreateSkinnedHierarchyPayload(MeshletDeformationOrderBuilder.Result order)
    {
        (CpuMeshletDescriptor[] descriptors, uint[] vertexIndices, byte[] triangles) = CreateStreams(MeshletVertices);
        MeshletGenerationSettingsSnapshot settings = MeshletGenerationSettingsSnapshot.From(
            new MeshletGenerationSettings { BuildClusterHierarchy = true });
        MeshletClusterSkinRecord[] skins = [.. Enumerable.Repeat(new MeshletClusterSkinRecord(0u, 1, 0), descriptors.Length)];

        MeshletPayload payload = new()
        {
            PayloadVersion = MeshletPayload.ExpectedPayloadVersion(clusterHierarchy: true),
            GenerationEnabled = true,
            State = MeshletPayloadState.Present,
            RuntimeCompatibilityToken = MeshletPayloadUtility.ComputeRuntimeCompatibilityToken(settings),
            SourceMeshIdentity = "deformation-order-test",
            SourceVertexCount = VertexCount,
            SourceTriangleCount = descriptors.Length,
            MeshletSettings = settings,
            Meshlets = [.. descriptors],
            VertexIndices = [.. vertexIndices],
            TriangleIndices = [.. triangles],
            Stats = new MeshOptimizerMeshletStats(descriptors.Length, vertexIndices.Length, triangles.Length, 0),
            ClusterLevel0Count = 2,
            ClusterLods = [.. ClusterLods],
            LodGroups = [.. Groups],
            ClusterSkins = [.. skins],
            ClusterBones = [0],
            DeformationVertexOrder = [.. order.VertexOrder],
            DeformationPrefixCounts = [.. order.PrefixCounts],
            LevelMaxErrors = [.. order.LevelMaxErrors],
        };
        payload.ValidatePortablePayload();
        return payload;
    }
}
