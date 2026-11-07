using System.Buffers.Binary;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Rendering.Residency;

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
        // A late LOD is ready at once unless a test changes the probe or the scheduler.
        RenderableMesh.LateLodReadinessScheduler = RunUntilComplete;
        RenderableMesh.LateLodReadinessProbe = _ => true;
    }

    [TearDown]
    public void TearDown()
    {
        RenderableMesh.LateLodReadinessScheduler = null;
        RenderableMesh.LateLodReadinessProbe = RenderableMesh.DefaultLateLodReadinessProbe;
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
    public void BrowserCookOfAStandaloneSubMesh_UsesTheInlineFallback_AndReaderRejectsANewerVersion()
    {
        // A browser cook opens no sink, so the registered codec declines a multi-LOD sub-mesh, and the cook writes it with
        // the generic serializer with every LOD inline.
        XRMaterial material = new() { Name = "BrowserMaterial" };
        SubMesh source = CreateSubMesh(out XRMesh[] meshes, material, offset: 0.0f);
        PublishedCookedAssetRegistry.Accepts(source).ShouldBeFalse();
        PublishedCookedAssetRegistry.TrySerialize(source, out _).ShouldBeFalse();
        PublishedCookedAssetRegistry.TryGetDependencies(source, out IReadOnlyList<PublishedCookedAssetDependency>? dependencies).ShouldBeTrue();
        dependencies.ShouldNotBeNull().ShouldBeEmpty();

        HashSet<object> walked = new(ReferenceEqualityComparer.Instance);
        CookedBinarySerializationCallbacks callbacks = new()
        {
            OnSerializingValue = value =>
            {
                if (value is not null)
                    walked.Add(value);
                return value;
            },
        };
        CookedAssetBlob blob = XREngine.Editor.ProjectBuilder.CreateCookedBlob(source, callbacks: callbacks);

        blob.Format.ShouldBe(CookedAssetFormat.BinaryV2);
        walked.ShouldContain(material);
        foreach (XRMesh mesh in meshes)
            walked.ShouldContain(mesh);
        Directory.Exists(Path.Combine(_root, "lod")).ShouldBeFalse();
        SubMesh loaded = Load(CookedAssetEnvelope.Serialize(blob), ELodPayloadLoadPolicy.ProxyFirst);
        _source!.OpenCount.ShouldBe(0);
        SubMeshLOD[] lods = [.. loaded.LODs];
        lods.Length.ShouldBe(3);
        for (int i = 0; i < lods.Length; i++)
        {
            lods[i].IsPayloadDeferred.ShouldBeFalse();
            lods[i].Mesh.ShouldNotBeNull().VertexCount.ShouldBe(meshes[i].VertexCount);
            lods[i].Material.ShouldBeSameAs(lods[0].Material);
        }

        // With a sink open the registered codec writes the split form.
        using (SubMeshLodPayloadCookSink.Begin(_root))
            PublishedCookedAssetRegistry.Accepts(source).ShouldBeTrue();

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
    public void LateLodReadiness_ChecksAgainWhileNotReady_ThenSelectsAndRegistersOnce()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        (SceneNode node, RenderableMesh renderable) = Spawn(loaded);
        var steps = new List<Func<bool>>();
        int probeCalls = 0;
        bool ready = false;
        try
        {
            RenderableMesh.LateLodReadinessScheduler = steps.Add;
            RenderableMesh.LateLodReadinessProbe = _ => { probeCalls++; return ready; };
            SubMeshLOD lod0 = loaded.LODs.First();
            SubMeshLodPayloads.TryLoadLod(lod0.CookedDescriptor!, out XRMesh? mesh, out _).ShouldBeTrue();
            lod0.Mesh = mesh;
            steps.Count.ShouldBe(1);
            int versionAtPublish = renderable.LodRegistrationVersion;

            // Coroutine contract: false runs the step again at the next dispatch.
            steps[0]().ShouldBeFalse();
            steps[0]().ShouldBeFalse();
            probeCalls.ShouldBe(2);
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(2);
            renderable.LodRegistrationVersion.ShouldBe(versionAtPublish);
            var meshes = new XRMesh?[3];
            var radii = new float[3];
            renderable.CollectLodMeshes(0u, meshes, radii).ShouldBe(3);
            meshes[0].ShouldBeNull();

            // True completes the coroutine after one registration advance.
            ready = true;
            steps[0]().ShouldBeTrue();
            renderable.LodRegistrationVersion.ShouldBe(versionAtPublish + 1);
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(0);
            renderable.CollectLodMeshes(0u, meshes, radii).ShouldBe(3);
            meshes[0].ShouldNotBeNull();
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void LateLodReadiness_EndsWhenTheLodIsEvicted()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        (SceneNode node, RenderableMesh renderable) = Spawn(loaded);
        var steps = new List<Func<bool>>();
        int probeCalls = 0;
        try
        {
            RenderableMesh.LateLodReadinessScheduler = steps.Add;
            RenderableMesh.LateLodReadinessProbe = _ => { probeCalls++; return false; };
            SubMeshLOD lod0 = loaded.LODs.First();
            SubMeshLodPayloads.TryLoadLod(lod0.CookedDescriptor!, out XRMesh? mesh, out _).ShouldBeTrue();
            lod0.Mesh = mesh;
            steps[0]().ShouldBeFalse();

            lod0.Mesh = null;
            steps[0]().ShouldBeTrue();
            probeCalls.ShouldBe(1);
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(2);
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void LateLodReadiness_UnderACoroutineScheduler_RunsUntilReady()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        (SceneNode node, RenderableMesh renderable) = Spawn(loaded);
        int probeCalls = 0;
        int dispatches = 0;
        try
        {
            // A scheduler with the engine contract: run the step until it gives true.
            RenderableMesh.LateLodReadinessScheduler = step =>
            {
                do
                    dispatches++;
                while (!step() && dispatches < 100);
            };
            RenderableMesh.LateLodReadinessProbe = _ => ++probeCalls >= 3;
            int versionBefore = renderable.LodRegistrationVersion;
            SubMeshLOD lod0 = loaded.LODs.First();
            SubMeshLodPayloads.TryLoadLod(lod0.CookedDescriptor!, out XRMesh? mesh, out _).ShouldBeTrue();
            lod0.Mesh = mesh;

            dispatches.ShouldBe(3);
            probeCalls.ShouldBe(3);
            // One advance for the publication and one for the readiness.
            renderable.LodRegistrationVersion.ShouldBe(versionBefore + 2);
            SettleLod(renderable, 0.0f);
            renderable.CurrentLodIndex.ShouldBe(0);
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
        byte[] generic = CreateLegacyGenericPayload(source);
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

    [Test]
    public void GenericSnapshot_KeepsANonResidentLod_AndTheBackendLoadsIt()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        byte[] snapshot = GenericSerialize(loaded);

        SubMesh restored;
        using (SubMeshLodPayloads.UseLoadPolicy(ELodPayloadLoadPolicy.ProxyFirst))
            restored = GenericDeserialize<SubMesh>(snapshot);

        restored.CookedCodecVersion.ShouldBe(SubMeshCookedCodec.CodecVersion);
        SubMeshLOD[] lods = [.. restored.LODs];
        lods.Length.ShouldBe(3);
        lods[0].Mesh.ShouldBeNull();
        lods[0].IsPayloadDeferred.ShouldBeTrue();
        lods[0].CookedDescriptor.ShouldBe(loaded.LODs.First().CookedDescriptor);
        lods[2].Mesh.ShouldNotBeNull();

        (SceneNode node, RenderableMesh renderable) = Spawn(restored);
        ManualScheduler scheduler = new();
        using MeshResidencyBackend backend = new([node], scheduler);
        try
        {
            // The restored LOD has no renderer, so the renderer never selects an empty mesh.
            renderable.LODs.Count.ShouldBe(1);
            Guid key = CutsceneResidencyAssetKeys.ForSubMesh(node, 0, 0);
            backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, default, 1.0f, 1)).IsValid.ShouldBeTrue();
            scheduler.RunAll();
            backend.PublishPending();
            renderable.IsLodResident(0).ShouldBeTrue();
            backend.GetLevel(key).ShouldBe(EResidencyLevel.Target);
        }
        finally
        {
            node.Destroy(now: true);
        }

        // A restore with the default policy loads the separate LODs, as a cooked load does.
        SubMesh restoredAll = GenericDeserialize<SubMesh>(snapshot);
        restoredAll.LODs.All(lod => lod.Mesh is not null).ShouldBeTrue();
    }

    [Test]
    public void GenericSnapshot_OfAnEditorSubMesh_KeepsVersionZero()
    {
        SubMesh editor = CreateSubMesh(out _);
        SubMesh restored = GenericDeserialize<SubMesh>(GenericSerialize(editor));

        restored.CookedCodecVersion.ShouldBe(0);
        restored.LODs.All(lod => lod.Mesh is not null && lod.CookedDescriptor is null).ShouldBeTrue();
    }

    [Test]
    public void StandaloneAndModelCodecs_RoundTripEveryAuthoredField()
    {
        SubMesh source = CreateAuthoredSubMesh();
        SubMesh standalone = Load(Cook(source, out _), ELodPayloadLoadPolicy.All);
        AssertAuthoredFields(source, standalone);

        Model model = new(source);
        Model cookedModel = LoadModel(CookModel(model, withSink: true, out _), ELodPayloadLoadPolicy.All);
        AssertAuthoredFields(source, cookedModel.Meshes.Single());

        Model snapshotModel = GenericDeserialize<Model>(GenericSerialize(model));
        AssertAuthoredFields(source, snapshotModel.Meshes.Single());
    }

    [Test]
    public void CookedModel_SplitsLodPayloads_AndSharesMaterialsAndSubMeshes()
    {
        XRMaterial shared = new() { Name = "SharedMaterial" };
        SubMesh first = CreateSubMesh(out _, shared, offset: 0.0f);
        SubMesh second = CreateSubMesh(out _, shared, offset: 50.0f);
        Model model = new(first, second, first);

        byte[] cooked = CookModel(model, withSink: true, out int entries);
        entries.ShouldBe(4);

        Model proxyFirst = LoadModel(cooked, ELodPayloadLoadPolicy.ProxyFirst);
        _source!.OpenCount.ShouldBe(0);
        proxyFirst.Meshes.Count.ShouldBe(3);
        proxyFirst.Meshes[2].ShouldBeSameAs(proxyFirst.Meshes[0]);
        foreach (SubMesh subMesh in proxyFirst.Meshes)
        {
            subMesh.CookedCodecVersion.ShouldBe(SubMeshCookedCodec.CodecVersion);
            SubMeshLOD[] lods = [.. subMesh.LODs];
            lods[0].IsPayloadDeferred.ShouldBeTrue();
            lods[0].Mesh.ShouldBeNull();
            lods[1].Mesh.ShouldBeNull();
            lods[2].Mesh.ShouldNotBeNull();
        }

        // One material on all LODs of both sub-meshes stays one object.
        XRMaterial material = proxyFirst.Meshes[0].LODs.First().Material.ShouldNotBeNull();
        foreach (SubMesh subMesh in proxyFirst.Meshes)
            foreach (SubMeshLOD lod in subMesh.LODs)
                lod.Material.ShouldBeSameAs(material);

        Model all = LoadModel(cooked, ELodPayloadLoadPolicy.All);
        _source.OpenCount.ShouldBe(4);
        all.Meshes.SelectMany(subMesh => subMesh.LODs).All(lod => lod.Mesh is not null).ShouldBeTrue();

        // The same input cooks to the same bytes, and the equal LOD meshes keep their entries.
        CookModel(model, withSink: true, out int secondEntries).ShouldBe(cooked);
        secondEntries.ShouldBe(0);
    }

    [Test]
    public void ModelSnapshot_WithNoSink_WritesEveryLodInline()
    {
        XRMaterial shared = new() { Name = "SharedMaterial" };
        Model model = new(CreateSubMesh(out _, shared, offset: 0.0f), CreateSubMesh(out _, shared, offset: 50.0f));

        byte[] snapshot = CookModel(model, withSink: false, out int entries);
        entries.ShouldBe(0);
        Directory.Exists(Path.Combine(_root, "lod")).ShouldBeFalse();

        Model restored = LoadModel(snapshot, ELodPayloadLoadPolicy.ProxyFirst);
        _source!.OpenCount.ShouldBe(0);
        restored.Meshes.SelectMany(subMesh => subMesh.LODs).All(lod => lod.Mesh is not null && !lod.IsPayloadDeferred).ShouldBeTrue();
        restored.Meshes.All(subMesh => subMesh.CookedCodecVersion == 0).ShouldBeTrue();
        restored.Meshes[1].LODs.First().Material.ShouldBeSameAs(restored.Meshes[0].LODs.First().Material);
    }

    [Test]
    public void BrowserCookWalk_SeesEveryLodMeshAndMaterial()
    {
        // The browser cooker plans meshlets and projects materials from the values of the generic stream, with no sink.
        XRMaterial material = new() { Name = "WalkMaterial" };
        SubMesh subMesh = CreateSubMesh(out XRMesh[] meshes, material, offset: 0.0f);
        Model model = new(subMesh);
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        CookedBinarySerializationCallbacks callbacks = new()
        {
            OnSerializingValue = value =>
            {
                if (value is not null)
                    seen.Add(value);
                return value;
            },
        };

#pragma warning disable IL2026, IL3050 // The test runs the reflective cooked walk.
        _ = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.CalculateSize(model, callbacks));
#pragma warning restore IL2026, IL3050

        seen.ShouldContain(subMesh);
        seen.ShouldContain(material);
        foreach (XRMesh mesh in meshes)
            seen.ShouldContain(mesh);
    }

    [Test]
    public void LateSkinnedLod_CopiesWeights_TracksBones_AndResetsTheSeed()
    {
        SceneNode node = new("SkinnedModel");
        SceneNode boneNode = new(node, "Bone");
        TransformBase[] proxyBones = [node.Transform];
        TransformBase[] fullBones = [node.Transform, boneNode.Transform];
        XRMaterial material = new() { Name = "SkinMaterial" };
        SubMesh source = new(
            new SubMeshLOD(material, CreateSkinnedBlendshapeMesh("Full", fullBones, 6), Lod0Distance),
            new SubMeshLOD(material, CreateSkinnedBlendshapeMesh("Proxy", proxyBones, 2), Lod2Distance))
        {
            Name = "SkinnedSubMesh",
        };
        SubMesh loaded = Load(Cook(source, out _), ELodPayloadLoadPolicy.ProxyFirst);
        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(loaded);
        RenderableMesh renderable = component.Meshes.Single();
        try
        {
            XRMeshRenderer proxy = renderable.CurrentLODRenderer.ShouldNotBeNull();
            proxy.EnsureBlendshapeBuffers(logWarnings: false).ShouldBeTrue();
            proxy.SetBlendshapeWeightNormalized("Smile", 0.75f);
            proxy.GetBlendshapeWeightNormalized("Smile").ShouldBe(0.75f);
            int trackedBefore = renderable.TrackedSkinnedBoneCount;

            SubMeshLOD full = loaded.LODs.First();
            SubMeshLodPayloads.TryLoadLod(full.CookedDescriptor!, out XRMesh? fullMesh, out _).ShouldBeTrue();
            full.Mesh = fullMesh;

            XRMeshRenderer late = renderable.LODs.First!.Value.Renderer;
            late.ShouldNotBeSameAs(proxy);
            late.GetBlendshapeWeightNormalized("Smile").ShouldBe(0.75f);
            renderable.TrackedSkinnedBoneCount.ShouldBeGreaterThan(trackedBefore);
            renderable.IsAwaitingLateLodSeed(late).ShouldBeTrue();

            // Collect through the pre-collect callback (BeforeAdd), as a camera does. While the proxy is selected, the late
            // renderer still waits for its seed.
            RenderCommandCollection passes = new(new Dictionary<int, IComparer<RenderCommand>?>());
            bool Collect() => renderable.RenderInfo.PreCollectCommandsCallback!(renderable.RenderInfo, passes, null);
            renderable.ForcedMinimumLod = 1;
            Collect();
            renderable.CurrentLodIndex.ShouldBe(1);
            renderable.InitialRenderStateSeeded.ShouldBeTrue();
            renderable.IsAwaitingLateLodSeed(late).ShouldBeTrue();

            // The first collect that selects the late LOD resets the seed state and seeds again for that renderer.
            renderable.ForcedMinimumLod = null;
            for (int i = 0; i < 4 && renderable.CurrentLodIndex != 0; i++)
                Collect();
            renderable.CurrentLodIndex.ShouldBe(0);
            renderable.IsAwaitingLateLodSeed(late).ShouldBeFalse();
            renderable.InitialRenderStateSeeded.ShouldBeTrue();
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void HashMismatch_RejectsTheLod_WithOneWarning(bool boneSet)
    {
        SceneNode node = new("SkinnedModel");
        SceneNode boneNode = new(node, "Bone");
        TransformBase[] bones = [node.Transform, boneNode.Transform];
        SubMesh source = new(
            new SubMeshLOD(null, CreateSkinnedBlendshapeMesh("Full", bones, 6), Lod0Distance),
            new SubMeshLOD(null, CreateSkinnedBlendshapeMesh("Proxy", bones, 2), Lod2Distance))
        {
            Name = "MismatchSubMesh",
        };
        SubMesh loaded = Load(Cook(source, out _), ELodPayloadLoadPolicy.ProxyFirst);
        SubMeshLOD full = loaded.LODs.First();
        SubMeshLodDescriptor real = full.CookedDescriptor!;
        SubMeshLodDescriptor wrong = boneSet
            ? real with { BoneSetHash = real.BoneSetHash ^ 1UL }
            : real with { BlendshapeNameHash = real.BlendshapeNameHash ^ 1UL };

        SubMeshLodPayloads.TryLoadLod(wrong, out XRMesh? mesh, out string? failure).ShouldBeFalse();
        mesh.ShouldBeNull();
        failure.ShouldNotBeNull().ShouldContain(boneSet ? "bone set" : "blendshape names");

        full.CookedDescriptor = wrong;
        ModelComponent component = node.AddComponent<ModelComponent>()!;
        component.Model = new Model(loaded);
        RenderableMesh renderable = component.Meshes.Single();
        ManualScheduler scheduler = new();
        using MeshResidencyBackend backend = new([node], scheduler);
        try
        {
            Guid key = CutsceneResidencyAssetKeys.ForSubMesh(node, 0, 0);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ResidencyTicket ticket = backend.Request(new ResidencyRequest(key, (int)EResidencyLevel.Target, default, 1.0f, attempt + 1));
                scheduler.RunAll();
                backend.PublishPending();
                renderable.IsLodResident(0).ShouldBeFalse();
                backend.GetLevel(key).ShouldBe(EResidencyLevel.Proxy);
                backend.Cancel(ticket);
            }

            backend.FailedLoadCount.ShouldBe(2);
            backend.WarnedLodCount.ShouldBe(1);
            full.Mesh.ShouldBeNull();
        }
        finally
        {
            node.Destroy(now: true);
        }
    }

    [Test]
    public void Cook_OfANonResidentSplitLod_FailsAndNamesTheSubMesh()
    {
        SubMesh loaded = Load(Cook(CreateSubMesh(out _), out _), ELodPayloadLoadPolicy.ProxyFirst);
        string other = Path.Combine(_root, "Other");

        using (SubMeshLodPayloadCookSink.Begin(other))
        {
            InvalidDataException standalone = Should.Throw<InvalidDataException>(() => PublishedCookedAssetRegistry.TrySerialize(loaded, out _));
            standalone.Message.ShouldContain(loaded.Name!);
            InvalidDataException embedded = Should.Throw<InvalidDataException>(() => GenericSerialize(new Model(loaded)));
            embedded.Message.ShouldContain(loaded.Name!);
        }
        Directory.Exists(Path.Combine(other, "lod")).ShouldBeFalse();
    }

    [Test]
    public void ContentCook_FailsForASubMesh_AndFallsBackToYamlForOtherTypes()
    {
        string assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(assets);
        // A multi-LOD YAML sub-mesh does not load (the LODs sorted set has no comparer on read), so its cook fails.
        WriteAsset(Path.Combine(assets, "Lods.asset"), CreateSubMesh(out _));
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            XREngine.Editor.ProjectBuilder.PrepareCookedContentDirectoryForTests(assets, Path.Combine(_root, "Intermediate")));
        error.Message.ShouldContain("Lods.asset");

        // Any other type keeps the older fallback to its YAML source.
        File.Delete(Path.Combine(assets, "Lods.asset"));
        string brokenPath = Path.Combine(assets, "Broken.asset");
        string brokenYaml = $"__assetType: XREngine.UnitTests.NoSuchAssetType{Environment.NewLine}Value: 1{Environment.NewLine}";
        File.WriteAllText(brokenPath, brokenYaml);
        string cooked = XREngine.Editor.ProjectBuilder.PrepareCookedContentDirectoryForTests(assets, Path.Combine(_root, "Intermediate2"));
        File.ReadAllText(Path.Combine(cooked, "Broken.asset")).ShouldBe(brokenYaml);
    }

    [Test]
    public void AotCook_RejectsASubMesh_AtCookTime()
    {
        string assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(assets);
        SubMesh single = new(new SubMeshLOD(new XRMaterial(), CreateMesh(2, 0.0f), 0.0f)) { Name = "AotSubMesh" };
        WriteAsset(Path.Combine(assets, "AotSubMesh.asset"), single);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            XREngine.Editor.ProjectBuilder.PrepareCookedContentDirectoryForTests(assets, Path.Combine(_root, "Intermediate"), publishLauncherAsNativeAot: true));

        error.Message.ShouldContain("NativeAOT");
        error.Message.ShouldContain(nameof(SubMesh));
    }

    /// <summary>
    /// Makes a generic cooked payload in the older form, with no per-sub-mesh version marker: a stand-in object with the
    /// LOD list of the sub-mesh, whose type name is then changed to the sub-mesh type.
    /// </summary>
    private static byte[] CreateLegacyGenericPayload(SubMesh source)
    {
        LegacySubMeshShape shape = new() { Name = source.Name, CookedBinaryLODs = [.. source.LODs] };
#pragma warning disable IL2026, IL3050 // The test writes the older generic cooked payload.
        byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(shape));
