using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Prefabs;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class HierarchyCloneCustodyTests
{
    [SetUp]
    public void SetUp() => Undo.ClearHistory();

    [TearDown]
    public void TearDown()
    {
        Undo.ClearHistory();
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
        XRObjectBase.ProcessPendingDestructions();
    }

    [Test]
    public void UndoAndRedoKeepOneReceiptThenDetachedPruningRetiresOnce()
    {
        SceneNode source = new("Source");
        _ = new SceneNode(source, "Child");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        SceneNode clone = custody.Node!;
        TransformBase transform = clone.Transform;
        int attempts = 0;
        Func<XRObjectBase, bool> destroying = _ => { attempts++; return true; };
        clone.Destroying += destroying;
        try
        {
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Applied));
            Assert.That(scene.SceneRoots, Does.Contain(clone));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Detached));
            Assert.That(clone.IsDestroyed, Is.False);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Applied));
            Assert.That(Undo.TryUndo(), Is.True);
            Undo.ClearHistory();
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Released));
            Assert.That(custody.Lifetime!.State, Is.EqualTo(EOwnedHierarchyLifetimeState.Retired));
            Assert.That(clone.IsDestroyed, Is.True);
            Assert.That(transform.IsDestroyed, Is.True);
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(store.CaptureEntries(), Is.Empty);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { clone.Destroying -= destroying; Cleanup(custody); source.Destroy(true); }
    }

    [Test]
    public void AppliedPruningRetainsCurrentSameDocumentPlacement()
    {
        SceneNode source = new("Source"), destination = new("Destination");
        XRScene scene = new("Hidden", source, destination) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        SceneNode clone = custody.Node!;
        try
        {
            scene.SceneRoots.Remove(clone);
            clone.Parent = destination;
            clone.Name = "Later edit";
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Document));
            Assert.That(custody.RetainedOwnership, Is.Not.Null);
            Assert.That(custody.Resource.IsReleased, Is.True);
            Assert.That(clone.Parent, Is.SameAs(destination));
            Assert.That(clone.Name, Is.EqualTo("Later edit"));
            Assert.That(clone.IsDestroyed, Is.False);
            HierarchyCloneCustody next = store.Reserve(world);
            next.CompleteCommand();
            Assert.That(store.CaptureEntries(), Is.EqualTo(new[] { custody }));
        }
        finally { Cleanup(custody); source.Destroy(true); destination.Destroy(true); }
    }

    [Test]
    public void HiddenForeignRootLinkPreventsDocumentTransferWithoutDestroyingIt()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Document", source) { IsVisible = false };
        XRScene foreign = new("Other hidden scene") { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        SceneNode clone = custody.Node!;
        try
        {
            foreign.SceneRoots.Add(clone);
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.Resource.ReleaseFailure, Is.Not.Null);
            Assert.That(custody.RetainedOwnership, Is.Not.Null);
            Assert.That(clone.IsDestroyed, Is.False);
            Assert.That(foreign.SceneRoots, Does.Contain(clone));
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
        }
        finally { foreign.SceneRoots.Remove(clone); Cleanup(custody); source.Destroy(true); foreign.Destroy(true); }
    }

    [Test]
    public void ConstructionFailureKeepsItsLedgerAndOriginalErrorBeforeBlockingAnotherClone()
    {
        SceneNode source = new("Source", new OwnedCloneTransformProbe());
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        TextFile? allocation = null;
        InvalidOperationException original = new("Expected construction callback failure.");
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
        {
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
            allocation = new TextFile();
            throw original;
        });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        try
        {
            Assert.That(Assert.Throws<InvalidOperationException>(() => CloneRoot(source, world, store)), Is.SameAs(original));
            HierarchyCloneCustody custody = store.CaptureEntries().Single();
            Assert.That(custody.Failure, Is.SameAs(original));
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership!.Contains(allocation!), Is.True);
            Assert.That(allocation!.IsDestroyed, Is.False);
            Assert.That(source.IsDestroyed, Is.False);
            Assert.Throws<InvalidOperationException>(() => CloneRoot(source, world, store));
        }
        finally { allocation?.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void FullInitialRollbackRetiresTheCloneAndKeepsAnUnrelatedCallbackEdit()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        SceneNode? failedClone = null;
        int callbackValue = 1;
        HierarchyPlacement sourcePlacement = HierarchyPlacement.Capture(source, world);
        try
        {
            Assert.Throws<InvalidOperationException>(() => HierarchyCloneMutation.Execute("Clone", source, world,
                clone => HierarchyPlacement.RootLike(clone, sourcePlacement), clone =>
                {
                    failedClone = clone;
                    Undo.RecordStructuralChange("Callback edit", () => callbackValue = 0, () => callbackValue = 1);
                    throw new InvalidOperationException("Expected callback failure.");
                }, store: store));
            Assert.That(failedClone, Is.Not.Null);
            Assert.That(failedClone!.IsDestroyed, Is.True);
            Assert.That(scene.SceneRoots, Does.Not.Contain(failedClone));
            Assert.That(store.CaptureEntries(), Is.Empty);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(callbackValue, Is.Zero);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { failedClone?.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void InitialRollbackKeepsANewerCallbackEditOfTheClone()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        SceneNode? clone = null;
        System.Numerics.Vector3 newer = new(3, 0, 0);
        HierarchyPlacement sourcePlacement = HierarchyPlacement.Capture(source, world);
        try
        {
            Assert.Throws<InvalidOperationException>(() => HierarchyCloneMutation.Execute("Clone", source, world,
                node => HierarchyPlacement.RootLike(node, sourcePlacement), node =>
                {
                    clone = node;
                    // A callback edits the applied clone, then fails. Rollback must not undo this edit.
                    ((Transform)node.Transform).Translation = newer;
                    throw new InvalidOperationException("Expected callback failure.");
                }, store: store));
            Assert.That(clone, Is.Not.Null);
            Assert.That(clone!.IsDestroyed, Is.False);
            Assert.That(((Transform)clone.Transform).Translation, Is.EqualTo(newer));
        }
        finally { clone?.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void ACommitObserverCannotStartAnotherCloneAndCanReleaseAppliedHistory()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        bool observed = false;
        void Changed()
        {
            if (observed) return;
            observed = true;
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
            Undo.ClearHistory();
        }
        Undo.HistoryChanged += Changed;
        HierarchyCloneCustody? custody = null;
        try
        {
            custody = CloneRoot(source, world, store);
            Assert.That(observed, Is.True);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Document));
            Assert.That(custody.Node!.IsDestroyed, Is.False);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { Undo.HistoryChanged -= Changed; if (custody is not null) Cleanup(custody); source.Destroy(true); }
    }

    [Test]
    public void VetoedRetirementIsRetainedAndExplicitHistoryRetryDoesNotRetryDestruction()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        SceneNode clone = custody.Node!;
        int attempts = 0;
        Func<XRObjectBase, bool> veto = _ => { attempts++; return false; };
        clone.Destroying += veto;
        try
        {
            Assert.That(Undo.TryUndo(), Is.True);
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.Lifetime!.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership, Is.Not.Null);
            Assert.That(Undo.RetryHistoryResourceRelease(custody.Resource), Is.True);
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(clone.IsDestroyed, Is.False);
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
            using (Undo.BeginUserInteraction())
            using (Undo.BeginChange("Edit retained clone"))
                clone.Name = "Still tracked";
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clone.Name, Is.EqualTo("Source"));
        }
        finally { clone.Destroying -= veto; Cleanup(custody); source.Destroy(true); }
    }

    [Test]
    public void StrictRetirementDoesNotSuppressAnUnrelatedCallbackDeletion()
    {
        SceneNode source = new("Source"), unrelated = new("Unrelated");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        SceneNode clone = custody.Node!;
        Undo.TrackSceneNode(unrelated);
        Func<XRObjectBase, bool> callback = _ => { unrelated.Destroy(true); return true; };
        clone.Destroying += callback;
        try
        {
            Assert.That(Undo.TryUndo(), Is.True);
            using (Undo.BeginUserInteraction())
                Undo.ClearHistory();
            Assert.That(clone.IsDestroyed, Is.True);
            Assert.That(unrelated.IsDestroyed, Is.True);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Released));
            Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
            Assert.That(Undo.PendingUndo[0].Description, Is.EqualTo("Delete Unrelated"));
        }
        finally
        {
            clone.Destroying -= callback;
            Undo.Untrack(unrelated);
            Cleanup(custody);
            unrelated.Destroy(true);
            source.Destroy(true);
        }
    }

    [Test]
    public void UnknownNestedAssetPreventsPrunedRetirementBeforeAnyDestruction()
    {
        SceneNode source = new("Source", new OwnedCloneTransformProbe());
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        TextFile? asset = null;
        using IDisposable handler = SceneNodePrefabUtility.RegisterTransformCloneHandler<OwnedCloneTransformProbe>(_ =>
            new OwnedCloneTransformProbe { Asset = asset = new TextFile() });
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        SceneNode clone = custody.Node!;
        int attempts = 0;
        Func<XRObjectBase, bool> callback = _ => { attempts++; return true; };
        clone.Destroying += callback;
        try
        {
            Assert.That(Undo.TryUndo(), Is.True);
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership!.Contains(asset!), Is.True);
            Assert.That(clone.IsDestroyed, Is.False);
            Assert.That(asset!.IsDestroyed, Is.False);
            Assert.That(attempts, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
        }
        finally { clone.Destroying -= callback; Cleanup(custody); asset?.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void EndedDocumentKeepsItsReceiptAndBlocksNewConstruction()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        try
        {
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Document));
            world.Dispose();
            using RuntimeWorld next = new(new HierarchyTestPhysicsScene(), new XRWorld("Next"));
            Assert.Throws<InvalidOperationException>(() => store.Reserve(next));
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership, Is.Not.Null);
        }
        finally { Cleanup(custody); source.Destroy(true); }
    }

    [Test]
    public void AppliedCloneInAReadyExternalHiddenSceneKeepsDocumentCustody()
    {
        SceneNode source = new("External source");
        XRScene scene = new("External hidden scene", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene());
        world.LoadScene(scene);
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        try
        {
            Assert.That(world.IsSceneReady(scene), Is.True);
            Assert.That(custody.Node!.World, Is.Null);
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Document));
            Assert.That(scene.SceneRoots, Does.Contain(custody.Node));
            Assert.That(custody.Node!.IsDestroyed, Is.False);
        }
        finally { Cleanup(custody); source.Destroy(true); }
    }

    [TestCase("Undo")]
    [TestCase("Release")]
    public void ChangedStructuralIdentityRetainsNewerStateInsteadOfReplayingOrDestroyingIt(string boundary)
    {
        SceneNode source = new("Source");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        Guid newId = Guid.NewGuid();
        HierarchyCloneCustody custody = CloneRoot(source, world, store);
        SceneNode clone = custody.Node!;
        try
        {
            if (boundary == "Release")
                Assert.That(Undo.TryUndo(), Is.True);
            clone.AdoptPersistentID(newId);
            if (boundary == "Undo")
                Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.Lifetime!.State, Is.EqualTo(EOwnedHierarchyLifetimeState.Retained));
            Assert.That(clone.ID, Is.EqualTo(newId));
            Assert.That(clone.IsDestroyed, Is.False);
            Assert.That(scene.SceneRoots.Contains(clone), Is.EqualTo(boundary != "Release"));
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
        }
        finally { Cleanup(custody); source.Destroy(true); }
    }

    [Test]
    public void InitialCloneCallbacksCannotChangeOwnedLifetimesButCanEditUnrelatedObjects()
    {
        SceneNode source = new("Source"), unrelated = new("Unrelated");
        XRScene scene = new("Hidden", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        Guid unrelatedId = unrelated.ID;
        bool invoked = false;
        HierarchyPlacement sourcePlacement = HierarchyPlacement.Capture(source, world);
        HierarchyCloneCustody custody = HierarchyCloneMutation.Execute("Clone", source, world,
            clone => HierarchyPlacement.RootLike(clone, sourcePlacement), clone =>
            {
                invoked = true;
                Assert.Throws<InvalidOperationException>(clone.Generate);
                Assert.Throws<InvalidOperationException>(() => clone.AdoptPersistentID(Guid.NewGuid()));
                Assert.Throws<InvalidOperationException>(() => clone.Transform.Children.Destroy(false));
                unrelated.Generate();
                unrelated.Name = "Allowed edit";
            }, store: store);
        try
        {
            Assert.That(invoked, Is.True);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Applied));
            Assert.That(unrelated.ID, Is.Not.EqualTo(unrelatedId));
            Assert.That(unrelated.Name, Is.EqualTo("Allowed edit"));
            Assert.That(Undo.TryUndo(), Is.True);
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Released));
        }
        finally { Cleanup(custody); source.Destroy(true); unrelated.Destroy(true); }
    }

    private static HierarchyCloneCustody CloneRoot(SceneNode source, RuntimeWorld world, HierarchyCloneCustodyStore store)
    {
        HierarchyPlacement sourcePlacement = HierarchyPlacement.Capture(source, world);
        return HierarchyCloneMutation.Execute("Clone", source, world,
            clone => HierarchyPlacement.RootLike(clone, sourcePlacement), store: store);
    }

    private static void Cleanup(HierarchyCloneCustody custody)
    {
        Undo.ClearHistory();
        if (custody.Node is not { } node)
            return;
        Undo.Untrack(node);
        foreach (SceneRootMembership membership in node.CaptureSceneRootMemberships())
            while (membership.Collection.Remove(node)) { }
        node.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();
    }
}
