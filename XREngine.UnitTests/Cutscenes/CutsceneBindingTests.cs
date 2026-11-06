using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Core.Files;
using XREngine.Cutscenes;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Cutscenes;

[TestFixture]
public sealed class CutsceneBindingTests
{
    private sealed class FakeResolver : ICutsceneBindingResolver
    {
        public readonly Dictionary<Guid, SceneNode> ById = [];
        public readonly Dictionary<string, SceneNode> ByPath = [];
        public readonly Dictionary<(Guid, Guid), SceneNode> ByPrefab = [];
        public readonly List<SceneNode> Spawned = [];
        public bool SpawnFails;

        public SceneNode? FindNodeById(Guid id) => ById.GetValueOrDefault(id);

        public SceneNode? FindNodeByPath(string path) => ByPath.GetValueOrDefault(path);

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId)
            => ByPrefab.GetValueOrDefault((prefabAssetId, prefabNodeId));

        public SceneNode? InstantiatePrefab(Guid prefabAssetId)
        {
            if (SpawnFails)
                return null;
            SceneNode node = new("Spawned", new Transform());
            Spawned.Add(node);
            return node;
        }
    }

    private static CutsceneAsset AssetWith(CutsceneBindingReference binding, ECutsceneFinishBehavior finish = ECutsceneFinishBehavior.RestoreState, bool forceRestoreAll = false)
    {
        CutsceneAsset asset = new() { Duration = CutsceneTime.FromSeconds(10), ForceRestoreAll = forceRestoreAll };
        asset.Bindings.Add(binding);
        asset.Tracks.Add(new TransformTrack { Name = "Move", BindingSlot = 0, WhenFinished = finish });
        asset.Tracks.Add(new ActivationTrack { Name = "Toggle", BindingSlot = 0, WhenFinished = finish });
        return asset;
    }

    private static CutsceneBindingTable Resolve(CutsceneAsset asset, FakeResolver resolver)
    {
        List<CutsceneCompileDiagnostic> compile = [];
        CompiledCutscene compiled = CutsceneCompiler.Compile(asset, compile);
        return CutsceneBindingTable.Resolve(compiled, resolver);
    }

    private static CutsceneBindingReference ById(Guid id, string? path = null)
        => new() { Kind = ECutsceneBindingKind.SceneObject, ObjectId = id, NodePath = path };

    [Test]
    public void SceneObject_ResolvesByGuid()
    {
        SceneNode node = new("Hero", new Transform());
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;

        using CutsceneBindingTable table = Resolve(AssetWith(ById(node.ID)), resolver);

        table.GetNode(0).ShouldBeSameAs(node);
        table.IsLive(0).ShouldBeTrue();
        table.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void SceneResolver_PrefersTheNodeUnderItsRoots_WhenTheCacheHoldsAnotherCopy()
    {
        // A play-mode world copy keeps the identifiers of the edit world, and the cache keeps the older node.
        SceneNode editRoot = new("Root", new Transform());
        SceneNode editNode = new(editRoot, "Hero");
        SceneNode playRoot = new("Root", new Transform());
        SceneNode playNode = new(playRoot, "Hero");
        playNode.ID = editNode.ID;
        try
        {
            XRObjectBase.ObjectsCache[editNode.ID].ShouldBeSameAs(editNode);

            CutsceneSceneBindingResolver playResolver = new(Engine.Assets, [playRoot]);
            CutsceneSceneBindingResolver editResolver = new(Engine.Assets, [editRoot]);

            playResolver.FindNodeById(editNode.ID).ShouldBeSameAs(playNode);
            editResolver.FindNodeById(editNode.ID).ShouldBeSameAs(editNode);
        }
        finally
        {
            playRoot.Destroy(true);
            editRoot.Destroy(true);
        }
    }

    [Test]
    public void SceneResolver_ReturnsNull_WhenTheIdIsNotUnderItsRoots()
    {
        SceneNode otherRoot = new("Other", new Transform());
        SceneNode otherNode = new(otherRoot, "Hero");
        SceneNode root = new("Root", new Transform());
        try
        {
            new CutsceneSceneBindingResolver(Engine.Assets, [root]).FindNodeById(otherNode.ID).ShouldBeNull();
        }
        finally
        {
            otherRoot.Destroy(true);
            root.Destroy(true);
        }
    }

    [Test]
    public void SceneObject_RepairsByPath_WhenGuidIsAbsent_AndReportsIt()
    {
        SceneNode node = new("Hero", new Transform());
        FakeResolver resolver = new();
        resolver.ByPath["Root/Hero"] = node;

        using CutsceneBindingTable table = Resolve(AssetWith(ById(Guid.NewGuid(), "Root/Hero")), resolver);

        table.GetNode(0).ShouldBeSameAs(node);
        table.Diagnostics.Count.ShouldBe(1);
        table.Diagnostics[0].Message.ShouldContain("Root/Hero");
    }

    [Test]
    public void PrefabNode_ResolvesThroughPrefabIds()
    {
        SceneNode node = new("Arm", new Transform());
        Guid prefab = Guid.NewGuid(), prefabNode = Guid.NewGuid();
        FakeResolver resolver = new();
        resolver.ByPrefab[(prefab, prefabNode)] = node;

        using CutsceneBindingTable table = Resolve(
            AssetWith(new CutsceneBindingReference { Kind = ECutsceneBindingKind.PrefabNode, PrefabAssetId = prefab, PrefabNodeId = prefabNode }),
            resolver);

        table.GetNode(0).ShouldBeSameAs(node);
    }

    [Test]
    public void UnresolvedSlot_GivesOneDiagnostic_AndNoNode()
    {
        FakeResolver resolver = new();

        using CutsceneBindingTable table = Resolve(AssetWith(ById(Guid.NewGuid())), resolver);

        for (int i = 0; i < 5; i++)
        {
            table.GetNode(0).ShouldBeNull();
            table.IsLive(0).ShouldBeFalse();
        }
        table.Diagnostics.Count.ShouldBe(1);
        table.Diagnostics[0].Message.ShouldContain("Move");
        table.Diagnostics[0].Message.ShouldContain("Toggle");
    }

    [Test]
    public void NodeDestroyedAfterResolve_IsNotLive_WithOneDiagnostic()
    {
        SceneNode node = new("Hero", new Transform());
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;
        using CutsceneBindingTable table = Resolve(AssetWith(ById(node.ID)), resolver);
        table.IsLive(0).ShouldBeTrue();

        node.Destroy(true);

        for (int i = 0; i < 5; i++)
        {
            table.IsLive(0).ShouldBeFalse();
            table.GetNode(0).ShouldBeNull();
        }
        table.Diagnostics.Count.ShouldBe(1);
    }

    [Test]
    public void Restore_PutsBackOriginalTransformAndActivation()
    {
        SceneNode node = new("Hero", new Transform());
        Transform transform = (Transform)node.Transform;
        transform.Translation = new Vector3(1, 2, 3);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        transform.Scale = new Vector3(2, 2, 2);
        Vector3 t0 = transform.Translation;
        Quaternion r0 = transform.Rotation;
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;
        using CutsceneBindingTable table = Resolve(AssetWith(ById(node.ID)), resolver);
        CutsceneStateSnapshot snapshot = new(table);

        snapshot.CaptureTransform(0);
        snapshot.CaptureActive(0);
        transform.Translation = new Vector3(9, 9, 9);
        transform.Rotation = Quaternion.Identity;
        transform.Scale = Vector3.One;
        node.IsActiveSelf = false;
        // A second capture must keep the first value.
        snapshot.CaptureTransform(0);
        snapshot.CaptureActive(0);

        snapshot.Restore(false, [ECutsceneFinishBehavior.RestoreState, ECutsceneFinishBehavior.RestoreState]);

        transform.Translation.ShouldBe(t0);
        transform.Rotation.ShouldBe(r0);
        transform.Scale.ShouldBe(new Vector3(2, 2, 2));
        node.IsActiveSelf.ShouldBeTrue();
    }

    [Test]
    public void KeepState_LeavesTheNodeAlone()
    {
        SceneNode node = new("Hero", new Transform());
        Transform transform = (Transform)node.Transform;
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;
        using CutsceneBindingTable table = Resolve(AssetWith(ById(node.ID), ECutsceneFinishBehavior.KeepState), resolver);
        CutsceneStateSnapshot snapshot = new(table);
        snapshot.CaptureTransform(0);
        snapshot.CaptureActive(0);
        transform.Translation = new Vector3(5, 5, 5);
        node.IsActiveSelf = false;

        snapshot.Restore(false, [ECutsceneFinishBehavior.KeepState, ECutsceneFinishBehavior.KeepState]);

        transform.Translation.ShouldBe(new Vector3(5, 5, 5));
        node.IsActiveSelf.ShouldBeFalse();
    }

    [Test]
    public void ForceRestoreAll_OverridesKeepState()
    {
        SceneNode node = new("Hero", new Transform());
        Transform transform = (Transform)node.Transform;
        FakeResolver resolver = new();
        resolver.ById[node.ID] = node;
        using CutsceneBindingTable table = Resolve(AssetWith(ById(node.ID), ECutsceneFinishBehavior.KeepState), resolver);
        CutsceneStateSnapshot snapshot = new(table);
        snapshot.CaptureTransform(0);
        snapshot.CaptureActive(0);
        transform.Translation = new Vector3(5, 5, 5);
        node.IsActiveSelf = false;

        snapshot.Restore(true, [ECutsceneFinishBehavior.KeepState, ECutsceneFinishBehavior.KeepState]);

        transform.Translation.ShouldBe(Vector3.Zero);
        node.IsActiveSelf.ShouldBeTrue();
    }

    [Test]
    public void Spawned_IsCreatedHiddenOnce_AndDestroyedAtClose()
    {
        FakeResolver resolver = new();
        CutsceneBindingTable table = Resolve(
            AssetWith(new CutsceneBindingReference { Kind = ECutsceneBindingKind.Spawned, PrefabAssetId = Guid.NewGuid() }),
            resolver);

        resolver.Spawned.Count.ShouldBe(1);
        SceneNode spawned = resolver.Spawned[0];
        spawned.IsActiveSelf.ShouldBeFalse();
        table.GetNode(0).ShouldBeSameAs(spawned);

        // Seeks only query the table. They make no new instance.
        for (int i = 0; i < 5; i++)
        {
            table.IsLive(0).ShouldBeTrue();
            table.GetNode(0);
        }
        resolver.Spawned.Count.ShouldBe(1);

        table.Dispose();

        spawned.IsDestroyed.ShouldBeTrue();
        table.GetNode(0).ShouldBeNull();
    }

    [Test]
    public void Spawned_WithFailedInstantiate_GivesOneDiagnostic()
    {
        FakeResolver resolver = new() { SpawnFails = true };

        using CutsceneBindingTable table = Resolve(
            AssetWith(new CutsceneBindingReference { Kind = ECutsceneBindingKind.Spawned, PrefabAssetId = Guid.NewGuid() }),
            resolver);

        table.GetNode(0).ShouldBeNull();
        table.Diagnostics.Count.ShouldBe(1);
    }

    [Test]
    public void EmptyRuntimeSlot_GivesOneDiagnostic_UntilFilled()
    {
        FakeResolver resolver = new();
        using CutsceneBindingTable table = Resolve(
            AssetWith(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "Player" }),
            resolver);

        table.GetNode(0).ShouldBeNull();
        table.IsLive(0).ShouldBeFalse();
        table.Diagnostics.Count.ShouldBe(1);
        table.Diagnostics[0].Message.ShouldContain("Player");

        SceneNode player = new("Player", new Transform());
        table.SetRuntimeSlot("Player", player);

        table.GetNode(0).ShouldBeSameAs(player);
        table.IsLive(0).ShouldBeTrue();
        table.Diagnostics.Count.ShouldBe(1);
    }

    [Test]
    public void SceneNodeId_SurvivesYamlSaveAndLoad()
    {
        SceneNode root = new("Root", new Transform());
        SceneNode child = new(root, "Child", new Transform());

        string yaml = AssetManager.Serializer.Serialize(root);
        SceneNode? clone = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);

        clone.ShouldNotBeNull();
        clone!.ID.ShouldBe(root.ID);
        clone.Transform.Children.Count.ShouldBe(1);
        clone.Transform.Children[0].SceneNode!.ID.ShouldBe(child.ID);
    }

    [Test]
    public void SceneNodeId_SurvivesCookedBinarySaveAndLoad()
    {
        SceneNode root = new("Root", new Transform());
        SceneNode child = new(root, "Child", new Transform());

        byte[] bytes = CookedBinarySerializer.Serialize(root);
        SceneNode? clone = CookedBinarySerializer.Deserialize(typeof(SceneNode), bytes) as SceneNode;

        clone.ShouldNotBeNull();
        clone!.ID.ShouldBe(root.ID);
        clone.Transform.Children[0].SceneNode!.ID.ShouldBe(child.ID);
    }
}
