using System.Numerics;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeHierarchyDuplicateTests
{
    private SceneNode[] _selection = [];

    [SetUp]
    public void SetUp()
    {
        _selection = Selection.SceneNodes;
        Selection.Clear();
        Undo.ClearHistory();
        XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Development);
    }

    [TearDown]
    public void TearDown()
    {
        Selection.Clear();
        Undo.ClearHistory();
        XRObjectBase.ProcessPendingDestructions();
        Selection.SceneNodes = _selection;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HiddenRootOrChildDuplicateKeepsOneUndoActionAndRestoresSelection(bool childSource)
    {
        SceneNode root = new("Root");
        SceneNode source = childSource ? new(root, "Source") : root;
        _ = new SceneNode(source, "Nested");
        XRScene scene = new("Hidden", root) { IsVisible = false };
        using RuntimeWorld world = World(scene);
        HierarchyCloneCustodyStore store = new();
        Selection.SceneNodes = [source];
        HierarchyDuplicateRequest request = new([source], world, () => true);
        Assert.That(request.OrdinaryUnavailable, Is.Null);
        HierarchyCloneCustody custody = request.Execute(true, store);
        SceneNode clone = custody.Node!;
        try
        {
            Assert.That(clone.Name, Is.EqualTo(childSource ? "Source (1)" : "Root (1)"));
            Assert.That(clone.World, Is.Null);
            Assert.That(clone.Parent, Is.SameAs(childSource ? root : null));
            Assert.That(clone.GetChild(0)!.Name, Is.EqualTo("Nested"));
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { clone }));
            Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(clone.Parent, Is.Null);
            Assert.That(scene.SceneRoots, Does.Not.Contain(clone));
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { source }));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { clone }));
            Assert.That(Undo.TryUndo(), Is.True);
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Released));
            Assert.That(clone.IsDestroyed, Is.True);
            Assert.That(store.CaptureEntries(), Is.Empty);
        }
        finally { Cleanup(custody); root.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroupedDuplicateUsesOneForestActionAndRestoresOrderedSelection(bool childSources)
    {
        SceneNode parentA = new("Parent A"), parentB = new("Parent B");
        SceneNode first = childSources ? new(parentA, "Same") : parentA;
        SceneNode second = childSources ? new(parentB, "Same") : parentB;
        XRScene scene = new("Hidden", parentA, parentB) { IsVisible = false };
        using RuntimeWorld world = World(scene);
        HierarchyCloneCustodyStore store = new();
        Selection.SceneNodes = [first, second];
        HierarchyCloneCustody custody = new HierarchyDuplicateRequest([first, second], world, () => true)
            .Execute(true, store);
        try
        {
            SceneNode[] clones = custody.Nodes!.ToArray();
            Assert.That(clones, Has.Length.EqualTo(2));
            Assert.That(clones[0].Name, Is.EqualTo(childSources ? "Same (1)" : "Parent A (1)"));
            Assert.That(clones[1].Name, Is.EqualTo(childSources ? "Same (1)" : "Parent B (1)"));
            Assert.That(Selection.SceneNodes, Is.EqualTo(clones));
            Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { first, second }));
            Assert.That(clones.All(clone => clone.Parent is null), Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(Selection.SceneNodes, Is.EqualTo(clones));
            Assert.That(Undo.TryUndo(), Is.True);
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Released));
            Assert.That(store.CaptureEntries(), Is.Empty);
        }
        finally { Cleanup(custody); parentA.Destroy(true); parentB.Destroy(true); }
    }

    [Test]
    public void GroupedDuplicateReservesNamesAndRetainsNewerPeerValue()
    {
        SceneNode first = new("Same"), second = new("Same");
        XRScene scene = new("Hidden", first, second) { IsVisible = false };
        using RuntimeWorld world = World(scene);
        HierarchyCloneCustodyStore store = new();
        SceneNode[]? clones = null;
        try
        {
            scene.ClearDirty();
            Assert.Throws<InvalidOperationException>(() =>
                new HierarchyDuplicateRequest([first, second], world, () => true).Execute(true, store,
                    clone =>
                    {
                        if (clones is not null) return;
                        clones = Selection.SceneNodes;
                        Assert.That(clones.Select(node => node.Name), Is.EqualTo(new[] { "Same (1)", "Same (2)" }));
                        ((Transform)clones[1].Transform).Translation = new Vector3(17, 0, 0);
                    }));
            Assert.That(clones, Is.Not.Null);
            Assert.That(((Transform)clones![1].Transform).Translation.X, Is.EqualTo(17));
            HierarchyCloneCustody retained = store.CaptureEntries().Single();
            Assert.That(retained.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(retained.RetainedOwnership, Is.Not.Null);
            Assert.That(scene.IsDirty, Is.True);
            Cleanup(retained);
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void CompleteGroupedCompensationReleasesForestAndKeepsUnrelatedCallbackEdit()
    {
        SceneNode first = new("First"), second = new("Second"), unrelated = new("Before callback");
        using RuntimeWorld world = World(new XRScene("Hidden", first, second, unrelated) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        HierarchyCloneCustody? custody = null;
        Undo.TrackSceneNode(unrelated);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new HierarchyDuplicateRequest([first, second], world, () => true).Execute(true, store,
                    _ =>
                    {
                        custody = store.CaptureEntries().Single();
                        unrelated.Name = "Callback edit";
                        throw new InvalidOperationException("Expected observer failure.");
                    }));
            Assert.That(custody, Is.Not.Null);
            Assert.That(custody!.State, Is.EqualTo(EHierarchyCloneCustodyState.Released));
            Assert.That(store.CaptureEntries(), Is.Empty);
            Assert.That(unrelated.Name, Is.EqualTo("Callback edit"));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(unrelated.Name, Is.EqualTo("Before callback"));
        }
        finally { first.Destroy(true); second.Destroy(true); unrelated.Destroy(true); }
    }

    [Test]
    public void GroupedSiblingsReserveNamesWithinTheirSharedParent()
    {
        SceneNode parent = new("Parent");
        SceneNode first = new(parent, "Same"), second = new(parent, "Same");
        using RuntimeWorld world = World(new XRScene("Hidden", parent) { IsVisible = false });
        HierarchyCloneCustody custody = new HierarchyDuplicateRequest([first, second], world, () => true)
            .Execute(true, new());
        try
        {
            Assert.That(custody.Nodes!.Select(node => node.Name), Is.EqualTo(new[] { "Same (1)", "Same (2)" }));
            Assert.That(custody.Nodes!.All(node => ReferenceEquals(node.Parent, parent)), Is.True);
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(Undo.TryRedo(), Is.True);
        }
        finally { Cleanup(custody); parent.Destroy(true); }
    }

    [Test]
    public void MenuTargetCaptureExcludesDescendantsOfSelectedRoots()
    {
        SceneNode root = new("Root"), child = new(root, "Child"), other = new("Other");
        try
        {
            Selection.SceneNodes = [child, root, other];
            Assert.That(EditorImGuiUI.CaptureNativeHierarchyDuplicateTargets(child),
                Is.EqualTo(new[] { root, other }));
        }
        finally { root.Destroy(true); other.Destroy(true); }
    }

    [Test]
    public void DeactivationCallbackChangingAnotherCloneCannotBecomeItsBeforeState()
    {
        SceneNode first = new("First"), second = new("Second");
        using RuntimeWorld world = World(new XRScene("Hidden", first, second) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        SceneNode?[] clones = new SceneNode?[2];
        try
        {
            Assert.Throws<InvalidOperationException>(() => HierarchyCloneMutation.ExecuteMany(
                "Duplicate Nodes", [first, second], world,
                (index, clone) => HierarchyPlacement.RootLike(clone,
                    HierarchyPlacement.Capture(index == 0 ? first : second, world)),
                store: store,
                configure: (index, clone, _, _) =>
                {
                    clones[index] = clone;
                    if (index == 0)
                        clone.PropertyChanged += (_, change) =>
                        {
                            if (change.PropertyName == nameof(SceneNode.IsActiveSelf) && !clone.IsActiveSelf &&
                                clones[1] is { } peer)
                                ((Transform)peer.Transform).Translation = new Vector3(17, 0, 0);
                        };
                }));
            Assert.That(clones[1], Is.Not.Null);
            Assert.That(((Transform)clones[1]!.Transform).Translation.X, Is.EqualTo(17));
            HierarchyCloneCustody retained = store.CaptureEntries().Single();
            Assert.That(retained.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(retained.RetainedOwnership, Is.Not.Null);
            Cleanup(retained);
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void PartialForestConstructionKeepsReceiptButPreflightRejectionLeavesNoHistory()
    {
        SceneNode first = new("First"), second = new("Second");
        XRScene scene = new("Hidden", first, second) { IsVisible = false };
        using RuntimeWorld world = World(scene);
        HierarchyCloneCustodyStore store = new();
        try
        {
            Assert.Throws<InvalidOperationException>(() => HierarchyCloneMutation.ExecuteMany(
                "Duplicate Nodes", [first, second], world,
                (index, clone) => HierarchyPlacement.RootLike(clone,
                    HierarchyPlacement.Capture(index == 0 ? first : second, world)),
                store: store, configure: (index, _, _, _) =>
                {
                    if (index == 1) throw new InvalidOperationException("Second clone configuration failed.");
                }));
            HierarchyCloneCustody retained = store.CaptureEntries().Single();
            Assert.That(retained.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(retained.RetainedOwnership, Is.Not.Null);
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Cleanup(retained);

            SceneNode unsupported = new("Unsupported", new NativeHierarchyDuplicateTransformProbe
                { Assets = [new TextFile()] });
            try
            {
                scene.SceneRoots.Add(unsupported);
                HierarchyDuplicateRequest request = new([first, unsupported], world, () => true);
                Assert.That(request.OrdinaryUnavailable, Is.Not.Null);
                Assert.Throws<InvalidOperationException>(() => request.Execute(true, new()));
                Assert.That(Undo.CanUndo, Is.False);
            }
            finally
            {
                scene.SceneRoots.Remove(unsupported);
                ((NativeHierarchyDuplicateTransformProbe)unsupported.Transform).Assets![0].Destroy(true);
                unsupported.Destroy(true);
            }
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PropertyReplayCallbackChangingPeerKeepsTheNewerValue(bool duringRedo)
    {
        SceneNode parent = new("Parent");
        SceneNode first = new(parent, "First"), second = new(parent, "Second");
        using RuntimeWorld world = World(new XRScene("Hidden", parent) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        bool configureValues = true;
        HierarchyCloneCustody custody = HierarchyCloneMutation.ExecuteMany(
            "Duplicate Nodes", [first, second], world,
            (_, clone) => HierarchyPlacement.Attached(clone, world, parent.Transform),
            store: store,
            configure: (index, clone, _, _) =>
            {
                clone.Transform.PropertyChanged += (_, change) =>
                {
                    if (configureValues && change.PropertyName == nameof(TransformBase.Parent) &&
                        ReferenceEquals(clone.Parent, parent))
                        ((Transform)clone.Transform).Translation = new Vector3(10 + index, 0, 0);
                };
            });
        configureValues = false;
        try
        {
            SceneNode[] clones = custody.Nodes!.ToArray();
            Transform firstValue = (Transform)clones[0].Transform;
            Transform peerValue = (Transform)clones[1].Transform;
            Assert.That(firstValue.Translation.X, Is.EqualTo(10));
            Assert.That(peerValue.Translation.X, Is.EqualTo(11));
            if (duringRedo) Assert.That(Undo.TryUndo(), Is.True);
            firstValue.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(Transform.Translation) &&
                    firstValue.Translation.X == (duringRedo ? 10 : 0))
                    peerValue.Translation = new Vector3(99, 0, 0);
            };
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (duringRedo) Undo.TryRedo();
                else Undo.TryUndo();
            });
            Assert.That(peerValue.Translation.X, Is.EqualTo(99));
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
        }
        finally { Cleanup(custody); parent.Destroy(true); }
    }

    [Test]
    public void OrdinaryDuplicateSharesEveryDirectAssetAliasAndAutoProperty()
    {
        TextFile asset = new();
        NativeHierarchyDuplicateTransformProbe transform = new() { First = asset, Second = asset, AutoAsset = asset };
        SceneNode source = new("Source", transform);
        using RuntimeWorld world = World(new XRScene("Hidden", source) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        HierarchyDuplicateRequest request = new([source], world, () => true);
        Assert.That(request.OrdinaryUnavailable, Is.Null);
        HierarchyCloneCustody custody = request.Execute(true, store);
        try
        {
            NativeHierarchyDuplicateTransformProbe clone = (NativeHierarchyDuplicateTransformProbe)custody.Node!.Transform;
            Assert.That(clone.First, Is.SameAs(asset));
            Assert.That(clone.Second, Is.SameAs(asset));
            Assert.That(clone.AutoAsset, Is.SameAs(asset));
            Assert.That(custody.RetainedOwnership!.Contains(asset), Is.False);
            Assert.That(Undo.TryUndo(), Is.True);
            Undo.ClearHistory();
            Assert.That(asset.IsDestroyed, Is.False);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership, Is.Not.Null, "Unused serialized asset allocations still need an owner.");
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
        }
        finally { Cleanup(custody); source.Destroy(true); asset.Destroy(true); }
    }

    [Test]
    public void LeafPlanMatchesCanonicalInheritedPrivateAndIgnoredAssetMembers()
    {
        TextFile asset = new();
        NativeHierarchyDuplicateTransformProbe transform = new() { InheritedAsset = asset, IgnoredAsset = asset };
        transform.SetPrivateAsset(asset);
        SceneNode source = new("Source", transform);
        using RuntimeWorld world = World(new XRScene("Hidden", source) { IsVisible = false });
        string serialized = AssetManager.Serializer.Serialize(source);
        Assert.That(serialized, Does.Contain("InheritedAssetAlias").And.Contain("PrivateAssetAlias"));
        Assert.That(serialized, Does.Not.Contain(nameof(NativeHierarchyDuplicateTransformProbe.IgnoredAsset)));
        HierarchyDuplicateRequest request = new([source], world, () => true);
        Assert.That(request.OrdinaryUnavailable, Is.Null);
        HierarchyCloneCustody custody = request.Execute(true, new());
        try
        {
            NativeHierarchyDuplicateTransformProbe clone = (NativeHierarchyDuplicateTransformProbe)custody.Node!.Transform;
            Assert.That(clone.InheritedAsset, Is.SameAs(asset));
            Assert.That(clone.ReadPrivateAsset(), Is.SameAs(asset));
            Assert.That(clone.IgnoredAsset, Is.Null);
        }
        finally { Cleanup(custody); source.Destroy(true); asset.Destroy(true); }
    }

    [Test]
    public void UnsupportedAssetContainerRejectsBeforeConstructionAndDeepDuplicateStillUsesCanonicalClone()
    {
        TextFile asset = new();
        SceneNode source = new("Source", new NativeHierarchyDuplicateTransformProbe { Assets = [asset] });
        using RuntimeWorld world = World(new XRScene("Hidden", source) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        HierarchyDuplicateRequest shallow = new([source], world, () => true);
        Assert.That(shallow.OrdinaryUnavailable, Does.Contain(nameof(NativeHierarchyDuplicateTransformProbe.Assets)));
        Assert.Throws<InvalidOperationException>(() => shallow.Execute(true, store));
        Assert.That(store.CaptureEntries(), Is.Empty);
        HierarchyCloneCustody custody = new HierarchyDuplicateRequest([source], world, () => true).Execute(false, store);
        try
        {
            TextFile clonedAsset = ((NativeHierarchyDuplicateTransformProbe)custody.Node!.Transform).Assets!.Single();
            Assert.That(clonedAsset, Is.Not.SameAs(asset));
            Assert.That(custody.RetainedOwnership!.Contains(clonedAsset), Is.True);
            Assert.That(asset.IsDestroyed, Is.False);
        }
        finally { Cleanup(custody); source.Destroy(true); asset.Destroy(true); }
    }

    [Test]
    public void UnsupportedGetterIsNotInvokedByOrdinaryDuplicateAdmission()
    {
        NativeHierarchyDuplicateGetterProbe transform = new();
        SceneNode source = new("Source", transform);
        using RuntimeWorld world = World(new XRScene("Hidden", source) { IsVisible = false });
        try
        {
            HierarchyDuplicateRequest request = new([source], world, () => true);
            Assert.That(request.OrdinaryUnavailable, Does.Contain(nameof(NativeHierarchyDuplicateGetterProbe.Asset)));
            Assert.That(transform.Reads, Is.Zero);
        }
        finally { source.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourceAssetChangeOrRekeyDuringSerializationRejectsBeforeCloneAllocation(bool rekey)
    {
        TextFile first = new(), second = new();
        NativeHierarchyDuplicateTransformProbe transform = new() { First = first };
        SceneNode source = new("Source", transform);
        using RuntimeWorld world = World(new XRScene("Hidden", source) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        HierarchyDuplicateRequest request = new([source], world, () => true);
        transform.Serializing = () =>
        {
            transform.Serializing = null;
            if (rekey) first.AdoptPersistentID(Guid.NewGuid());
            else transform.First = second;
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => request.Execute(true, store));
            Assert.That(store.CaptureEntries(), Is.Empty);
            Assert.That(Undo.CanUndo, Is.False);
            Assert.That(first.IsDestroyed, Is.False);
            Assert.That(second.IsDestroyed, Is.False);
        }
        finally { transform.Serializing = null; source.Destroy(true); first.Destroy(true); second.Destroy(true); }
    }

    [Test]
    public void RepeatedRequestCannotAllocateAnotherClone()
    {
        SceneNode source = new("Source");
        using RuntimeWorld world = World(new XRScene("Hidden", source) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        HierarchyDuplicateRequest request = new([source], world, () => true);
        Assert.That(request.TryQueue(), Is.True);
        Assert.That(request.TryQueue(), Is.False);
        HierarchyCloneCustody custody = request.Execute(true, store);
        try
        {
            Assert.Throws<InvalidOperationException>(() => request.Execute(true, store));
            Assert.That(store.CaptureEntries().Length, Is.EqualTo(1));
            Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
        }
        finally { Cleanup(custody); source.Destroy(true); }
    }

    [Test]
    public void SelectionObserverReplacementSurvivesUndoAndDetachedRelease()
    {
        SceneNode source = new("Source"), unrelated = new("Unrelated");
        using RuntimeWorld world = World(new XRScene("Hidden", source, unrelated) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        Selection.SceneNodes = [source];
        bool replaced = false;
        void Changed(SceneNode[] nodes)
        {
            if (replaced || nodes.Length != 1 || ReferenceEquals(nodes[0], source)) return;
            replaced = true;
            Selection.SceneNodes = [unrelated];
        }
        Selection.SelectionChanged += Changed;
        HierarchyCloneCustody? custody = null;
        try
        {
            custody = new HierarchyDuplicateRequest([source], world, () => true).Execute(true, store);
            Assert.That(replaced, Is.True);
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { unrelated }));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(Selection.SceneNodes, Is.EqualTo(new[] { unrelated }));
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Released));
        }
        finally
        {
            Selection.SelectionChanged -= Changed;
            if (custody is not null) Cleanup(custody);
            source.Destroy(true); unrelated.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MalformedSilentOrCrossWorldSourceRejectsBeforeConstruction(bool wrongWorld)
    {
        SceneNode source = new("Source"), child = new("Child");
        using RuntimeWorld world = World(new XRScene("Hidden", source) { IsVisible = false });
        using RuntimeWorld foreign = World(new XRScene("Other") { IsVisible = false });
        if (wrongWorld)
        {
            child.Parent = source;
            child.SetWorldContext(foreign);
        }
        else
            source.Transform.Children.Add(child.Transform, false, false);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new HierarchyDuplicateRequest([source], world, () => true));
            Assert.That(child.IsDestroyed, Is.False);
            if (wrongWorld) Assert.That(child.World, Is.SameAs(foreign));
            else Assert.That(child.Parent, Is.Null);
        }
        finally
        {
            source.Transform.Children.Clear(false, false);
            child.Parent = null;
            child.SetWorldContext(null);
            child.Destroy(true); source.Destroy(true);
        }
    }

    [Test]
    public void ForeignHiddenRootBacklinkRejectsSourceAndPreservesAnEscapedClone()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Document", source) { IsVisible = false };
        XRScene foreign = new("Foreign") { IsVisible = false };
        using RuntimeWorld world = World(scene);
        HierarchyCloneCustodyStore store = new();
        try
        {
            foreign.SceneRoots.Add(source);
            Assert.Throws<InvalidOperationException>(() => new HierarchyDuplicateRequest([source], world, () => true));
            foreign.SceneRoots.Remove(source);
            SceneNode? escaped = null;
            Assert.Throws<InvalidOperationException>(() => new HierarchyDuplicateRequest([source], world, () => true)
                .Execute(true, store, clone => { escaped = clone; foreign.SceneRoots.Add(clone); }));
            Assert.That(escaped, Is.Not.Null);
            Assert.That(escaped!.IsDestroyed, Is.False);
            Assert.That(foreign.SceneRoots, Does.Contain(escaped));
            HierarchyCloneCustody retained = store.CaptureEntries().Single();
            Assert.That(retained.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Cleanup(retained);
        }
        finally { foreign.SceneRoots.Clear(); source.Destroy(true); foreign.Destroy(true); }
    }

    [Test]
    public void InitialChildTransferRejectsCompensationAndKeepsTheNewOwner()
    {
        SceneNode source = new("Source"), destination = new("Destination");
        _ = new SceneNode(source, "Child");
        using RuntimeWorld world = World(new XRScene("Hidden", source, destination) { IsVisible = false });
        HierarchyCloneCustodyStore store = new();
        SceneNode? moved = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() => new HierarchyDuplicateRequest([source], world, () => true)
                .Execute(true, store, clone =>
                {
                    moved = clone.GetChild(0)!;
                    moved.Parent = destination;
                }));
            HierarchyCloneCustody retained = store.CaptureEntries().Single();
            Assert.That(retained.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(moved!.Parent, Is.SameAs(destination));
            Assert.That(moved.IsDestroyed, Is.False);
            Assert.That(retained.Node!.IsDestroyed, Is.False);
            Cleanup(retained);
        }
        finally { source.Destroy(true); destination.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LaterChildMoveUsesCurrentDocumentProofForReleaseAndOriginalShapeForUndo(bool release)
    {
        SceneNode source = new("Source"), destination = new("Destination");
        _ = new SceneNode(source, "Child");
        using RuntimeWorld world = World(new XRScene("Hidden", source, destination) { IsVisible = false });
        HierarchyCloneCustody custody = new HierarchyDuplicateRequest([source], world, () => true).Execute(true, new());
        try
        {
            SceneNode moved = custody.Node!.GetChild(0)!;
            moved.Parent = destination;
            if (release)
            {
                Undo.ClearHistory();
                Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Document));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
                Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
                Assert.That(custody.Node!.Parent, Is.Null);
                Assert.That(custody.Node!.IsActiveSelf, Is.True);
            }
            Assert.That(moved.Parent, Is.SameAs(destination));
        }
        finally { Cleanup(custody); source.Destroy(true); destination.Destroy(true); }
    }

    private static RuntimeWorld World(XRScene scene) => new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));

    private static void Cleanup(HierarchyCloneCustody custody)
    {
        XRObjectBase[] allocations = custody.RetainedOwnership?.Objects.ToArray() ?? [];
        Selection.Clear();
        Undo.ClearHistory();
        foreach (SceneNode node in allocations.OfType<SceneNode>())
        {
            Undo.Untrack(node);
            if (node.IsDestroyed) continue;
            foreach (SceneRootMembership membership in node.CaptureSceneRootMemberships())
                while (membership.Collection.Remove(node)) { }
            node.Parent = null;
        }
        foreach (SceneNode node in allocations.OfType<SceneNode>())
            if (!node.IsDestroyed) node.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();
        foreach (TransformBase transform in allocations.OfType<TransformBase>())
            if (!transform.IsDestroyed) transform.Destroy(true);
        foreach (TextFile asset in allocations.OfType<TextFile>())
            if (!asset.IsDestroyed) asset.Destroy(true);
    }
}