#pragma warning restore IL2026, IL3050
        byte[] from = EncodeString(typeof(LegacySubMeshShape).AssemblyQualifiedName!);
        byte[] to = EncodeString(typeof(SubMesh).AssemblyQualifiedName!);
        int index = payload.AsSpan().IndexOf(from);
        index.ShouldBeGreaterThanOrEqualTo(0);
        return [.. payload.AsSpan(0, index), .. to, .. payload.AsSpan(index + from.Length)];

        static byte[] EncodeString(string text)
        {
            using MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                writer.Write(text);
            return stream.ToArray();
        }
    }

    /// <summary>The members of a sub-mesh that an older generic payload holds, with no version marker.</summary>
    private sealed class LegacySubMeshShape
    {
        public string? Name { get; set; }

        public List<SubMeshLOD> CookedBinaryLODs { get; set; } = [];
    }

    private static void WriteAsset(string path, XRAsset asset)
    {
        string yaml = AssetManager.Serializer.Serialize(asset);
        if (!yaml.Contains("__assetType:", StringComparison.Ordinal))
            yaml = $"__assetType: {asset.GetType().FullName}{Environment.NewLine}{yaml}";
        File.WriteAllText(path, yaml);
    }

    private static SubMesh CreateAuthoredSubMesh()
    {
        Transform rootBone = new() { Name = "RootBone" };
        Transform rootTransform = new() { Name = "RootTransform" };
        XRMaterial material = new() { Name = "AuthoredMaterial" };
        SubMesh subMesh = new(
            new SubMeshLOD(material, CreateMesh(16, 0.0f), Lod0Distance, 120.0f)
            {
                GenerateAsync = true,
                StableSortKey = "authored:lod0",
            },
            new SubMeshLOD(null, CreateMesh(8, 0.3f), Lod1Distance)
            {
                IsAutoGenerated = true,
                GeneratedTargetIndexRatio = 0.5f,
                GeneratedNormalizedError = 0.01f,
                GeneratedObjectSpaceError = 0.02f,
                StableSortKey = "authored:lod1",
            },
            new SubMeshLOD(material, CreateMesh(2, 0.6f), Lod2Distance)
            {
                GenerateAsync = true,
                StableSortKey = "authored:lod2",
            })
        {
            Name = "AuthoredSubMesh",
            OriginalPath = "game://Models/Authored.fbx",
            ImportedEntityIdentity = "authored-entity",
            ImportedEntityIdentityIsStable = true,
            CullingBounds = new XREngine.Data.Geometry.AABB(new Vector3(-2.0f), new Vector3(3.0f)),
            RootBone = rootBone,
            RootTransform = rootTransform,
            UseGpuMeshBvh = false,
            RealtimeGpuMeshBvhForSkinnedMeshes = false,
        };
        subMesh.MeshOptimizer.Meshlets.Enabled = !subMesh.MeshOptimizer.Meshlets.Enabled;
        return subMesh;
    }

    private static void AssertAuthoredFields(SubMesh expected, SubMesh actual)
    {
        actual.ID.ShouldBe(expected.ID);
        actual.Name.ShouldBe(expected.Name);
        actual.OriginalPath.ShouldBe(expected.OriginalPath);
        actual.ImportedEntityIdentity.ShouldBe(expected.ImportedEntityIdentity);
        actual.ImportedEntityIdentityIsStable.ShouldBe(expected.ImportedEntityIdentityIsStable);
        actual.Bounds.ShouldBe(expected.Bounds);
        actual.CullingBounds.ShouldBe(expected.CullingBounds);
        actual.RootBone.ShouldNotBeNull().EffectiveSerializedReferenceId.ShouldBe(expected.RootBone!.EffectiveSerializedReferenceId);
        actual.RootBone.Name.ShouldBe(expected.RootBone.Name);
        actual.RootTransform.ShouldNotBeNull().EffectiveSerializedReferenceId.ShouldBe(expected.RootTransform!.EffectiveSerializedReferenceId);
        actual.UseGpuMeshBvh.ShouldBe(expected.UseGpuMeshBvh);
        actual.RealtimeGpuMeshBvhForSkinnedMeshes.ShouldBe(expected.RealtimeGpuMeshBvhForSkinnedMeshes);
        actual.MeshOptimizer.Meshlets.Enabled.ShouldBe(expected.MeshOptimizer.Meshlets.Enabled);

        SubMeshLOD[] expectedLods = [.. expected.LODs];
        SubMeshLOD[] actualLods = [.. actual.LODs];
        actualLods.Length.ShouldBe(expectedLods.Length);
        for (int i = 0; i < expectedLods.Length; i++)
        {
            SubMeshLOD e = expectedLods[i];
            SubMeshLOD a = actualLods[i];
            a.MaxVisibleDistance.ShouldBe(e.MaxVisibleDistance);
            a.MinProjectedScreenRadiusPixels.ShouldBe(e.MinProjectedScreenRadiusPixels);
            a.GenerateAsync.ShouldBe(e.GenerateAsync);
            a.IsAutoGenerated.ShouldBe(e.IsAutoGenerated);
            a.GeneratedTargetIndexRatio.ShouldBe(e.GeneratedTargetIndexRatio);
            a.GeneratedNormalizedError.ShouldBe(e.GeneratedNormalizedError);
            a.GeneratedObjectSpaceError.ShouldBe(e.GeneratedObjectSpaceError);
            a.StableSortKey.ShouldBe(e.StableSortKey);
            (a.Material is null).ShouldBe(e.Material is null);
            a.Material?.Name.ShouldBe(e.Material!.Name);
            a.Mesh.ShouldNotBeNull().VertexCount.ShouldBe(e.Mesh!.VertexCount);
        }
        actualLods[0].Material.ShouldBeSameAs(actualLods[2].Material);
    }

    private static XRMesh CreateSkinnedBlendshapeMesh(string name, TransformBase[] bones, int triangles)
    {
        List<Vertex> vertices = [];
        List<ushort> indices = [];
        for (int t = 0; t < triangles; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                Vector3 position = new(t + (c == 1 ? 1.0f : 0.0f), c == 2 ? 1.0f : 0.0f, 0.0f);
                TransformBase bone = bones[vertices.Count % bones.Length];
                vertices.Add(new Vertex(position)
                {
                    Weights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(ReferenceEqualityComparer.Instance)
                    {
                        [bone] = (1.0f, Matrix4x4.Identity),
                    },
                    Blendshapes =
                    [
                        ("Smile", new XREngine.Data.Rendering.VertexData { Position = position + new Vector3(0.0f, 0.1f, 0.0f), Normal = Vector3.UnitZ }),
                    ],
                    Normal = Vector3.UnitZ,
                });
                indices.Add((ushort)indices.Count);
            }
        }

        XRMesh mesh = new(vertices, indices) { Name = name };
        mesh.RebuildSkinningBuffersFromVertices(vertices);
        mesh.BlendshapeNames = ["Smile"];
        mesh.RebuildBlendshapeBuffersFromVertices(vertices);
        return mesh;
    }

    private byte[] CookModel(Model model, bool withSink, out int writtenEntries)
    {
        SubMeshLodPayloadCookSink? sink = withSink ? SubMeshLodPayloadCookSink.Begin(_root) : null;
        try
        {
            byte[] payload = GenericSerialize(model);
            writtenEntries = sink?.WrittenEntryCount ?? 0;
            return CookedAssetEnvelope.Serialize(new CookedAssetBlob(
                CookedAssetTypeReference.Encode(typeof(Model)), CookedAssetFormat.BinaryV2, payload));
        }
        finally
        {
            sink?.Dispose();
        }
    }

    private static Model LoadModel(byte[] cooked, ELodPayloadLoadPolicy policy)
    {
        using IDisposable scope = SubMeshLodPayloads.UseLoadPolicy(policy);
        return CookedAssetReader.LoadAsset<Model>(cooked).ShouldNotBeNull();
    }

#pragma warning disable IL2026, IL3050 // The tests use the reflective cooked serializer, as snapshots, clones, and model cooks do.
    private static byte[] GenericSerialize(object value)
        => CookedBinarySerializer.Serialize(value);

    private static T GenericDeserialize<T>(byte[] payload) where T : class
        => CookedBinarySerializer.Deserialize(typeof(T), payload).ShouldBeOfType<T>();
#pragma warning restore IL2026, IL3050

    /// <summary>Runs a readiness step with the coroutine contract: again until it gives true.</summary>
    private static void RunUntilComplete(Func<bool> step)
    {
        for (int i = 0; i < 100 && !step(); i++)
        {
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
        => CreateSubMesh(out meshes, new XRMaterial { Name = "LodMaterial" }, 0.0f);

    private static SubMesh CreateSubMesh(out XRMesh[] meshes, XRMaterial material, float offset)
    {
        meshes = [CreateMesh(32, offset), CreateMesh(12, offset + 0.1f), CreateMesh(4, offset + 0.2f)];
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
