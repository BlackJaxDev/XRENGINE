using System.Collections.Concurrent;
using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Physics.Jitter2;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeHierarchySceneOwnershipTests
{
    private SceneNode[] _previousSelection = [];

    [SetUp]
    public void SetUp()
    {
        Undo.ClearHistory();
        _previousSelection = Selection.SceneNodes;
        Selection.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Undo.ClearHistory();
        Selection.SceneNodes = _previousSelection;
    }

    [Test]
    public void EditorSceneResolutionUsesThePassedWorldAndMarksItsOwnSceneDirty()
    {
        XRWorld asset = new("World");
        using RuntimeWorld world = new(new JitterScene(), asset);
        using RuntimeWorld otherWorld = new(new JitterScene());
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        SceneNode root = new("Editor root");
        SceneNode child = new(root, "Child");
        integration.AddToEditorScene(root);
        try
        {
            XRScene scene = integration.EditorScene;
            asset.ClearDirty();
            scene.ClearDirty();

            Assert.That(HierarchySceneOwnership.FindOwningScene(root, world), Is.SameAs(scene));
            Assert.That(HierarchySceneOwnership.FindOwningScene(child, world), Is.SameAs(scene));
            Assert.That(HierarchySceneOwnership.FindOwningScene(child, otherWorld), Is.Null);
            Assert.That(EditorWorldIntegrationRegistry.TryGet(otherWorld, out _), Is.False);
            typeof(HierarchyPanel).GetMethod("MarkSceneHierarchyDirty", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [child, world]);

            Assert.That(scene.IsDirty, Is.True);
            Assert.That(asset.IsDirty, Is.False);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void DeleteEditorRootUpdatesBothRootCollectionsAndUndoRestoresThem()
    {
        using RuntimeWorld world = new(new JitterScene());
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        SceneNode root = new("Editor root");
        integration.AddToEditorScene(root);
        XRScene scene = integration.EditorScene;
        try
        {
            RunQueuedEdit(() => EditorImGuiUI.QueueNativeHierarchyDelete(root, world));

            Assert.That(scene.RootNodes, Does.Not.Contain(root));
            Assert.That(world.RootNodes, Does.Not.Contain(root));
            Assert.That(root.IsActiveSelf, Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(scene.RootNodes, Does.Contain(root));
            Assert.That(world.RootNodes, Does.Contain(root));
            Assert.That(root.IsActiveSelf, Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(scene.RootNodes, Does.Not.Contain(root));
            Assert.That(world.RootNodes, Does.Not.Contain(root));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void ReparentEditorRootRetiresItsOldSceneMembershipAndUndoRestoresIt()
    {
        using RuntimeWorld world = new(new JitterScene());
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        SceneNode root = new("Editor root");
        SceneNode target = new("Destination root");
        integration.AddToEditorScene(root);
        world.RootNodes.Add(target);
        XRScene scene = integration.EditorScene;
        try
        {
            RunQueuedEdit(() => EditorImGuiUI.QueueNativeHierarchyReparent(root, target, world));

            Assert.That(root.Parent, Is.SameAs(target));
            Assert.That(scene.RootNodes, Does.Not.Contain(root));
            Assert.That(world.RootNodes, Does.Not.Contain(root));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(root.Parent, Is.Null);
            Assert.That(scene.RootNodes, Does.Contain(root));
            Assert.That(world.RootNodes, Does.Contain(root));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(root.Parent, Is.SameAs(target));
            Assert.That(scene.RootNodes, Does.Not.Contain(root));
        }
        finally { root.Destroy(true); target.Destroy(true); }
    }

    [Test]
    public void MovingAnEditorChildToRootKeepsItsEditorSceneOwner()
    {
        using RuntimeWorld world = new(new JitterScene());
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        SceneNode parent = new("Editor parent");
        SceneNode child = new(parent, "Editor child");
        integration.AddToEditorScene(parent);
        try
        {
            RunQueuedEdit(() => EditorImGuiUI.QueueNativeHierarchyMoveToRoot(child, world));

            Assert.That(child.Parent, Is.Null);
            Assert.That(integration.EditorScene.RootNodes, Does.Contain(child));
            Assert.That(world.RootNodes, Does.Contain(child));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(child.Parent, Is.SameAs(parent));
            Assert.That(integration.EditorScene.RootNodes, Does.Not.Contain(child));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(integration.EditorScene.RootNodes, Does.Contain(child));
        }
        finally { child.Destroy(true); parent.Destroy(true); }
    }

    [Test]
    public void RedoRejectsAChildThatEnteredTheEditorSceneWhileDetached()
    {
        using RuntimeWorld world = new(new JitterScene());
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        SceneNode parent = new("Editor parent");
        integration.AddToEditorScene(parent);
        SceneNode? child = null;
        RunQueuedEdit(() => EditorImGuiUI.QueueNativeHierarchyCreateChild(parent, world, value => child = value));
        Assert.That(child, Is.Not.Null);
        try
        {
            Assert.That(Undo.TryUndo(), Is.True);
            integration.EditorScene.RootNodes.Add(child!);

            Assert.Throws<InvalidOperationException>(() => Undo.TryRedo());

            Assert.That(child!.Parent, Is.Null);
            Assert.That(integration.EditorScene.RootNodes, Does.Contain(child));
            Assert.That(world.RootNodes, Does.Not.Contain(child));
        }
        finally
        {
            if (child is not null)
            {
                integration.EditorScene.RootNodes.Remove(child);
                child.Destroy(true);
            }
            parent.Destroy(true);
        }
    }

    private static void RunQueuedEdit(Action enqueue)
    {
        ConcurrentQueue<Action> edits = (ConcurrentQueue<Action>)typeof(EditorImGuiUI)
            .GetField("_queuedSceneEdits", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.That(edits.IsEmpty, Is.True, "The test must not consume another owner's scene edits.");
        enqueue();
        Assert.That(edits.TryDequeue(out Action? edit), Is.True);
        edit!();
        Assert.That(edits.IsEmpty, Is.True);
    }
}
