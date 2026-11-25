using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeHierarchyMutationTests
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
    public void HiddenRootDeleteAndReplayKeepTheRootOutOfTheRuntimeWorld()
    {
        SceneNode root = new("Hidden");
        XRScene scene = new("Hidden scene", root) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        try
        {
            Assert.That(EditorImGuiUI.CanApplyNativeHierarchyEdit(root, world), Is.True);
            Run(() => EditorImGuiUI.QueueNativeHierarchyDelete(root, world));
            Assert.That(scene.SceneRoots, Does.Not.Contain(root));
            Assert.That(root.World, Is.Null);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(scene.SceneRoots, Does.Contain(root));
            Assert.That(root.World, Is.Null);
            Assert.That(world.RootNodes, Does.Not.Contain(root));
            Assert.That(root.IsActiveSelf, Is.True);
            Assert.That(scene.IsDirty, Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(scene.SceneRoots, Does.Not.Contain(root));
            Assert.That(root.World, Is.Null);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void HiddenChildCreationAndPromotionUseTheCapturedSourceScene()
    {
        SceneNode root = new("Hidden");
        XRScene scene = new("Hidden scene", root) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        SceneNode? child = null;
        try
        {
            Run(() => EditorImGuiUI.QueueNativeHierarchyCreateChild(root, world, value => child = value));
            Assert.That(child, Is.Not.Null);
            Assert.That(child!.Parent, Is.SameAs(root));
            Assert.That(child.World, Is.Null);
            Run(() => EditorImGuiUI.QueueNativeHierarchyMoveToRoot(child, world));
            Assert.That(scene.SceneRoots, Does.Contain(child));
            Assert.That(child.Parent, Is.Null);
            Assert.That(child.World, Is.Null);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(child.Parent, Is.SameAs(root));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(child.Parent, Is.Null);
            Assert.That(child.IsActiveSelf, Is.False);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(child.Parent, Is.SameAs(root));
            Assert.That(child.World, Is.Null);
        }
        finally { child?.Destroy(true); root.Destroy(true); }
    }

    [Test]
    public void DualSceneDeleteRestoresEveryMembershipAndRuntimeRoute()
    {
        SceneNode root = new("Editor content") { IsEditorOnly = true };
        XRScene source = new("Source", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", source));
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        XRScene editor = integration.EditorScene;
        try
        {
            Run(() => EditorImGuiUI.QueueNativeHierarchyDelete(root, world));
            Assert.That(source.SceneRoots, Does.Not.Contain(root));
            Assert.That(editor.SceneRoots, Does.Not.Contain(root));
            Assert.That(integration.CaptureHierarchySourceRoutes(root), Is.Empty);
            Assert.That(world.CaptureHierarchyRootRoutes(root).Scenes, Is.Empty);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(source.SceneRoots, Does.Contain(root));
            Assert.That(editor.SceneRoots, Does.Contain(root));
            Assert.That(integration.CaptureHierarchySourceRoutes(root), Is.EqualTo(new[] { source }));
            Assert.That(world.CaptureHierarchyRootRoutes(root).Scenes, Is.EqualTo(new[] { source }));
            source.IsVisible = false;
            Assert.That(world.RootNodes, Does.Not.Contain(root));
            Assert.That(editor.SceneRoots, Does.Not.Contain(root));
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void MovingAnEditorRootAcrossScenesRetiresTheOldSceneCensus()
    {
        SceneNode root = new("Moved root") { IsEditorOnly = true };
        SceneNode target = new("Destination");
        XRScene first = new("First", root), second = new("Second", target);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", first, second));
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        try
        {
            Run(() => EditorImGuiUI.QueueNativeHierarchyReparent(root, target, world));
            Assert.That(first.SceneRoots, Does.Not.Contain(root));
            Assert.That(integration.EditorScene.SceneRoots, Does.Not.Contain(root));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(first.SceneRoots, Does.Contain(root));
            Assert.That(integration.EditorScene.SceneRoots, Does.Contain(root));
            Assert.That(Undo.TryRedo(), Is.True);
            Run(() => EditorImGuiUI.QueueNativeHierarchyMoveToRoot(root, world));
            Assert.That(second.SceneRoots, Does.Contain(root));
            Assert.That(integration.EditorScene.SceneRoots, Does.Contain(root));
            Assert.That(integration.CaptureHierarchySourceRoutes(root), Is.EqualTo(new[] { second }));
            first.IsVisible = false;
            world.UnloadScene(first);
            Assert.That(root.World, Is.SameAs(world));
            Assert.That(world.RootNodes, Does.Contain(root));
            second.IsVisible = false;
            Assert.That(world.RootNodes, Does.Not.Contain(root));
            second.IsVisible = true;
            Assert.That(integration.EditorScene.SceneRoots, Does.Contain(root));
            Assert.That(world.RootNodes, Does.Contain(root));
        }
        finally { root.Destroy(true); target.Destroy(true); }
    }

    [Test]
    public void TheFirstPromotedEditorChildAllocatesTheEditorScene()
    {
        SceneNode root = new("Scene root");
        SceneNode child = new(root, "Editor child") { IsEditorOnly = true };
        XRScene scene = new("Source", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        try
        {
            Assert.That(integration.ExistingEditorScene, Is.Null);
            Run(() => EditorImGuiUI.QueueNativeHierarchyMoveToRoot(child, world));
            XRScene? editor = integration.ExistingEditorScene;
            Assert.That(editor, Is.Not.Null);
            Assert.That(child.Parent, Is.Null);
            Assert.That(scene.SceneRoots, Does.Contain(child));
            Assert.That(editor!.SceneRoots, Does.Contain(child));
            Assert.That(integration.CaptureHierarchySourceRoutes(child), Is.EqualTo(new[] { scene }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(child.Parent, Is.SameAs(root));
            Assert.That(scene.SceneRoots, Does.Not.Contain(child));
            Assert.That(editor.SceneRoots, Does.Not.Contain(child));
            Assert.That(integration.ExistingEditorScene, Is.SameAs(editor));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(scene.SceneRoots, Does.Contain(child));
            Assert.That(editor.SceneRoots, Does.Contain(child));
        }
        finally { child.Destroy(true); root.Destroy(true); }
    }

    [Test]
    public void AnOrdinaryPromotedChildDoesNotInheritItsParentsEditorRoute()
    {
        SceneNode root = new("Editor root") { IsEditorOnly = true };
        SceneNode child = new(root, "Ordinary child");
        XRScene scene = new("Source", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        EditorWorldIntegration integration = EditorWorldIntegrationRegistry.GetOrAttach(world);
        try
        {
            Run(() => EditorImGuiUI.QueueNativeHierarchyMoveToRoot(child, world));
            Assert.That(scene.SceneRoots, Does.Contain(child));
            Assert.That(integration.EditorScene.SceneRoots, Does.Not.Contain(child));
            Assert.That(integration.CaptureHierarchySourceRoutes(child), Is.Empty);
            scene.IsVisible = false;
            Assert.That(world.RootNodes, Does.Not.Contain(child));
            scene.IsVisible = true;
            Assert.That(world.RootNodes, Does.Contain(child));
            Assert.That(integration.EditorScene.SceneRoots, Does.Not.Contain(child));
        }
        finally { child.Destroy(true); root.Destroy(true); }
    }

    [Test]
    public void PromotedChildDoesNotInheritItsRootsExternalLifetimeOwner()
    {
        SceneNode root = new("External root");
        SceneNode child = new(root, "Child");
        XRScene scene = new("Scene", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene());
        world.RootNodes.Add(root);
        world.LoadScene(scene);
        try
        {
            Assert.That(world.CaptureHierarchyRootRoutes(root).ExternallyOwned, Is.True);
            Run(() => EditorImGuiUI.QueueNativeHierarchyMoveToRoot(child, world));
            Assert.That(scene.SceneRoots, Does.Contain(child));
            Assert.That(world.CaptureHierarchyRootRoutes(child).ExternallyOwned, Is.False);
            world.UnloadScene(scene);
            Assert.That(world.RootNodes, Does.Contain(root));
            Assert.That(world.RootNodes, Does.Not.Contain(child));
        }
        finally { child.Destroy(true); root.Destroy(true); }
    }

    [Test]
    public void ParentCallbackFailureRestoresMembershipButKeepsAnUnrelatedEditUndoable()
    {
        SceneNode root = new("Root"), target = new("Target"), other = new("Before callback");
        XRScene scene = new("Scene", root, target, other);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        Undo.TrackSceneNode(other);
        root.Transform.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(TransformBase.Parent) && ReferenceEquals(root.Parent, target))
            {
                other.Name = "Callback edit";
                Assert.That(Undo.TryUndo(), Is.False);
                throw new InvalidOperationException("Expected parent observer failure.");
            }
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => Run(() => EditorImGuiUI.QueueNativeHierarchyReparent(root, target, world)));
            Assert.That(root.Parent, Is.Null);
            Assert.That(scene.SceneRoots, Does.Contain(root));
            Assert.That(world.RootNodes, Does.Contain(root));
            Assert.That(other.Name, Is.EqualTo("Callback edit"));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(other.Name, Is.EqualTo("Before callback"));
            Assert.That(root.Parent, Is.Null);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { root.Destroy(true); target.Destroy(true); other.Destroy(true); }
    }

    [Test]
    public void FullyRestoredActiveFailureDoesNotLeaveARollbackOnlyPropertyAction()
    {
        SceneNode root = new("Root"), other = new("Before callback");
        XRScene scene = new("Scene", root, other);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        Undo.TrackSceneNode(other);
        root.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SceneNode.IsActiveSelf) && !root.IsActiveSelf)
            {
                other.Name = "Callback edit";
                throw new InvalidOperationException("Expected observer failure before Undo notification.");
            }
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => Run(() => EditorImGuiUI.QueueNativeHierarchyDelete(root, world)));
            Assert.That(root.IsActiveSelf, Is.True);
            Assert.That(scene.SceneRoots, Does.Contain(root));
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(other.Name, Is.EqualTo("Before callback"));
            Assert.That(root.IsActiveSelf, Is.True);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { root.Destroy(true); other.Destroy(true); }
    }

    [Test]
    public void FailedActiveRestorationKeepsAnInverseWhenTheObserverPrecedesUndoTracking()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Scene", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        bool fail = true;
        root.PropertyChanging += (_, change) =>
        {
            if (fail && change.PropertyName == nameof(SceneNode.IsActiveSelf) && !root.IsActiveSelf)
                throw new InvalidOperationException("Expected activation failure.");
        };
        root.PropertyChanged += (_, change) =>
        {
            if (fail && change.PropertyName == nameof(SceneNode.IsActiveSelf) && !root.IsActiveSelf)
                throw new InvalidOperationException("Expected deactivation observer failure.");
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => Run(() => EditorImGuiUI.QueueNativeHierarchyDelete(root, world)));
            Assert.That(scene.SceneRoots, Does.Contain(root));
            Assert.That(root.IsActiveSelf, Is.False);
            fail = false;
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(root.IsActiveSelf, Is.True);
            Assert.Throws<InvalidOperationException>(() => Undo.TryRedo());
            Assert.That(root.IsActiveSelf, Is.True);
            Assert.That(Undo.CanRedo, Is.True);
        }
        finally { fail = false; root.Destroy(true); }
    }

    [Test]
    public void ActiveReplayFailureCannotPassBySkippingItsIncompleteLifecycleOnRetry()
    {
        SceneNode root = new("Root");
        NativeHierarchyActivationProbe probe = root.AddComponent<NativeHierarchyActivationProbe>()!;
        XRScene scene = new("Scene", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        world.BeginPlayAsync().GetAwaiter().GetResult();
        bool fail = true;
        try
        {
            Run(() => EditorImGuiUI.QueueNativeHierarchyDelete(root, world));
            int activationCount = probe.ActivationCount;
            Assert.That(activationCount, Is.GreaterThan(0));
            Assert.That(probe.DeactivationCount, Is.GreaterThan(0));
            root.PropertyChanged += (_, change) =>
            {
                if (fail && change.PropertyName == nameof(SceneNode.IsActiveSelf) && root.IsActiveSelf)
                    throw new InvalidOperationException("Expected active observer failure.");
            };

            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(root.IsActiveSelf, Is.True);
            Assert.That(root.World, Is.SameAs(world));
            Assert.That(probe.ActivationCount, Is.EqualTo(activationCount));
            Assert.That(Undo.CanUndo, Is.True);
            Assert.That(Undo.CanRedo, Is.False);

            fail = false;
            root.IsActiveSelf = false;
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(root.IsActiveSelf, Is.False);
            Assert.That(probe.ActivationCount, Is.EqualTo(activationCount));
            Assert.That(Undo.CanUndo, Is.True);
        }
        finally { fail = false; root.Destroy(true); }
    }

    [Test]
    public void UndoDeleteAttachesTheNodeBeforeActivatingItsComponents()
    {
        SceneNode root = new("Root");
        NativeHierarchyActivationProbe probe = root.AddComponent<NativeHierarchyActivationProbe>()!;
        XRScene scene = new("Scene", root);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        world.BeginPlayAsync().GetAwaiter().GetResult();
        try
        {
            Run(() => EditorImGuiUI.QueueNativeHierarchyDelete(root, world));
            int activations = probe.ActivationCount;
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(probe.ActivationCount, Is.EqualTo(activations + 1));
            Assert.That(probe.WorldAtActivation, Is.SameAs(world));
            Assert.That(root.IsActiveSelf, Is.True);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void UndoRejectsForeignOwnershipBeforeChangingLocalValues()
    {
        SceneNode root = new("Root"), target = new("Target"), foreign = new("Foreign");
        ((Transform)root.Transform).Translation = new Vector3(8, 2, 1);
        ((Transform)target.Transform).Translation = new Vector3(4, 0, 0);
        XRScene scene = new("Scene", root, target);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        using RuntimeWorld other = new(new HierarchyTestPhysicsScene());
        other.RootNodes.Add(foreign);
        try
        {
            Run(() => EditorImGuiUI.QueueNativeHierarchyReparent(root, target, world));
            root.Transform.SetParent(foreign.Transform, false, EParentAssignmentMode.Immediate);
            root.SetWorldContext(other);
            Vector3 beforeUndo = ((Transform)root.Transform).Translation;
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(((Transform)root.Transform).Translation, Is.EqualTo(beforeUndo));
            Assert.That(root.Parent, Is.SameAs(foreign));
            Assert.That(Undo.CanUndo, Is.True);
        }
        finally { root.Destroy(true); target.Destroy(true); foreign.Destroy(true); }
    }

    [Test]
    public void FailedChildAttachmentRetiresItsExactNewAllocations()
    {
        SceneNode parent = new("Parent");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene());
        world.RootNodes.Add(parent);
        SceneNode? created = null;
        parent.Transform.Children.PostAnythingAdded += transform =>
        {
            created = transform.SceneNode;
            throw new InvalidOperationException("Expected attachment failure.");
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => Run(() => EditorImGuiUI.QueueNativeHierarchyCreateChild(parent, world, null)));
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.IsDestroyed, Is.True);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(created.ID), Is.False);
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(created.Transform.ID), Is.False);
            Assert.That(parent.Transform.Children, Is.Empty);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { created?.Destroy(true); parent.Destroy(true); }
    }

    private static void Run(Action enqueue)
    {
        ConcurrentQueue<Action> edits = (ConcurrentQueue<Action>)typeof(EditorImGuiUI)
            .GetField("_queuedSceneEdits", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.That(edits.IsEmpty, Is.True);
        enqueue();
        Assert.That(edits.TryDequeue(out Action? edit), Is.True);
        edit!();
        Assert.That(edits.IsEmpty, Is.True);
    }
}
