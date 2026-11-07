using System.Buffers.Binary;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Tests the split of cooked sub-mesh LOD payloads, the residency of RenderableMesh LODs, and the mesh residency backend.
/// </summary>
[TestFixture]
public sealed class MeshLodResidencyTests
{
    private const float Lod0Distance = 10.0f;
    private const float Lod1Distance = 30.0f;
    private const float Lod2Distance = 1000.0f;

    private string _root = string.Empty;
    private CountingSource? _source;
    private IDisposable? _sourceRegistration;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "MeshLodResidency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _source = new CountingSource(new DirectoryLodPayloadSource(_root));
        _sourceRegistration = SubMeshLodPayloads.RegisterSource(_source);
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
    public void CookedSubMesh_GivesTheLowestLod_WithNoReadOfOtherPayloads()
    {
        SubMesh source = CreateSubMesh(out XRMesh[] meshes);
        byte[] cooked = Cook(source, out int writtenEntries);
        writtenEntries.ShouldBe(2);

        SubMesh loaded = Load(cooked, ELodPayloadLoadPolicy.ProxyFirst);

        _source!.OpenCount.ShouldBe(0);
        loaded.CookedCodecVersion.ShouldBe(SubMeshCookedCodec.CodecVersion);
        loaded.ID.ShouldBe(source.ID);
        loaded.Bounds.ShouldBe(source.Bounds);
        SubMeshLOD[] lods = [.. loaded.LODs];
        lods.Length.ShouldBe(3);
        lods[0].Mesh.ShouldBeNull();
        lods[0].IsPayloadDeferred.ShouldBeTrue();
        lods[1].Mesh.ShouldBeNull();
        lods[1].IsPayloadDeferred.ShouldBeTrue();
        lods[2].IsPayloadDeferred.ShouldBeFalse();
        lods[2].Mesh.ShouldNotBeNull().VertexCount.ShouldBe(meshes[2].VertexCount);
        lods[0].MaxVisibleDistance.ShouldBe(Lod0Distance);
        lods[0].CookedDescriptor.ShouldNotBeNull().IndexCount.ShouldBe((uint)meshes[0].IndexCount);
        loaded.HasNonResidentLods.ShouldBeTrue();
        // One material on all LODs stays one object after the load.
        lods[0].Material.ShouldNotBeNull().ShouldBeSameAs(lods[2].Material);
    }

    [Test]
    public void CookedSubMesh_LoadsEveryLod_WithTheDefaultPolicy()
    {
        SubMeshLodPayloads.DefaultLoadPolicy.ShouldBe(ELodPayloadLoadPolicy.All);
        SubMesh source = CreateSubMesh(out XRMesh[] meshes);
        byte[] cooked = Cook(source, out _);

        SubMesh loaded = CookedAssetReader.LoadAsset<SubMesh>(cooked).ShouldNotBeNull();

        _source!.OpenCount.ShouldBe(2);
        SubMeshLOD[] lods = [.. loaded.LODs];
        for (int i = 0; i < lods.Length; i++)
            lods[i].Mesh.ShouldNotBeNull().VertexCount.ShouldBe(meshes[i].VertexCount);
        loaded.HasNonResidentLods.ShouldBeFalse();
    }

    [Test]
    public void BadLodPayload_StaysNotResident_AndTheOtherLodsLoad()
    {
        SubMesh source = CreateSubMesh(out _);
        byte[] cooked = Cook(source, out _);
        string entry = Directory.GetFiles(Path.Combine(_root, "lod")).First();
        byte[] bytes = File.ReadAllBytes(entry);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(entry, bytes);

        SubMesh loaded = CookedAssetReader.LoadAsset<SubMesh>(cooked).ShouldNotBeNull();

        SubMeshLOD[] lods = [.. loaded.LODs];
        lods.Count(lod => lod.Mesh is null).ShouldBe(1);
        lods[2].Mesh.ShouldNotBeNull();
        SubMeshLOD broken = lods.Single(lod => lod.Mesh is null);
        SubMeshLodPayloads.TryLoadLod(broken.CookedDescriptor!, out XRMesh? mesh, out string? failure).ShouldBeFalse();
        mesh.ShouldBeNull();
        failure.ShouldNotBeNull().ShouldContain("checksum");
    }

