using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The page builder packs the clusters of a cluster hierarchy into fixed-size geometry pages. These
/// tests check the page rules on a small hand-built hierarchy, the page split on large groups, the
/// validation of broken page sets, and the pages of a hierarchy that the native cook built.
/// </summary>
[TestFixture]
public sealed class MeshletPageBuilderTests
{
    private const int VertexCount = 9;

    /// <summary>
    /// The hand-built hierarchy: nine vertices, three levels.
    /// <list type="bullet">
    /// <item>Meshlet 0: group 0 (level 0, error 0.5, center x = 0), vertices 0, 1, 2, 3.</item>
    /// <item>Meshlet 1: group 1 (level 0, error 0.7, center x = 1), vertices 4, 5, 6, 7.</item>
    /// <item>Meshlet 2: group 2 (level 1, error 2.0), made from group 0, vertices 0, 2, 3.</item>
    /// <item>Meshlet 3: group 2, made from group 1, vertices 4, 7.</item>
    /// <item>Meshlet 4: group 3 (level 2, root), made from group 2, vertices 0, 4, 7.</item>
    /// </list>
    /// The expected pages are: page 0 (root) holds group 3, page 1 holds groups 0 and 1, and
    /// page 2 holds group 2.
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
        new(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), 0.7f, 0),
        new(new Vector4(0.5f, 0.0f, 0.0f, 2.0f), 2.0f, 1),
        new(new Vector4(0.5f, 0.0f, 0.0f, 3.0f), float.MaxValue, 2),
    ];

    [Test]
    public void Build_PutsRootGroupsInTheFirstPages()
    {
        MeshletPageSet set = BuildStatic();

        set.Table.RootPageCount.ShouldBe(1);
        set.Table.PageCount.ShouldBe(3);
        set.Table.ClusterOrder.ShouldBe([4u, 0u, 1u, 2u, 3u]);
        set.Table.Pages.Select(static p => (p.FirstCluster, p.ClusterCount)).ShouldBe([(0u, 1u), (1u, 2u), (3u, 2u)]);
        (MeshletPageHeader.Read(set.PageImages[0]).Flags & EMeshletPageFlags.Root).ShouldBe(EMeshletPageFlags.Root);
        (MeshletPageHeader.Read(set.PageImages[1]).Flags & EMeshletPageFlags.Root).ShouldBe(EMeshletPageFlags.None);
    }

    [Test]
    public void Build_StoresThePageOfEachGroup()
    {
        MeshletPageSet set = BuildStatic();

        set.Table.Groups.Select(static g => g.Reserved0).ShouldBe([1u, 1u, 2u, 0u]);
        set.Table.ClusterRecords.ShouldBe([ClusterLods[4], ClusterLods[0], ClusterLods[1], ClusterLods[2], ClusterLods[3]]);
    }

    [Test]
    public void Build_ListsThePagesOfTheParentGroupsAsDependencies()
    {
        MeshletPageSet set = BuildStatic();

        set.Table.GetDependencies(0).ToArray().ShouldBeEmpty();
        set.Table.GetDependencies(1).ToArray().ShouldBe([2u]);
        set.Table.GetDependencies(2).ToArray().ShouldBe([0u]);
    }

    [Test]
    public void StaticPage_DecodesToThePayloadTriangles()
    {
        MeshletPageSet set = BuildStatic();
        byte[] image = set.PageImages[1];
        MeshletPageHeader header = MeshletPageHeader.Read(image);

        header.ClusterCount.ShouldBe((ushort)2);
        header.VertexCount.ShouldBe((ushort)8);
        AdvancedMeshletDescriptor[] descriptors = MemoryMarshal.Cast<byte, AdvancedMeshletDescriptor>(
            image.AsSpan((int)header.DescriptorOffset, header.ClusterCount * MeshletPageFormat.DescriptorBytes)).ToArray();
        uint[] vertexIndices = MemoryMarshal.Cast<byte, uint>(
            image.AsSpan((int)header.VertexIndexOffset, (int)(header.TriangleOffset - header.VertexIndexOffset))).ToArray();
        AdvancedDeformedVertex[] vertices = MemoryMarshal.Cast<byte, AdvancedDeformedVertex>(
            image.AsSpan((int)header.VertexOffset, header.VertexCount * MeshletPageFormat.PackedVertexBytes)).ToArray();

        for (int k = 0; k < descriptors.Length; k++)
        {
            int meshlet = (int)set.Table.ClusterOrder[header.FirstCluster + k];
            AdvancedMeshletDescriptor descriptor = descriptors[k];
            for (int corner = 0; corner < 3; corner++)
            {
                byte local = image[header.TriangleOffset + descriptor.TriangleByteOffset + corner];
                uint pageVertex = vertexIndices[descriptor.VertexOffset + local];
                AdvancedDeformedVertex vertex = vertices[pageVertex];
                uint expected = MeshletVertices[meshlet][Math.Min(corner, MeshletVertices[meshlet].Length - 1)];
                vertex.SourceVertex.ShouldBe(expected);
                vertex.Position.X.ShouldBe(expected);
            }
        }
    }

    [Test]
    public void SkinnedTopologyPage_KeepsSourceVertexIndicesAndHoldsNoVertices()
    {
        (CpuMeshletDescriptor[] descriptors, uint[] vertexIndices, byte[] triangles) = CreateStreams(MeshletVertices);
        MeshletPageSet set = MeshletPageBuilder.Build(descriptors, vertexIndices, triangles, ClusterLods, Groups, VertexCount, [], skinnedTopology: true);

        set.Table.Kind.ShouldBe(EMeshletPageFlags.SkinnedTopology);
        set.Stats.PageVertexCount.ShouldBe(0L);
        MeshletPageHeader header = MeshletPageHeader.Read(set.PageImages[1]);
        header.VertexCount.ShouldBe((ushort)0);
        (header.Flags & EMeshletPageFlags.SkinnedTopology).ShouldBe(EMeshletPageFlags.SkinnedTopology);
        uint[] pageIndices = MemoryMarshal.Cast<byte, uint>(
            set.PageImages[1].AsSpan((int)header.VertexIndexOffset, 8 * MeshletPageFormat.VertexIndexBytes)).ToArray();
        pageIndices.ShouldBe([0u, 1u, 2u, 3u, 4u, 5u, 6u, 7u]);
    }

    [Test]
    public void Build_StartsANewPageWhenTheNextGroupDoesNotFit()
    {
        // Three level-0 groups of 16 clusters, each cluster with 64 vertices of its own and 124
        // triangles (about 76 KiB per group), and one root group made from them.
        const int LevelZeroGroups = 3;
        const int ClustersPerGroup = 16;
        List<uint[]> meshletVertices = [];
        List<uint[]> meshletTriangles = [];
        List<MeshletClusterLodRecord> lods = [];
        List<MeshletLodGroupRecord> groups = [];
        uint nextVertex = 0u;
        for (int g = 0; g < LevelZeroGroups; g++)
        {
            groups.Add(new MeshletLodGroupRecord(new Vector4(g, 0.0f, 0.0f, 1.0f), 0.1f, 0));
            for (int c = 0; c < ClustersPerGroup; c++)
            {
                meshletVertices.Add([.. Enumerable.Range((int)nextVertex, 64).Select(static v => (uint)v)]);
                meshletTriangles.Add(FanTriangles(64, 124));
                lods.Add(new MeshletClusterLodRecord(g, -1));
                nextVertex += 64u;
            }
        }
        groups.Add(new MeshletLodGroupRecord(new Vector4(1.0f, 0.0f, 0.0f, 4.0f), float.MaxValue, 1));
        for (int g = 0; g < LevelZeroGroups; g++)
        {
            uint first = (uint)(g * ClustersPerGroup * 64);
            meshletVertices.Add([first, first + 1u, first + 2u]);
            meshletTriangles.Add([0, 1, 2]);
            lods.Add(new MeshletClusterLodRecord(LevelZeroGroups, g));
        }

        (CpuMeshletDescriptor[] descriptors, uint[] vertexIndices, byte[] triangles) = CreateStreams(meshletVertices, meshletTriangles);
        AdvancedDeformedVertex[] packed = CreatePackedVertices((int)nextVertex);
        MeshletPageSet set = MeshletPageBuilder.Build(descriptors, vertexIndices, triangles, [.. lods], [.. groups], (int)nextVertex, packed, skinnedTopology: false);

        set.Table.RootPageCount.ShouldBe(1);
        set.Table.PageCount.ShouldBe(1 + LevelZeroGroups);
        for (int page = 1; page < set.Table.PageCount; page++)
        {
            set.Table.Pages[page].ClusterCount.ShouldBe((uint)ClustersPerGroup);
            set.Table.GetDependencies(page).ToArray().ShouldBe([0u]);
            set.PageImages[page].Length.ShouldBeLessThanOrEqualTo(MeshletPageFormat.PageBytes);
        }
    }

    [Test]
    public void Build_RejectsAGroupWithMoreClustersThanAPageHolds()
    {
        List<uint[]> meshletVertices = [];
        List<MeshletClusterLodRecord> lods = [];
        for (int c = 0; c <= MeshletPageFormat.MaxClustersPerPage; c++)
        {
            meshletVertices.Add([0u, 1u, 2u]);
            lods.Add(new MeshletClusterLodRecord(0, -1));
        }
        MeshletLodGroupRecord[] groups = [new(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), float.MaxValue, 0)];
        (CpuMeshletDescriptor[] descriptors, uint[] vertexIndices, byte[] triangles) = CreateStreams(meshletVertices);

        Should.Throw<InvalidDataException>(() => MeshletPageBuilder.Build(
            descriptors, vertexIndices, triangles, [.. lods], groups, 3, CreatePackedVertices(3), skinnedTopology: false));
    }

    [Test]
    public void Validate_RejectsAPageWithoutItsDependency()
    {
        MeshletPageSet set = BuildStatic();
        MeshletPageRecord[] pages = (MeshletPageRecord[])set.Table.Pages.Clone();
        pages[1] = pages[1] with { DependencyCount = 0u };
        MeshletPageSet broken = set with { Table = CopyTable(set.Table, pages: pages) };

        Should.Throw<InvalidDataException>(() => Validate(broken));
    }

    [Test]
    public void Validate_RejectsAPageThatMixesRootAndNonRootGroups()
    {
        MeshletPageSet set = BuildStatic();
        MeshletPageSet broken = set with { Table = CopyTable(set.Table, rootPageCount: 2) };

        Should.Throw<InvalidDataException>(() => Validate(broken));
    }

    [Test]
    public void Validate_RejectsATriangleThatDecodesToAnotherVertex()
    {
        MeshletPageSet set = BuildStatic();
        byte[][] images = [.. set.PageImages.Select(static image => (byte[])image.Clone())];
        MeshletPageHeader header = MeshletPageHeader.Read(images[1]);
        // The first triangle of meshlet 0 is (0, 1, 2); swapping two corners keeps it in range.
        (images[1][header.TriangleOffset], images[1][header.TriangleOffset + 1]) =
            (images[1][header.TriangleOffset + 1], images[1][header.TriangleOffset]);
        MeshletPageSet broken = set with { PageImages = images };

        Should.Throw<InvalidDataException>(() => Validate(broken));
    }

    [Test]
    public void CookedHierarchy_PagesPassValidation()
    {
        MeshOptimizerBackend.Register();
        XRMesh mesh = CreateIndexedGrid(96);
        MeshletGenerationSettings settings = new()
        {
            Enabled = true,
            BuildMode = MeshletBuildMode.Dense,
            MaxVertices = 64u,
            MaxTriangles = 124u,
            OptimizeMeshlets = true,
            ComputeBounds = true,
            BuildClusterHierarchy = true,
        };
        MeshletPayload payload = mesh.GetOrCreateMeshletPayload(settings, null, "page-builder-grid");
        payload.HasClusterHierarchy.ShouldBeTrue();
        AdvancedDeformedVertex[] packed = new AdvancedDeformedVertex[mesh.VertexCount];
        AdvancedPackedVertexCodec.Pack(mesh, 0u, packed, 0u);

        MeshletPageSet set = MeshletPageBuilder.Build(payload, packed);

        set.Table.ClusterCount.ShouldBe(payload.Meshlets.Length);
        set.Table.PageCount.ShouldBeGreaterThan(set.Table.RootPageCount);
        payload.LodGroups.Max(static g => g.Depth).ShouldBeGreaterThan(0);
        for (int page = set.Table.RootPageCount; page < set.Table.PageCount; page++)
            set.Table.GetDependencies(page).Length.ShouldBeGreaterThan(0);
    }

    private static MeshletPageSet BuildStatic()
    {
        (CpuMeshletDescriptor[] descriptors, uint[] vertexIndices, byte[] triangles) = CreateStreams(MeshletVertices);
        return MeshletPageBuilder.Build(descriptors, vertexIndices, triangles, ClusterLods, Groups, VertexCount, CreatePackedVertices(VertexCount), skinnedTopology: false);
    }

    private static void Validate(MeshletPageSet set)
    {
        (CpuMeshletDescriptor[] descriptors, uint[] vertexIndices, byte[] triangles) = CreateStreams(MeshletVertices);
        MeshletPageBuilder.Validate(set, descriptors, vertexIndices, triangles, ClusterLods, Groups, CreatePackedVertices(VertexCount));
    }

    private static MeshletPageTable CopyTable(MeshletPageTable table, MeshletPageRecord[]? pages = null, int? rootPageCount = null)
        => new()
        {
            Kind = table.Kind,
            RootPageCount = rootPageCount ?? table.RootPageCount,
            ClusterOrder = table.ClusterOrder,
            ClusterRecords = table.ClusterRecords,
            Groups = table.Groups,
            Pages = pages ?? table.Pages,
            Dependencies = table.Dependencies,
        };

    /// <summary>
    /// Creates packed vertices whose x position is the vertex index. The source vertex field holds
    /// a wrong value, so the tests also check that the builder writes the right one.
    /// </summary>
    private static AdvancedDeformedVertex[] CreatePackedVertices(int count)
    {
        AdvancedDeformedVertex[] vertices = new AdvancedDeformedVertex[count];
        for (int i = 0; i < count; i++)
            vertices[i] = AdvancedPackedVertexCodec.Pack(
                new Vector3(i, 0.0f, 0.0f), Vector3.UnitZ, Vector3.UnitX, 1.0f,
                Vector2.Zero, Vector2.Zero, Vector4.One, Vector4.One, sourceVertex: uint.MaxValue);
        return vertices;
    }

    /// <summary>Returns <paramref name="triangleCount"/> fan triangles over <paramref name="vertexCount"/> local vertices.</summary>
    private static uint[] FanTriangles(int vertexCount, int triangleCount)
    {
        uint[] corners = new uint[triangleCount * 3];
        for (int t = 0; t < triangleCount; t++)
        {
            corners[t * 3] = 0u;
            corners[t * 3 + 1] = (uint)(1 + t % (vertexCount - 2));
            corners[t * 3 + 2] = (uint)(2 + t % (vertexCount - 2));
        }
        return corners;
    }

    /// <summary>
    /// Creates the meshlet streams for the given meshlet vertex lists. Each meshlet gets one triangle
    /// of its first three local vertices (or of its last vertex repeated).
    /// </summary>
    private static (CpuMeshletDescriptor[] Descriptors, uint[] VertexIndices, byte[] TriangleIndices) CreateStreams(IReadOnlyList<uint[]> meshletVertices)
    {
        List<uint[]> triangles = [];
        foreach (uint[] vertices in meshletVertices)
        {
            uint last = (uint)(vertices.Length - 1);
            triangles.Add([0u, Math.Min(1u, last), Math.Min(2u, last)]);
        }
        return CreateStreams(meshletVertices, triangles);
    }

    private static (CpuMeshletDescriptor[] Descriptors, uint[] VertexIndices, byte[] TriangleIndices) CreateStreams(
        IReadOnlyList<uint[]> meshletVertices,
        IReadOnlyList<uint[]> meshletTriangles)
    {
        List<CpuMeshletDescriptor> descriptors = [];
        List<uint> vertexIndices = [];
        List<byte> triangles = [];
        for (int m = 0; m < meshletVertices.Count; m++)
        {
            uint vertexOffset = (uint)vertexIndices.Count;
            uint triangleOffset = (uint)triangles.Count;
            vertexIndices.AddRange(meshletVertices[m]);
            foreach (uint corner in meshletTriangles[m])
                triangles.Add((byte)corner);
            while ((triangles.Count & 3) != 0)
                triangles.Add(0);
            descriptors.Add(new CpuMeshletDescriptor(
                new Vector4(m, 0.0f, 0.0f, 1.0f),
                vertexOffset,
                triangleOffset,
                (uint)meshletVertices[m].Length,
                (uint)(meshletTriangles[m].Length / 3),
                Vector4.Zero,
                Vector4.Zero,
                0u));
        }
        return ([.. descriptors], [.. vertexIndices], [.. triangles]);
    }

    /// <summary>Creates a flat, welded grid of <paramref name="cells"/> by <paramref name="cells"/> quads with normals and UVs.</summary>
    private static XRMesh CreateIndexedGrid(int cells)
    {
        List<Vertex> vertices = new((cells + 1) * (cells + 1));
        for (int y = 0; y <= cells; y++)
            for (int x = 0; x <= cells; x++)
                vertices.Add(new Vertex(new Vector3(x, y, 0.0f))
                {
                    Normal = Vector3.UnitZ,
                    TextureCoordinateSets = [new Vector2(x / (float)cells, y / (float)cells)],
                });

        List<ushort> indices = new(cells * cells * 6);
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                ushort a = (ushort)(y * (cells + 1) + x);
                ushort b = (ushort)(a + 1);
                ushort c = (ushort)(a + cells + 1);
                ushort d = (ushort)(c + 1);
                indices.AddRange([a, b, d, a, d, c]);
            }
        return new XRMesh(vertices, indices);
    }
}
