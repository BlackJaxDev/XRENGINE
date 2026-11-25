using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class HierarchySceneActionsTests
{
    [SetUp]
    public void SetUp()
    {
        Undo.ClearHistory();
        Selection.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Undo.ClearHistory();
        Selection.Clear();
        XRObjectBase.ProcessPendingDestructions();
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void UnloadRestoresExactOrderLoadedStateSelectionAndDirtyDocuments(bool visible, bool loaded)
    {
        SceneNode root = new("Target root"), other = new("Other root");
        SceneNode child = new(root, "Selected child");
        XRScene first = new("First", other), scene = new("Target", root) { IsVisible = visible }, last = new("Last");
        XRWorld asset = new("World", first, scene, last);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        if (!loaded) world.UnloadScene(scene);
        Selection.SceneNodes = [child, other];
        scene.ClearDirty(); asset.ClearDirty();
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.True);
            Assert.That(asset.Scenes, Is.EqualTo(new[] { first, last }));
            Assert.That(world.IsSceneLoaded(scene), Is.False);
            Assert.That(scene.IsVisible, Is.False);
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { other }));
            Assert.That(scene.IsDirty && asset.IsDirty, Is.True);
            scene.ClearDirty(); asset.ClearDirty();

            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(asset.Scenes, Is.EqualTo(new[] { first, scene, last }));
            Assert.That(world.IsSceneLoaded(scene), Is.EqualTo(loaded));
            Assert.That(scene.IsVisible, Is.EqualTo(visible));
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { child, other }));
            Assert.That(scene.IsDirty && asset.IsDirty, Is.True);
            Assert.That(root.IsDestroyed || child.IsDestroyed, Is.False);
            Assert.That(Undo.CanUndo, Is.False, "One unload must publish one history action.");
            scene.ClearDirty(); asset.ClearDirty();

            // Redo captures its own selection before it removes authored membership.
            Selection.SceneNodes = [child];
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(Selection.SceneNodes, Is.Empty);
            Assert.That(asset.Scenes, Is.EqualTo(new[] { first, last }));
            Assert.That(world.IsSceneLoaded(scene), Is.False);
            Assert.That(scene.IsDirty && asset.IsDirty, Is.True);
        }
        finally { Cleanup(world, root, other); }
    }

    [Test]
    public void ConsecutiveVisibilityHistoryMarksTheSceneOnEveryReplay()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Target", root);
        XRWorld asset = new("World", scene);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        try
        {
            asset.ClearDirty(); scene.ClearDirty();
            Assert.That(HierarchySceneActions.SetVisibility(scene, world, false), Is.True);
            Assert.That(scene.IsDirty, Is.True);
            Assert.That(asset.IsDirty, Is.False, "Visibility belongs to the scene document.");
            scene.ClearDirty();
            Assert.That(HierarchySceneActions.SetVisibility(scene, world, true), Is.True);
            Assert.That(scene.IsDirty, Is.True);
            foreach (bool expected in new[] { false, true })
            {
                scene.ClearDirty();
                Assert.That(Undo.TryUndo(), Is.True);
                Assert.That(scene.IsVisible, Is.EqualTo(expected));
                Assert.That(scene.IsDirty, Is.True);
            }
            foreach (bool expected in new[] { false, true })
            {
                scene.ClearDirty();
                Assert.That(Undo.TryRedo(), Is.True);
                Assert.That(scene.IsVisible, Is.EqualTo(expected));
                Assert.That(scene.IsDirty, Is.True);
            }
        }
        finally { Cleanup(world, root); }
    }

    [Test]
    public void VisibilityCallbackSelectionPublicationIsPreservedThroughUnloadAndUndo()
    {
        SceneNode root = new("Root"), other = new("Other");
        XRScene scene = new("Target", root), peer = new("Peer", other);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene, peer));
        Selection.SceneNodes = [root, other];
        bool once = true;
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName != nameof(XRScene.IsVisible) || !once) return;
            once = false;
            // Equal values still express a newer selection owner.
            Selection.SceneNodes = [root, other];
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.True);
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { root, other }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { root, other }));
        }
        finally { scene.PropertyChanging -= callback; Cleanup(world, root, other); }
    }

    [Test]
    public void PeerListChangeDuringVisibilityPrewritePreventsTheOldUnload()
    {
        SceneNode root = new("Root");
        XRScene peer = new("Peer"), scene = new("Target", root);
        XRWorld asset = new("World", peer, scene);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible)) asset.Scenes.Remove(peer);
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.False);
            Assert.That(asset.Scenes, Is.EqualTo(new[] { scene }));
            Assert.That(scene.IsVisible, Is.True);
            Assert.That(world.IsSceneLoaded(scene), Is.True);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(asset.Scenes, Is.EqualTo(new[] { scene }));
        }
        finally { scene.PropertyChanging -= callback; Cleanup(world, root); }
    }

    [Test]
    public void NewerEqualVisibilityIntentCannotBeRearmedAsAnOwnedUnload()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Target", root);
        XRWorld asset = new("World", scene);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible)) scene.IsVisible = true;
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.False);
            Assert.That(scene.IsVisible, Is.True);
            Assert.That(asset.Scenes, Is.EqualTo(new[] { scene }));
            Assert.That(world.IsSceneReady(scene), Is.True);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        }
        finally { scene.PropertyChanging -= callback; Cleanup(world, root); }
    }

    [Test]
    public void ThrowAfterVisibilityCommitCompensatesOnlyTheCompletedWrite()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Target", root);
        XRWorld asset = new("World", scene);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        bool once = true;
        XRPropertyChangedEventHandler callback = (_, args) =>
        {
            if (args.PropertyName != nameof(XRScene.IsVisible) || !once) return;
            once = false;
            throw new InvalidOperationException("Expected observer failure after commit.");
        };
        scene.PropertyChanged += callback;
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.False);
            Assert.That(scene.IsVisible, Is.True);
            Assert.That(world.IsSceneReady(scene), Is.True);
            Assert.That(asset.Scenes, Is.EqualTo(new[] { scene }));
            Assert.That(root.IsDestroyed, Is.False);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { scene.PropertyChanged -= callback; Cleanup(world, root); }
    }

    [Test]
    public void ReentrantSceneActionCannotEnterTheCurrentWorldReservation()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Target", root), peer = new("Peer");
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene, peer));
        bool rejected = false;
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible))
                rejected = !HierarchySceneActions.Unload(peer, world);
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.True);
            Assert.That(rejected, Is.True);
            Assert.That(world.TargetWorld!.Scenes, Is.EqualTo(new[] { peer }));
        }
        finally { scene.PropertyChanging -= callback; Cleanup(world, root); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EmbeddedSourceDocumentsReceiveDirtyStateAndSharedSourcesAreDeduplicated(bool shared)
    {
        SceneNode root = new("Root");
        XRScene scene = new("Target", root);
        XRWorld asset = new("World", scene);
        XRWorld sceneDocument = new("Scene document"), worldDocument = shared ? sceneDocument : new("World document");
        scene.SourceAsset = sceneDocument;
        asset.SourceAsset = worldDocument;
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        int sceneMarks = 0;
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRWorld.IsDirty)) sceneMarks++;
        };
        sceneDocument.PropertyChanging += callback;
        try
        {
            for (int phase = 0; phase < 3; phase++)
            {
                sceneDocument.ClearDirty(); worldDocument.ClearDirty();
                scene.ClearDirty(); asset.ClearDirty();
                sceneMarks = 0;
                bool success = phase switch
                {
                    0 => HierarchySceneActions.Unload(scene, world),
                    1 => Undo.TryUndo(),
                    _ => Undo.TryRedo()
                };
                Assert.That(success, Is.True);
                Assert.That(sceneDocument.IsDirty && worldDocument.IsDirty, Is.True);
                Assert.That(scene.IsDirty || asset.IsDirty, Is.False);
                Assert.That(sceneMarks, Is.EqualTo(1));
            }
        }
        finally
        {
            sceneDocument.PropertyChanging -= callback;
            Cleanup(world, root);
            scene.SourceAsset = scene; asset.SourceAsset = asset;
            sceneDocument.Destroy(true);
            if (!shared) worldDocument.Destroy(true);
        }
    }

    [Test]
    public void SourceDocumentReplacementPreventsMutationAndDoesNotDirtyEitherDocument()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Target", root);
        XRWorld asset = new("World", scene), oldDocument = new("Old source"), newDocument = new("New source");
        scene.SourceAsset = oldDocument;
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        XRPropertyChangingEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRScene.IsVisible)) scene.SourceAsset = newDocument;
        };
        scene.PropertyChanging += callback;
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.False);
            Assert.That(scene.SourceAsset, Is.SameAs(newDocument));
            Assert.That(scene.IsVisible, Is.True);
            Assert.That(asset.Scenes, Is.EqualTo(new[] { scene }));
            Assert.That(oldDocument.IsDirty || newDocument.IsDirty, Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        }
        finally
        {
            scene.PropertyChanging -= callback;
            Cleanup(world, root);
            scene.SourceAsset = scene;
            oldDocument.Destroy(true); newDocument.Destroy(true);
        }
    }

    [Test]
    public void SceneDirtyCallbackSourceReplacementStillMarksTheUnchangedWorldDocument()
    {
        SceneNode root = new("Root");
        XRScene scene = new("Target", root);
        XRWorld asset = new("World", scene), sceneDocument = new("Scene source"),
            worldDocument = new("World source"), replacement = new("Replacement scene source");
        scene.SourceAsset = sceneDocument;
        asset.SourceAsset = worldDocument;
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        XRPropertyChangedEventHandler callback = (_, args) =>
        {
            if (args.PropertyName == nameof(XRWorld.IsDirty) && sceneDocument.IsDirty)
                scene.SourceAsset = replacement;
        };
        sceneDocument.PropertyChanged += callback;
        try
        {
            Assert.That(HierarchySceneActions.Unload(scene, world), Is.False);
            Assert.That(asset.Scenes, Is.Empty);
            Assert.That(world.IsSceneLoaded(scene), Is.False);
            Assert.That(scene.SourceAsset, Is.SameAs(replacement));
            Assert.That(sceneDocument.IsDirty, Is.True);
            Assert.That(worldDocument.IsDirty, Is.True, "The world still owns its committed scene-list edit.");
            Assert.That(replacement.IsDirty, Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(asset.Scenes, Is.Empty);
        }
        finally
        {
            sceneDocument.PropertyChanged -= callback;
            Cleanup(world, root);
            scene.SourceAsset = scene; asset.SourceAsset = asset;
            sceneDocument.Destroy(true); worldDocument.Destroy(true); replacement.Destroy(true);
        }
    }

    private static void Cleanup(RuntimeWorld world, params SceneNode[] roots)
    {
        Undo.ClearHistory();
        Selection.Clear();
        world.Dispose();
        foreach (SceneNode root in roots)
        {
            Undo.Untrack(root);
            if (!root.IsDestroyed) root.Destroy(true);
        }
        XRObjectBase.ProcessPendingDestructions();
    }
}
