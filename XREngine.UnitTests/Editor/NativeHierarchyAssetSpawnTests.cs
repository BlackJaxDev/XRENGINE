using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks the shared asset spawn path for hierarchy drop targets.</summary>
[TestFixture, NonParallelizable]
public sealed class NativeHierarchyAssetSpawnTests
{
    private SceneNode[] _selection = [];

    [SetUp]
    public void SetUp()
    {
        _selection = Selection.SceneNodes;
        Selection.Clear();
        Undo.ClearHistory();
    }

    [TearDown]
    public void TearDown()
    {
        Selection.Clear();
        Undo.ClearHistory();
        XRObjectBase.ProcessPendingDestructions();
        Selection.SceneNodes = _selection;
    }

    [Test]
    public void PrefabSpawnsUnderAHiddenSceneParentWithoutJoiningTheWorld()
    {
        SceneNode root = new("Hidden root");
        XRScene scene = new("Hidden", root) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        XRPrefabSource prefab = new() { Name = "Prefab", RootNode = new SceneNode("Prefab root") };
        _ = new SceneNode(prefab.RootNode!, "Prefab child");
        try
        {
            Assert.That(root.World, Is.Null);
            Assert.That(IsValidDropSpawnTarget(world, root), Is.True);
            Assert.That(SpawnPrefabNode(world, root, prefab), Is.True);

            SceneNode instance = root.GetChild(0)!;
            Assert.That(instance.Name, Is.EqualTo("Prefab root"));
            Assert.That(instance.World, Is.Null);
            Assert.That(instance.GetChild(0)!.World, Is.Null);
            Assert.That(world.RootNodes, Does.Not.Contain(instance));
            Assert.That(scene.SceneRoots, Does.Not.Contain(instance));
            Assert.That(Selection.SceneNode, Is.SameAs(instance));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(instance.Parent, Is.Null);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(instance.Parent, Is.SameAs(root));
            Assert.That(instance.World, Is.Null);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void SpawnRejectsDestroyedAndForeignParents()
    {
        SceneNode foreign = new("Foreign");
        SceneNode destroyed = new("Destroyed");
        XRScene hidden = new("Hidden", destroyed) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", hidden));
        destroyed.Destroy(true);
        try
        {
            Assert.That(IsValidDropSpawnTarget(world, foreign), Is.False);
            Assert.That(IsValidDropSpawnTarget(world, destroyed), Is.False);
            Assert.That(IsValidDropSpawnTarget(world, null), Is.True);
        }
        finally { foreign.Destroy(true); }
    }

    private static bool IsValidDropSpawnTarget(RuntimeWorld world, SceneNode? parent)
        => (bool)Method(nameof(IsValidDropSpawnTarget)).Invoke(null, [world, parent])!;

    private static bool SpawnPrefabNode(RuntimeWorld world, SceneNode parent, XRPrefabSource prefab)
        => (bool)Method(nameof(SpawnPrefabNode)).Invoke(null, [world, parent, prefab, null])!;

    private static MethodInfo Method(string name)
        => typeof(EditorImGuiUI).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(EditorImGuiUI), name);
}
