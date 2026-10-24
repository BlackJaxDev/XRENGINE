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
