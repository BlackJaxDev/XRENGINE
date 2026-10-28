using System.Numerics;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class HierarchySpawnTests
{
    private SceneNode[] _selection = [];

    [SetUp]
    public void SetUp()
    {
        Undo.ClearHistory();
        _selection = Selection.SceneNodes;
        Selection.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Undo.ClearHistory();
        Selection.SceneNodes = _selection;
    }

    [Test]
    public void RootSpawnJoinsTheSceneAndReplaysWithDirtyMarking()
    {
        SceneNode existing = new("Existing");
        XRScene scene = new("Scene", existing);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        SceneNode spawned = new("Spawned") { IsActiveSelf = false };
        try
        {
            scene.ClearDirty();
            Assert.That(HierarchySpawn.Place("Spawn Model", world, null, scene, spawned, preserveWorld: false), Is.True);
            Assert.That(scene.SceneRoots, Does.Contain(spawned));
            Assert.That(world.RootNodes, Does.Contain(spawned));
            Assert.That(world.CaptureHierarchyRootRoutes(spawned).Scenes, Is.EqualTo(new[] { scene }));
            Assert.That(spawned.IsActiveSelf, Is.True);
            Assert.That(Selection.SceneNode, Is.SameAs(spawned));
            Assert.That(scene.IsDirty, Is.True);

            scene.ClearDirty();
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(scene.SceneRoots, Does.Not.Contain(spawned));
            Assert.That(world.RootNodes, Does.Not.Contain(spawned));
            Assert.That(spawned.IsActiveSelf, Is.False);
            Assert.That(scene.IsDirty, Is.True);

            scene.ClearDirty();
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(scene.SceneRoots, Does.Contain(spawned));
            Assert.That(world.RootNodes, Does.Contain(spawned));
            Assert.That(spawned.IsActiveSelf, Is.True);
            Assert.That(scene.IsDirty, Is.True);
        }
        finally { spawned.Destroy(true); existing.Destroy(true); }
    }

    [Test]
    public void ChildSpawnKeepsTheRequestedWorldPosition()
    {
        SceneNode parent = new("Parent");
        XRScene scene = new("Scene", parent);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        ((XREngine.Scene.Transforms.Transform)parent.Transform).Translation = new Vector3(10, 0, 0);
        SceneNode spawned = new("Spawned") { IsActiveSelf = false };
        ((XREngine.Scene.Transforms.Transform)spawned.Transform).Translation = new Vector3(3, 4, 5);
        try
        {
            Assert.That(HierarchySpawn.Place("Spawn Prefab", world, parent, null, spawned, preserveWorld: true), Is.True);
            Assert.That(spawned.Parent, Is.SameAs(parent));
            Assert.That(scene.SceneRoots, Does.Not.Contain(spawned));
            Vector3 position = spawned.Transform.WorldMatrix.Translation;
            Assert.That(Vector3.Distance(position, new Vector3(3, 4, 5)), Is.LessThan(0.001f));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(spawned.Parent, Is.Null);
            Assert.That(parent.Transform.Children, Does.Not.Contain(spawned.Transform));
        }
        finally { spawned.Destroy(true); parent.Destroy(true); }
    }

    [Test]
    public void HiddenSceneRootSpawnStaysOutOfTheRuntimeWorld()
    {
        SceneNode existing = new("Existing");
        XRScene scene = new("Hidden", existing) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        SceneNode spawned = new("Spawned") { IsActiveSelf = false };
        try
        {
            Assert.That(HierarchySpawn.Place("Spawn Model", world, null, scene, spawned, preserveWorld: false), Is.True);
            Assert.That(scene.SceneRoots, Does.Contain(spawned));
            Assert.That(world.RootNodes, Does.Not.Contain(spawned));
            Assert.That(spawned.World, Is.Null);
        }
        finally { spawned.Destroy(true); existing.Destroy(true); }
    }

    [Test]
    public void AttachedNodeIsRefusedBeforeAnyEdit()
    {
        SceneNode parent = new("Parent");
        XRScene scene = new("Scene", parent);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        SceneNode child = new(parent, "Child");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                HierarchySpawn.Place("Spawn Model", world, null, scene, child, preserveWorld: false));
            Assert.That(child.Parent, Is.SameAs(parent));
            Assert.That(Undo.TryUndo(), Is.False);
        }
        finally { parent.Destroy(true); }
    }
}
