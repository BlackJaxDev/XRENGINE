using System;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Caching;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The page file stores the streaming pages of a model next to its meshlet payload file. A stored
/// entry may only be used for the same payload and vertex data, and every page must read back to
/// the cooked image.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ModelImportMeshletPageCacheTests
{
    private const string FirstMesh = "model/mesh:0/lod:0";
    private const string SecondMesh = "model/mesh:1/lod:0";
    private const ulong Freshness = 0x1234_5678_9ABC_DEF0UL;
    private const ulong VertexHash = 0x0FED_CBA9_8765_4321UL;
    private string _root = string.Empty;
    private string _payloadFile = string.Empty;

    [SetUp]
    public void CreateRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "xre-meshlet-page-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _payloadFile = Path.Combine(_root, "0123456789ABCDEF.xrmeshlets");
    }

    [TearDown]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void SavedPages_ReadBackToTheCookedImages()
    {
        MeshletPageSet set = BuildSet(offset: 0.0f);
        Save(cache => cache.Record(FirstMesh, Freshness, VertexHash, set));

        ModelImportMeshletPageCache reopened = ModelImportMeshletPageCache.Open(_payloadFile);
        reopened.LoadFailure.ShouldBeNull();
        reopened.FilePath.ShouldEndWith(ModelImportMeshletPageCache.FileExtension);
        reopened.TryUse(FirstMesh, Freshness, VertexHash, out MeshletPageTable? table).ShouldBeTrue();
        table!.ClusterOrder.ShouldBe(set.Table.ClusterOrder);
        table.Pages.ShouldBe(set.Table.Pages);
        table.Groups.ShouldBe(set.Table.Groups);
        table.Dependencies.ShouldBe(set.Table.Dependencies);
        byte[] page = new byte[MeshletPageFormat.PageBytes];
        for (int p = 0; p < set.Table.PageCount; p++)
        {
            int bytes = reopened.ReadPage(FirstMesh, p, page);
            page.AsSpan(0, bytes).ToArray().ShouldBe(set.PageImages[p]);
        }
    }

    [Test]
    public void ChangedVertexData_IsNotUsed()
    {
        Save(cache => cache.Record(FirstMesh, Freshness, VertexHash, BuildSet(offset: 0.0f)));

        ModelImportMeshletPageCache reopened = ModelImportMeshletPageCache.Open(_payloadFile);

        reopened.TryUse(FirstMesh, Freshness, VertexHash + 1UL, out _).ShouldBeFalse();
        reopened.TryUse(FirstMesh, Freshness + 1UL, VertexHash, out _).ShouldBeFalse();
        reopened.HitCount.ShouldBe(0);
    }

    [Test]
    public void SecondSave_KeepsTheStoredBlobsOfReusedMeshes()
    {
        MeshletPageSet first = BuildSet(offset: 0.0f);
        Save(cache => cache.Record(FirstMesh, Freshness, VertexHash, first));

        MeshletPageSet second = BuildSet(offset: 5.0f);
        ModelImportMeshletPageCache cache = ModelImportMeshletPageCache.Open(_payloadFile);
        cache.TryUse(FirstMesh, Freshness, VertexHash, out _).ShouldBeTrue();
        cache.Record(SecondMesh, Freshness, VertexHash, second);
        cache.SaveIfChanged().ShouldBeTrue();

        ModelImportMeshletPageCache reopened = ModelImportMeshletPageCache.Open(_payloadFile);
        reopened.StoredCount.ShouldBe(2);
        byte[] page = new byte[MeshletPageFormat.PageBytes];
        int bytes = reopened.ReadPage(FirstMesh, 1, page);
        page.AsSpan(0, bytes).ToArray().ShouldBe(first.PageImages[1]);
        bytes = reopened.ReadPage(SecondMesh, 1, page);
        page.AsSpan(0, bytes).ToArray().ShouldBe(second.PageImages[1]);
    }

    [Test]
    public void UnchangedImport_DoesNotWriteTheFile()
    {
        Save(cache => cache.Record(FirstMesh, Freshness, VertexHash, BuildSet(offset: 0.0f)));

        ModelImportMeshletPageCache cache = ModelImportMeshletPageCache.Open(_payloadFile);
        cache.TryUse(FirstMesh, Freshness, VertexHash, out _).ShouldBeTrue();

        cache.SaveIfChanged().ShouldBeFalse();
    }

    [Test]
    public void DamagedIndex_IsReportedAndIgnored()
    {
        Save(cache => cache.Record(FirstMesh, Freshness, VertexHash, BuildSet(offset: 0.0f)));
        string file = Path.ChangeExtension(_payloadFile, ModelImportMeshletPageCache.FileExtension);
        byte[] bytes = File.ReadAllBytes(file);
        bytes[40] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        ModelImportMeshletPageCache reopened = ModelImportMeshletPageCache.Open(_payloadFile);

        reopened.LoadFailure.ShouldNotBeNull();
        reopened.StoredCount.ShouldBe(0);
    }

    [Test]
    public void DamagedBlob_IsRejected()
    {
        MeshletPageSet set = BuildSet(offset: 0.0f);
        Save(cache => cache.Record(FirstMesh, Freshness, VertexHash, set));
        string file = Path.ChangeExtension(_payloadFile, ModelImportMeshletPageCache.FileExtension);
        byte[] bytes = File.ReadAllBytes(file);
        // The last byte of the file is the last byte of the last blob.
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        ModelImportMeshletPageCache reopened = ModelImportMeshletPageCache.Open(_payloadFile);
        byte[] page = new byte[MeshletPageFormat.PageBytes];

        Should.Throw<InvalidDataException>(() => reopened.ReadPage(FirstMesh, set.Table.PageCount - 1, page));
    }

    private void Save(Action<ModelImportMeshletPageCache> record)
    {
        ModelImportMeshletPageCache cache = ModelImportMeshletPageCache.Open(_payloadFile);
        record(cache);
        cache.SaveIfChanged().ShouldBeTrue();
    }

    /// <summary>
    /// Builds the pages of a two-level hierarchy: two level-0 clusters in one group and one root
    /// cluster made from that group. <paramref name="offset"/> moves the vertices, so two sets
    /// have different page images.
    /// </summary>
    private static MeshletPageSet BuildSet(float offset)
    {
        CpuMeshletDescriptor[] descriptors =
        [
            new(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), 0u, 0u, 3u, 1u, Vector4.Zero, Vector4.Zero, 0u),
            new(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), 3u, 4u, 3u, 1u, Vector4.Zero, Vector4.Zero, 0u),
            new(new Vector4(0.5f, 0.0f, 0.0f, 2.0f), 6u, 8u, 3u, 1u, Vector4.Zero, Vector4.Zero, 0u),
        ];
        uint[] vertexIndices = [0u, 1u, 2u, 3u, 4u, 5u, 0u, 2u, 5u];
        byte[] triangles = [0, 1, 2, 0, 0, 1, 2, 0, 0, 1, 2, 0];
        MeshletClusterLodRecord[] lods = [new(0, -1), new(0, -1), new(1, 0)];
        MeshletLodGroupRecord[] groups =
        [
            new(new Vector4(0.5f, 0.0f, 0.0f, 2.0f), 0.25f, 0),
            new(new Vector4(0.5f, 0.0f, 0.0f, 3.0f), float.MaxValue, 1),
        ];
        AdvancedDeformedVertex[] packed = [.. Enumerable.Range(0, 6).Select(i => AdvancedPackedVertexCodec.Pack(
            new Vector3(i + offset, i * 0.5f, 0.0f), Vector3.UnitZ, Vector3.UnitX, 1.0f,
            new Vector2(i * 0.1f, 0.0f), Vector2.Zero, Vector4.One, Vector4.One, (uint)i))];
        return MeshletPageBuilder.Build(descriptors, vertexIndices, triangles, lods, groups, packed.Length, packed, skinnedTopology: false);
    }
}
