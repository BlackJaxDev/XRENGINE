using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.ModelCaching;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ModelCookOverrideSnapshotTests
{
    [Test]
    public void Build_IncludesOnlyAuthoredDifferences_WithoutConsultingModelSource()
    {
        using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
        SceneNode root = new("Root");
        SceneNode meshNode = new(root, "Mesh");
        ModelComponent component = meshNode.AddComponent<ModelComponent>().ShouldNotBeNull();
        SubMesh subMesh = new();
        component.Model = new Model(subMesh);
        XRPrefabSource prefab = new()
        {
            RootNode = root,
        };
        ModelCookSettings defaults = new();
        defaults.Meshlets.Enabled = subMesh.MeshOptimizer.Meshlets.Enabled;

        ModelCookOverrideSnapshot empty = ModelCookOverrideSnapshotBuilder.Build(prefab, defaults);
        empty.Entries.ShouldBeEmpty();

        subMesh.MeshOptimizer.Meshlets.Enabled = true;
        ModelCookOverrideSnapshot first = ModelCookOverrideSnapshotBuilder.Build(prefab, defaults);
        ModelCookOverrideSnapshot second = ModelCookOverrideSnapshotBuilder.Build(prefab, defaults);

        first.Entries.Count.ShouldBe(1);
        first.Entries[0].EntityKey.Value.ShouldBe(
            "fallback/Root[0]/Mesh[0]:model:0:submesh:0");
        first.Entries[0].EntityKey.IsStable.ShouldBeFalse();
        second.Hash.ShouldBe(first.Hash);
        second.CanonicalBytes.ToArray().ShouldBe(first.CanonicalBytes.ToArray());

        defaults.Meshlets.Enabled = true;
        ModelCookOverrideSnapshot matchesNewDefault =
            ModelCookOverrideSnapshotBuilder.Build(prefab, defaults);
        matchesNewDefault.Entries.ShouldBeEmpty();
    }

    [Test]
    public void Snapshot_SortsKeysAndRejectsDuplicates()
    {
        MeshOptimizerSubMeshSettings settings = new();
        ModelCookOverrideEntry second = new(
            new ImportedEntityKey("entity:b", isStable: true),
            settings);
        ModelCookOverrideEntry first = new(
            new ImportedEntityKey("entity:a", isStable: true),
            settings);

        ModelCookOverrideSnapshot snapshot = new([second, first]);

        snapshot.Entries
            .Select(static entry => entry.EntityKey.Value)
            .ShouldBe(["entity:a", "entity:b"]);
        Should.Throw<ArgumentException>(() => new ModelCookOverrideSnapshot([first, first]));
    }
}
