using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Tests the contract between mesh LOD residency and the GPU scene registration: deferred levels register as null levels in
/// LOD table order, the lowest LOD is the mandatory resident level, a published LOD uploads at the next registration, and an
/// evicted LOD leaves the LOD table before its atlas range is freed.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class GpuLodResidencyContractTests
{
    private string _root = string.Empty;
    private IDisposable? _sourceRegistration;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "GpuLodResidency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sourceRegistration = SubMeshLodPayloads.RegisterSource(new DirectoryLodPayloadSource(_root));
    }

    [TearDown]
    public void TearDown()
    {
        _sourceRegistration?.Dispose();
        _sourceRegistration = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void StreamOnDemand_KeepsTheLowestLodResident_NotLod0()
    {
        bool previous = RuntimeEngine.Rendering.Settings.StreamMeshLodsOnDemand;
        RuntimeEngine.Rendering.Settings.StreamMeshLodsOnDemand = true;
        try
        {
            GPUScene scene = new();
            XRMesh lod0 = CreateMesh(8, 0.0f);
            XRMesh lod1 = CreateMesh(4, 0.1f);
            XRMesh lod2 = CreateMesh(2, 0.2f);

            scene.RegisterLogicalMeshLODs([(lod0, 128.0f), (lod1, 64.0f), (lod2, 0.0f)], out uint logicalMeshId, out string? failure).ShouldBeTrue(failure);

            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
            entry.LODCount.ShouldBe(3u);
            entry.LOD0_MeshDataID.ShouldBe(0u);
            entry.LOD1_MeshDataID.ShouldBe(0u);
            entry.LOD2_MeshDataID.ShouldNotBe(0u);

            // The lowest level is the fallback, so it cannot be released. A finer level can load and release.
            scene.ReleaseLOD(logicalMeshId, 2, out string? releaseFailure).ShouldBeFalse();
            releaseFailure.ShouldNotBeNull().ShouldContain("lowest");
            scene.RequestLODLoad(logicalMeshId, 0, out string? loadFailure).ShouldBeTrue(loadFailure);
            scene.ReleaseLOD(logicalMeshId, 0, out releaseFailure).ShouldBeTrue(releaseFailure);
        }
        finally
        {
            RuntimeEngine.Rendering.Settings.StreamMeshLodsOnDemand = previous;
        }
    }

    [Test]
    public void DeferredLods_RegisterAsNullLevels_InTableOrder()
    {
        (SceneNode node, RenderableMesh renderable, _) = SpawnProxyFirst();
        try
        {
            GPUScene scene = new();
            XRMesh commandMesh = renderable.CurrentLODMesh.ShouldNotBeNull();

            scene.ResolveRenderableLogicalMeshRegistration(renderable, commandMesh, 0, "Proxy", out uint meshId, out uint logicalMeshId, out uint lodCount, out string? failure)
                .ShouldBeTrue(failure);

            lodCount.ShouldBe(3u);
            meshId.ShouldNotBe(0u);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
            entry.LODCount.ShouldBe(3u);
            entry.LOD0_MeshDataID.ShouldBe(0u);
            entry.LOD1_MeshDataID.ShouldBe(0u);
            entry.LOD2_MeshDataID.ShouldBe(meshId);
            entry.LOD2_MinProjectedRadiusPixels.ShouldBe(0.0f);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void TableLongerThanTheLevelCapacity_RegistersTheInlineLodAsTheLastLevel()
    {
        (SceneNode node, RenderableMesh renderable, _) = SpawnProxyFirst(6);
        try
        {
            GPUScene scene = new();
            XRMesh commandMesh = renderable.CurrentLODMesh.ShouldNotBeNull();

            scene.ResolveRenderableLogicalMeshRegistration(renderable, commandMesh, 0, "Proxy", out uint meshId, out uint logicalMeshId, out uint lodCount, out string? failure)
                .ShouldBeTrue(failure);

            lodCount.ShouldBe((uint)GPUScene.MaxLogicalMeshLodCount);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
            entry.LOD0_MeshDataID.ShouldBe(0u);
            entry.LOD1_MeshDataID.ShouldBe(0u);
            entry.LOD2_MeshDataID.ShouldBe(0u);
            entry.LOD3_MeshDataID.ShouldBe(meshId);
            entry.LOD3_MinProjectedRadiusPixels.ShouldBe(0.0f);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void PublishedLod_UploadsAtTheNextRegistration()
    {
        (SceneNode node, RenderableMesh renderable, SubMesh subMesh) = SpawnProxyFirst();
        try
        {
            GPUScene scene = new();
            XRMesh commandMesh = renderable.CurrentLODMesh.ShouldNotBeNull();
            scene.ResolveRenderableLogicalMeshRegistration(renderable, commandMesh, 0, "Proxy", out _, out uint logicalMeshId, out _, out string? failure)
                .ShouldBeTrue(failure);
            int version = renderable.LodRegistrationVersion;

            // The mesh backend sets the source mesh at the frame boundary. The LOD is published and the version advances.
            subMesh.LODs.First().Mesh = CreateMesh(8, 0.0f);
            renderable.LodRegistrationVersion.ShouldBeGreaterThan(version);

            scene.ResolveRenderableLogicalMeshRegistration(renderable, commandMesh, 0, "Proxy", out _, out uint sameLogicalMeshId, out _, out failure)
                .ShouldBeTrue(failure);
            sameLogicalMeshId.ShouldBe(logicalMeshId);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
            entry.LOD0_MeshDataID.ShouldNotBe(0u);
            entry.LOD1_MeshDataID.ShouldBe(0u);
            entry.LOD2_MeshDataID.ShouldNotBe(0u);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void EvictedLod_LeavesTheTableFirst_AndTheAtlasAfterTheFramesInFlight()
    {
        (SceneNode node, RenderableMesh renderable, SubMesh subMesh) = SpawnProxyFirst();
        try
        {
            GPUScene scene = new();
            XRMesh commandMesh = renderable.CurrentLODMesh.ShouldNotBeNull();
            subMesh.LODs.First().Mesh = CreateMesh(8, 0.0f);
            scene.ResolveRenderableLogicalMeshRegistration(renderable, commandMesh, 0, "Proxy", out _, out uint logicalMeshId, out _, out string? failure)
                .ShouldBeTrue(failure);
            AcquireResidency(scene, logicalMeshId);
            XRMesh lod0Runtime = renderable.LODs.First!.Value.Renderer.Mesh.ShouldNotBeNull();
            scene.GetAtlasMeshReferenceCount(lod0Runtime).ShouldBe(1);

            // The mesh backend releases the payload: the LOD leaves the selectable set.
            subMesh.LODs.First().Mesh = null;
            scene.ResolveRenderableLogicalMeshRegistration(renderable, commandMesh, 0, "Proxy", out _, out _, out _, out failure)
                .ShouldBeTrue(failure);

            // The level ID is zero at once, but the atlas range stays for the frames that the GPU can still draw.
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
            entry.LOD0_MeshDataID.ShouldBe(0u);
            scene.GetAtlasMeshReferenceCount(lod0Runtime).ShouldBe(1);
            scene.PendingLodAtlasReleaseCount.ShouldBe(1);

            scene.RetirePendingLodAtlasReleasesAtFrameBoundary();
            scene.GetAtlasMeshReferenceCount(lod0Runtime).ShouldBe(1);

            scene.RetirePendingLodAtlasReleasesAtFrameBoundary();
            scene.GetAtlasMeshReferenceCount(lod0Runtime).ShouldBe(0);
            scene.PendingLodAtlasReleaseCount.ShouldBe(0);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void RequestForALevelWithNoCpuPayload_ReportsThePayloadAndLoadsNothing()
    {
        (SceneNode node, RenderableMesh renderable, _) = SpawnProxyFirst();
        try
        {
            GPUScene scene = new();
            XRMesh commandMesh = renderable.CurrentLODMesh.ShouldNotBeNull();
            scene.ResolveRenderableLogicalMeshRegistration(renderable, commandMesh, 0, "Proxy", out _, out uint logicalMeshId, out _, out string? failure)
                .ShouldBeTrue(failure);

            // The render side never loads a payload. The mesh residency backend owns that work.
            scene.RequestLODLoad(logicalMeshId, 0, out string? loadFailure).ShouldBeFalse();
            loadFailure.ShouldNotBeNull().ShouldContain("not resident");
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
            entry.LOD0_MeshDataID.ShouldBe(0u);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    private static void AcquireResidency(GPUScene scene, uint logicalMeshId)
        => typeof(GPUScene)
            .GetMethod("AcquireLogicalMeshResidency", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull()
            .Invoke(scene, [logicalMeshId]);

    private (SceneNode Node, RenderableMesh Renderable, SubMesh SubMesh) SpawnProxyFirst(int lodCount = 3)
    {
        XRMaterial material = new() { Name = "ProxyMaterial" };
        SubMeshLOD[] lods = new SubMeshLOD[lodCount];
        for (int i = 0; i < lodCount; i++)
            lods[i] = new SubMeshLOD(material, CreateMesh(2 * (lodCount - i), i * 0.1f), i == lodCount - 1 ? 1000.0f : 10.0f * (i + 1));
        SubMesh source = new(lods)
        {
            Name = "GpuLodSubMesh",
        };

        byte[] payload;
        using (SubMeshLodPayloadCookSink.Begin(_root))
            PublishedCookedAssetRegistry.TrySerialize(source, out payload).ShouldBeTrue();
        byte[] cooked = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            CookedAssetTypeReference.Encode(typeof(SubMesh)), CookedAssetFormat.RuntimeBinaryV1, payload));

        SubMesh loaded;
        using (SubMeshLodPayloads.UseLoadPolicy(ELodPayloadLoadPolicy.ProxyFirst))
            loaded = CookedAssetReader.LoadAsset<SubMesh>(cooked).ShouldNotBeNull();

        SceneNode node = new("GpuLodModel");
        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(loaded);
        RenderableMesh renderable = component.Meshes.Single();
        renderable.LodTableCount.ShouldBe(lodCount);
        renderable.IsLodResident(0).ShouldBeFalse();
        renderable.IsLodResident(lodCount - 1).ShouldBeTrue();
        return (node, renderable, loaded);
    }

    private static XRMesh CreateMesh(int triangles, float offset)
    {
        Vector3[] positions = new Vector3[triangles * 3];
        for (int i = 0; i < triangles; i++)
        {
            float x = i + offset;
            positions[i * 3] = new Vector3(x, 0.0f, 0.0f);
            positions[(i * 3) + 1] = new Vector3(x + 1.0f, 0.0f, 0.0f);
            positions[(i * 3) + 2] = new Vector3(x, 1.0f, 0.0f);
        }
        XRMesh mesh = XRMesh.CreateTriangles(positions);
        mesh.Name = $"Mesh{triangles}";
        return mesh;
    }
}