    [Test]
    public void Writer_NeedsASink_ForMoreThanOneLod_AndReaderRejectsANewerVersion()
    {
        SubMesh source = CreateSubMesh(out _);
        Should.Throw<InvalidOperationException>(() => PublishedCookedAssetRegistry.TrySerialize(source, out _));

        SubMesh single = new(new SubMeshLOD(new XRMaterial(), CreateMesh(2, 0.0f), 0.0f));
        PublishedCookedAssetRegistry.TrySerialize(single, out byte[] payload).ShouldBeTrue();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 3u);
        Should.Throw<InvalidDataException>(() => CookedAssetReader.LoadAsset<SubMesh>(Envelope(payload)));
    }

    [Test]
    public void BakeProxySize_IsThePayloadLengthOfTheLowestLod()
    {
        SubMesh source = CreateSubMesh(out _);
        long editorBytes = SubMeshCookedCodec.GetCoarsestLodPayloadLength(source);
        SubMesh loaded = Load(Cook(source, out _), ELodPayloadLoadPolicy.ProxyFirst);

        long cookedBytes = SubMeshCookedCodec.GetCoarsestLodPayloadLength(loaded);

        cookedBytes.ShouldBeGreaterThan(0L);
        editorBytes.ShouldBe(cookedBytes);
        SceneNode node = new("Model");
        try
        {
            node.AddComponent<ModelComponent>()!.Model = new Model(loaded);
            List<CutsceneResidencyAsset> assets = [];
            CutsceneRenderableAssetCollector.Instance.Collect(node, assets);
            assets.Single(asset => asset.Kind == EResidencyAssetKind.Mesh).ProxyBytes.ShouldBe(cookedBytes);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void UpdateLod_SelectsTheNearestResidentLod_ForAnyDistance()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        (SceneNode node, RenderableMesh renderable) = Spawn(loaded);
        try
        {
            renderable.LodTableCount.ShouldBe(3);
            renderable.LODs.Count.ShouldBe(1);
            renderable.GetLodResidency(0).ShouldBe(EResidencyLevel.None);
            renderable.GetLodResidency(2).ShouldBe(EResidencyLevel.Proxy);
            foreach (float distance in new[] { 0.0f, 5.0f, 20.0f, 500.0f, 50000.0f, 0.0f })
            {
                renderable.UpdateLOD(distance);
                renderable.CurrentLodIndex.ShouldBe(2);
            }

            // Make LOD 0 resident. LOD 1 is not, so a distance that requests it takes the nearest resident LOD.
            SubMeshLOD lod0 = loaded.LODs.First();
            SubMeshLodPayloads.TryLoadLod(lod0.CookedDescriptor!, out XRMesh? mesh, out _).ShouldBeTrue();
            lod0.Mesh = mesh;
            renderable.GetLodResidency(0).ShouldBe(EResidencyLevel.Target);
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(0);
            SettleLod(renderable, 20.0f);
            renderable.CurrentLodIndex.ShouldBe(2);
            SettleLod(renderable, 50000.0f);
            renderable.CurrentLodIndex.ShouldBe(2);

            renderable.ForcedMinimumLod = 1;
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(2);
            renderable.ForcedMinimumLod = null;
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(0);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void AllResidentSubMesh_KeepsTheSelectionOfBeforeThePayloadSplit()
    {
        SubMesh editor = CreateSubMesh(out _);
        (SceneNode node, RenderableMesh renderable) = Spawn(editor);
        try
        {
            renderable.LODs.Count.ShouldBe(3);
            renderable.CurrentLodIndex.ShouldBe(0);
            renderable.UpdateLOD(20.0f);
            renderable.CurrentLodIndex.ShouldBe(1);
            renderable.UpdateLOD(5000.0f);
            renderable.CurrentLodIndex.ShouldBe(2);
            // The walk back moves one LOD for each update, as before.
            renderable.UpdateLOD(0.0f);
            renderable.CurrentLodIndex.ShouldBe(1);
            renderable.UpdateLOD(0.0f);
            renderable.CurrentLodIndex.ShouldBe(0);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void CompletedLod_BecomesSelectable_AtTheNextFrame()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        (SceneNode node, RenderableMesh renderable) = Spawn(loaded);
        ManualScheduler scheduler = new();
        using MeshResidencyBackend backend = new([node], scheduler);
        try
        {
            Guid key = CutsceneResidencyAssetKeys.ForSubMesh(node, 0, 0);
            backend.CanResolve(key).ShouldBeTrue();
            backend.GetLevel(key).ShouldBe(EResidencyLevel.Proxy);

            ResidencyTicket proxy = backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Proxy, default, 1.0f, 1));
            proxy.IsValid.ShouldBeTrue();
            scheduler.Pending.ShouldBe(0);

            int versionBefore = renderable.LodRegistrationVersion;
            ResidencyTicket target = backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, default, 1.0f, 1));
            target.IsValid.ShouldBeTrue();
            scheduler.Pending.ShouldBe(1);
            scheduler.RunAll();

            // The load is prepared on the job thread, but nothing is published before the frame boundary.
            renderable.IsLodResident(0).ShouldBeFalse();
            backend.GetLevel(key).ShouldBe(EResidencyLevel.Proxy);
            renderable.LodRegistrationVersion.ShouldBe(versionBefore);

            backend.SetPlayhead(default, 1.0);

            renderable.IsLodResident(0).ShouldBeTrue();
            backend.GetLevel(key).ShouldBe(EResidencyLevel.Target);
            backend.PublishedLodCount.ShouldBe(1);
            renderable.LodRegistrationVersion.ShouldBeGreaterThan(versionBefore);
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(0);

            // A cancel of the complete ticket releases the LOD: it leaves the selectable set, and the proxy stays.
            backend.Cancel(target);
            renderable.IsLodResident(0).ShouldBeFalse();
            renderable.CurrentLodIndex.ShouldBe(2);
            backend.GetLevel(key).ShouldBe(EResidencyLevel.Proxy);
            loaded.LODs.First().Mesh.ShouldBeNull();
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void CanceledLoad_LeavesTheStateUnchanged()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        (SceneNode node, RenderableMesh renderable) = Spawn(loaded);
        ManualScheduler scheduler = new();
        using MeshResidencyBackend backend = new([node], scheduler);
        try
        {
            Guid key = CutsceneResidencyAssetKeys.ForSubMesh(node, 0, 0);
            int versionBefore = renderable.LodRegistrationVersion;

            // A cancel while the job runs.
            ResidencyTicket running = backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, default, 1.0f, 1));
            backend.Cancel(running);
            scheduler.RunAll();
            backend.PublishPending();
            AssertProxyOnly();

            // A cancel after the job prepared the mesh, before the frame boundary.
            ResidencyTicket prepared = backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Coarse, default, 1.0f, 2));
            scheduler.RunAll();
            backend.Cancel(prepared);
            backend.PublishPending();
            AssertProxyOnly();

            void AssertProxyOnly()
            {
                renderable.LODs.Count.ShouldBe(1);
                renderable.CurrentLodIndex.ShouldBe(2);
                renderable.LodRegistrationVersion.ShouldBe(versionBefore);
                backend.GetLevel(key).ShouldBe(EResidencyLevel.Proxy);
                backend.PublishedLodCount.ShouldBe(0);
                loaded.LODs.Count(lod => lod.Mesh is null).ShouldBe(2);
            }
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void OldCookedSubMesh_LoadsWithAllLodsResident()
    {
        SubMesh source = CreateSubMesh(out XRMesh[] meshes);
        byte[] generic;
#pragma warning disable IL2026, IL3050 // The test writes the older generic cooked payload.
        generic = CookedBinarySerializer.Serialize(source);
#pragma warning restore IL2026, IL3050
        byte[] cooked = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            CookedAssetTypeReference.Encode(typeof(SubMesh)), CookedAssetFormat.BinaryV2, generic));

        SubMesh loaded = Load(cooked, ELodPayloadLoadPolicy.ProxyFirst);

        loaded.CookedCodecVersion.ShouldBe(1);
        SubMeshLOD[] lods = [.. loaded.LODs];
        lods.Length.ShouldBe(3);
        for (int i = 0; i < lods.Length; i++)
        {
            lods[i].IsPayloadDeferred.ShouldBeFalse();
            lods[i].Mesh.ShouldNotBeNull().VertexCount.ShouldBe(meshes[i].VertexCount);
        }

        (SceneNode node, RenderableMesh renderable) = Spawn(loaded);
        ManualScheduler scheduler = new();
        using MeshResidencyBackend backend = new([node], scheduler);
        try
        {
            renderable.LODs.Count.ShouldBe(3);
            Guid key = CutsceneResidencyAssetKeys.ForSubMesh(node, 0, 0);
            backend.LegacySubMeshCount.ShouldBe(1);
            backend.GetLevel(key).ShouldBe(EResidencyLevel.Target);
            ResidencyTicket ticket = backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, default, 1.0f, 1));
            ticket.IsValid.ShouldBeTrue();
            scheduler.Pending.ShouldBe(0);
            backend.Cancel(ticket);
            renderable.LODs.Count.ShouldBe(3);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void BakeCheck_ReportsSingleLodAndUnreducedProxies()
    {
        SceneNode node = new("Models");
        try
        {
            SubMesh single = new(new SubMeshLOD(null, CreateMesh(4, 0.0f), 0.0f)) { Name = "Single" };
            SubMesh unreduced = new(
                new SubMeshLOD(null, CreateMesh(10, 0.0f), Lod0Distance),
                new SubMeshLOD(null, CreateMesh(8, 0.0f), Lod2Distance)) { Name = "Unreduced" };
            node.AddComponent<ModelComponent>()!.Model = new Model(single, unreduced, CreateSubMesh(out _));
            List<CutsceneCompileDiagnostic> diagnostics = [];

            CutsceneMeshLodBakeCheck.Check([node], diagnostics);

            diagnostics.Count.ShouldBe(2);
            diagnostics[0].Message.ShouldStartWith("1 sub-meshes of the cutscene have one LOD");
            diagnostics[1].Message.ShouldStartWith("1 sub-meshes of the cutscene have a lowest LOD with more than");
            diagnostics[1].Message.ShouldContain("'Unreduced'");
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    private static void SettleLod(RenderableMesh renderable, float distance)
    {
        // The requested LOD walks back one step for each update, as before the payload split.
        for (int i = 0; i < 4; i++)
            renderable.UpdateLOD(distance);
    }

    private static (SceneNode Node, RenderableMesh Renderable) Spawn(SubMesh subMesh)
    {
        SceneNode node = new("LodModel");
        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(subMesh);
        return (node, component.Meshes.Single());
    }

    private byte[] Cook(SubMesh subMesh, out int writtenEntries)
    {
        using SubMeshLodPayloadCookSink sink = SubMeshLodPayloadCookSink.Begin(_root);
        PublishedCookedAssetRegistry.TrySerialize(subMesh, out byte[] payload).ShouldBeTrue();
        writtenEntries = sink.WrittenEntryCount;
        return Envelope(payload);
    }

    private static byte[] Envelope(byte[] payload)
        => CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            CookedAssetTypeReference.Encode(typeof(SubMesh)), CookedAssetFormat.RuntimeBinaryV1, payload));

    private static SubMesh Load(byte[] cooked, ELodPayloadLoadPolicy policy)
    {
        using IDisposable scope = SubMeshLodPayloads.UseLoadPolicy(policy);
        return CookedAssetReader.LoadAsset<SubMesh>(cooked).ShouldNotBeNull();
    }

    private static SubMesh CreateSubMesh(out XRMesh[] meshes)
    {
        XRMaterial material = new() { Name = "LodMaterial" };
        meshes = [CreateMesh(32, 0.0f), CreateMesh(12, 0.1f), CreateMesh(4, 0.2f)];
        return new SubMesh(
            new SubMeshLOD(material, meshes[0], Lod0Distance),
            new SubMeshLOD(material, meshes[1], Lod1Distance),
            new SubMeshLOD(material, meshes[2], Lod2Distance))
        {
            Name = "LodSubMesh",
        };
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

    /// <summary>Counts the entries that the loads open.</summary>
    private sealed class CountingSource(ISubMeshLodPayloadSource inner) : ISubMeshLodPayloadSource
    {
        private int _openCount;

        public int OpenCount => Volatile.Read(ref _openCount);

        public bool TryOpen(string entryName, out CookedPayloadOwner? payload)
        {
            Interlocked.Increment(ref _openCount);
            return inner.TryOpen(entryName, out payload);
        }
    }

    /// <summary>Runs the queued load jobs only when the test asks.</summary>
    private sealed class ManualScheduler : TaskScheduler
    {
        private readonly List<Task> _tasks = [];

        public int Pending => _tasks.Count;

        public void RunAll()
        {
            while (_tasks.Count > 0)
            {
                Task task = _tasks[0];
                _tasks.RemoveAt(0);
                TryExecuteTask(task);
            }
        }

        protected override IEnumerable<Task> GetScheduledTasks() => _tasks;

        protected override void QueueTask(Task task) => _tasks.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}
