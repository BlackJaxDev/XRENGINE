using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Caching;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The import meshlet cache may only attach a payload that is valid for the mesh and fresh for the
/// requested settings. Every other case must cook the mesh.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ModelImportMeshletPayloadCacheTests
{
    private const string ModelIdentity = "model-identity-under-test";
    private string _root = string.Empty;

    [OneTimeSetUp]
    public void InstallMeshProcessing() => MeshOptimizerBackend.Register();

    [SetUp]
    public void CreateRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "xre-meshlet-cache-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void SecondImport_AttachesTheCachedPayloadWithoutCooking()
    {
        string identity = ModelIdentity + "/mesh:0/lod:0";
        MeshletGenerationSettings settings = CreateSettings();
        PublishOne(CreateGrid(offset: 0.0f), identity, settings);

        ModelImportMeshletPayloadCache cache = ModelImportMeshletPayloadCache.TryOpen(_root, ModelIdentity)!;
        cache.StoredCount.ShouldBe(1);
        XRMesh mesh = CreateGrid(offset: 0.0f);

        cache.TryAttach(mesh, identity, settings, null).ShouldBeTrue();

        cache.HitCount.ShouldBe(1);
        cache.MissCount.ShouldBe(0);
        mesh.TryGetFreshMeshletPayload(settings, null, identity, out MeshletPayload attached).ShouldBeTrue();
        ReferenceEquals(mesh.GetOrCreateMeshletPayload(settings, null, identity), attached).ShouldBeTrue();
        cache.Record(attached);
        cache.SaveIfChanged().ShouldBeFalse();
    }

    [Test]
    public void ChangedGeometry_IsRejectedAndCooked()
    {
        string identity = ModelIdentity + "/mesh:0/lod:0";
        MeshletGenerationSettings settings = CreateSettings();
        PublishOne(CreateGrid(offset: 0.0f), identity, settings);

        ModelImportMeshletPayloadCache cache = ModelImportMeshletPayloadCache.TryOpen(_root, ModelIdentity)!;
        XRMesh edited = CreateGrid(offset: 0.25f);

        cache.TryAttach(edited, identity, settings, null).ShouldBeFalse();

        cache.RejectedCount.ShouldBe(1);
        cache.MissCount.ShouldBe(1);
        edited.HasMeshletPayload.ShouldBeFalse();
    }

    [Test]
    public void ChangedMeshletSettings_AreRejected()
    {
        string identity = ModelIdentity + "/mesh:0/lod:0";
        PublishOne(CreateGrid(offset: 0.0f), identity, CreateSettings(maxTriangles: 124u));

        ModelImportMeshletPayloadCache cache = ModelImportMeshletPayloadCache.TryOpen(_root, ModelIdentity)!;
        XRMesh mesh = CreateGrid(offset: 0.0f);
        MeshletGenerationSettings other = CreateSettings(maxTriangles: 64u);

        cache.TryAttach(mesh, identity, other, null).ShouldBeFalse();
        cache.RejectedCount.ShouldBe(1);
        mesh.TryGetFreshMeshletPayload(other, null, identity, out _).ShouldBeFalse();
    }

    [Test]
    public void UnknownMeshIdentity_IsAMiss()
    {
        string identity = ModelIdentity + "/mesh:0/lod:0";
        MeshletGenerationSettings settings = CreateSettings();
        PublishOne(CreateGrid(offset: 0.0f), identity, settings);

        ModelImportMeshletPayloadCache cache = ModelImportMeshletPayloadCache.TryOpen(_root, ModelIdentity)!;

        cache.TryAttach(CreateGrid(offset: 0.0f), ModelIdentity + "/mesh:1/lod:0", settings, null).ShouldBeFalse();
        cache.MissCount.ShouldBe(1);
        cache.RejectedCount.ShouldBe(0);
    }

    [Test]
    public void CorruptFile_IsReportedAndRewritten()
    {
        string identity = ModelIdentity + "/mesh:0/lod:0";
        MeshletGenerationSettings settings = CreateSettings();
        string path = Path.Combine(_root, ModelImportMeshletPayloadCache.CacheFolderName, ModelImportMeshletPayloadCache.ResolveFileName(ModelIdentity));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9]);

        ModelImportMeshletPayloadCache cache = ModelImportMeshletPayloadCache.TryOpen(_root, ModelIdentity)!;
        cache.LoadFailure.ShouldNotBeNull();
        cache.StoredCount.ShouldBe(0);

        XRMesh mesh = CreateGrid(offset: 0.0f);
        cache.TryAttach(mesh, identity, settings, null).ShouldBeFalse();
        cache.Record(mesh.GetOrCreateMeshletPayload(settings, null, identity));
        cache.SaveIfChanged().ShouldBeTrue();

        ModelImportMeshletPayloadCache.TryOpen(_root, ModelIdentity)!.StoredCount.ShouldBe(1);
    }

    [Test]
    public void FileName_DependsOnModelIdentity()
        => ModelImportMeshletPayloadCache.ResolveFileName("model-a")
            .ShouldNotBe(ModelImportMeshletPayloadCache.ResolveFileName("model-b"));

    [Test]
    public void SavedFile_LeavesNoTemporaryFiles()
    {
        PublishOne(CreateGrid(offset: 0.0f), ModelIdentity + "/mesh:0/lod:0", CreateSettings());

        string folder = Path.Combine(_root, ModelImportMeshletPayloadCache.CacheFolderName);
        Directory.GetFiles(folder, "*.tmp").ShouldBeEmpty();
        Directory.GetFiles(folder).Length.ShouldBe(1);
    }

    [Test]
    public void SourceMeshHash_MatchesPerValueReference()
    {
        List<Vertex> vertices = [];
        for (int index = 0; index < 700; ++index)
        {
            vertices.Add(new Vertex(new Vector3(index * 0.5f, -index, index * 0.25f))
            {
                Normal = Vector3.Normalize(new Vector3(1.0f, index, 2.0f)),
                Tangent = Vector3.UnitX,
                BitangentSign = index % 2 == 0 ? 1.0f : -1.0f,
                TextureCoordinateSets = [new Vector2(index * 0.01f, 1.0f - index * 0.02f), new Vector2(index, -index)],
                ColorSets = [new Vector4(0.1f, 0.2f, 0.3f, index * 0.001f)],
            });
        }
        List<ushort> indices = [];
        for (int index = 0; index + 2 < vertices.Count; index += 3)
        {
            indices.Add((ushort)index);
            indices.Add((ushort)(index + 1));
            indices.Add((ushort)(index + 2));
        }
        XRMesh mesh = new(vertices, indices);

        MeshletPayloadUtility.ComputeSourceMeshHash(mesh).ShouldBe(ReferenceSourceMeshHash(mesh));
    }

    // The per-value byte sequence that the staged hash must reproduce.
    private static ulong ReferenceSourceMeshHash(XRMesh mesh)
    {
        System.IO.Hashing.XxHash64 hash = new();
        void Int(int value) { byte[] b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, value); hash.Append(b); }
        void UInt(uint value) { byte[] b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, value); hash.Append(b); }
        void Bool(bool value) => hash.Append([value ? (byte)1 : (byte)0]);
        void Float(float value) => UInt(System.BitConverter.SingleToUInt32Bits(value));

        Int(MeshletPayloadUtility.SourceMeshHashVersion);
        Int(mesh.VertexCount);
        Int((int)mesh.Type);
        Bool(mesh.HasNormals);
        Bool(mesh.HasTangents);
        UInt(mesh.TexCoordCount);
        UInt(mesh.ColorCount);
        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        Int(indices.Length);
        foreach (int index in indices)
            Int(index);
        for (uint vertex = 0; vertex < (uint)mesh.VertexCount; ++vertex)
        {
            Vector3 position = mesh.GetPosition(vertex);
            Vector3 normal = mesh.GetNormal(vertex);
            Vector4 tangent = mesh.GetTangentWithSign(vertex);
            Float(position.X); Float(position.Y); Float(position.Z);
            Float(normal.X); Float(normal.Y); Float(normal.Z);
            Float(tangent.X); Float(tangent.Y); Float(tangent.Z); Float(tangent.W);
            Bool(false);
            for (uint set = 0; set < mesh.TexCoordCount; ++set)
            {
                Vector2 uv = mesh.GetTexCoord(vertex, set);
                Float(uv.X); Float(uv.Y);
            }
            for (uint set = 0; set < mesh.ColorCount; ++set)
            {
                Vector4 color = mesh.GetColor(vertex, set);
                Float(color.X); Float(color.Y); Float(color.Z); Float(color.W);
            }
        }
        return hash.GetCurrentHashAsUInt64();
    }

    private void PublishOne(XRMesh mesh, string identity, MeshletGenerationSettings settings)
    {
        ModelImportMeshletPayloadCache cache = ModelImportMeshletPayloadCache.TryOpen(_root, ModelIdentity)!;
        cache.TryAttach(mesh, identity, settings, null).ShouldBeFalse();
        cache.Record(mesh.GetOrCreateMeshletPayload(settings, null, identity));
        cache.SaveIfChanged().ShouldBeTrue();
    }

    private static MeshletGenerationSettings CreateSettings(uint maxTriangles = 124u)
        => new()
        {
            Enabled = true,
            BuildMode = MeshletBuildMode.Dense,
            MaxVertices = 64u,
            MaxTriangles = maxTriangles,
            OptimizeMeshlets = true,
            ComputeBounds = true,
        };

    private static XRMesh CreateGrid(float offset)
    {
        List<Vector3> positions = new(360);
        for (int index = 0; index < 120; index++)
        {
            float x = index % 24 + offset;
            float y = index / 24;
            positions.Add(new Vector3(x, y, 0.0f));
            positions.Add(new Vector3(x + 0.4f, y, 0.0f));
            positions.Add(new Vector3(x, y + 0.4f, 0.0f));
        }

        return XRMesh.CreateTriangles(positions);
    }
}
