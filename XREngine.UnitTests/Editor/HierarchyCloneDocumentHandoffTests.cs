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
public sealed class HierarchyCloneDocumentHandoffTests
{
    [SetUp]
    public void SetUp() => Undo.ClearHistory();

    [TearDown]
    public void TearDown()
    {
        Undo.ClearHistory();
        XRObjectBase.ProcessPendingDestructions();
    }

    [Test]
    public void AppliedHistoryHandsOffLiveRootsAndRetiresOnlyPrivateConstructorStorage()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Authored", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        Transform? spare = null;
        EventList<SceneNode>? temporary = null;
        HierarchyCloneCustody custody = Clone(source, world, store, () =>
        {
            spare = new Transform();
            temporary = new EventList<SceneNode>();
        });
        SceneNode clone = custody.Node!;
        ObjectConstructionOwnership original = custody.RetainedOwnership!;
        try
        {
            Undo.ClearHistory();
            Assert.Multiple(() =>
            {
                Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Document));
                Assert.That(custody.RetainedOwnership, Is.Null);
                Assert.That(custody.DocumentRetirement, Is.Null);
                Assert.That(custody.DocumentRetirementOwnership, Is.Null);
                Assert.That(store.CaptureEntries(), Is.Empty);
                Assert.That(spare!.IsDestroyed, Is.True);
                Assert.That(clone.IsDestroyed, Is.False);
                Assert.That(source.IsDestroyed, Is.False);
                Assert.That(scene.SceneRoots, Does.Contain(clone));
                Assert.That(custody.Node, Is.SameAs(clone));
                Assert.That(temporary!.IsDestroyed, Is.False);
                Assert.That(temporary, Is.Empty);
            });
            world.Dispose();
            Assert.That(scene.SceneRoots, Does.Contain(clone));
            Assert.That(clone.IsDestroyed, Is.False);
            using RuntimeWorld next = new(new HierarchyTestPhysicsScene(), new XRWorld("Next"));
            store.Reserve(next).CompleteCommand();
            Assert.That(store.CaptureEntries(), Is.Empty);
        }
        finally { Cleanup(original); temporary!.Destroy(true); source.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TemporaryBorrowedContentRetainsCustodyWithoutDestroyingItsItems(bool populatedDuringCleanup)
    {
        SceneNode source = new("Borrowed source");
        XRScene scene = new("Authored", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        Transform? spare = null;
        EventList<SceneNode>? temporary = null;
        HierarchyCloneCustody custody = Clone(source, world, store, () =>
        {
            spare = new Transform();
            temporary = new EventList<SceneNode>();
            if (!populatedDuringCleanup)
                temporary.Add(source);
        });
        ObjectConstructionOwnership ownership = custody.RetainedOwnership!;
        int attempts = 0;
        Func<XRObjectBase, bool> callback = _ =>
        {
            attempts++;
            if (populatedDuringCleanup)
                temporary!.Add(source);
            return true;
        };
        spare!.Destroying += callback;
        try
        {
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership, Is.SameAs(ownership));
            Assert.That(ownership.Contains(source), Is.False);
            Assert.That(temporary, Is.EqualTo(new[] { source }));
            Assert.That(temporary!.IsDestroyed, Is.False);
            Assert.That(source.IsDestroyed, Is.False);
            Assert.That(custody.Node!.IsDestroyed, Is.False);
            Assert.That(scene.SceneRoots, Does.Contain(source));
            Assert.That(attempts, Is.EqualTo(populatedDuringCleanup ? 1 : 0));
            Assert.That(spare.IsDestroyed, Is.EqualTo(populatedDuringCleanup));
            if (populatedDuringCleanup)
            {
                Assert.That(custody.DocumentRetirement!.State, Is.EqualTo(EOwnedHierarchyLifetimeState.Retired));
                Assert.That(custody.DocumentRetirementOwnership!.Contains(spare), Is.True);
            }
            else
                Assert.That(custody.DocumentRetirement, Is.Null);
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
        }
        finally
        {
            spare.Destroying -= callback;
            temporary!.Clear();
            Cleanup(ownership);
            temporary.Destroy(true);
            source.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnsupportedAllocationRejectsBeforeAnyPrivateRetirement(bool customTransform)
    {
        SceneNode source = new("Source");
        XRScene scene = new("Authored", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        Transform? spare = null;
        XRObjectBase? unsupported = null;
        HierarchyCloneCustody custody = Clone(source, world, store, () =>
        {
            spare = new Transform();
            unsupported = customTransform ? new OwnedCloneTransformProbe() : new TextFile();
        });
        ObjectConstructionOwnership ownership = custody.RetainedOwnership!;
        int attempts = 0;
        Func<XRObjectBase, bool> callback = _ => { attempts++; return true; };
        spare!.Destroying += callback;
        try
        {
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership, Is.SameAs(ownership));
            Assert.That(custody.DocumentRetirement, Is.Null);
            Assert.That(attempts, Is.Zero);
            Assert.That(unsupported!.IsDestroyed, Is.False);
            Assert.That(custody.Node!.IsDestroyed, Is.False);
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
        }
        finally { spare.Destroying -= callback; Cleanup(ownership); unsupported!.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void PrivateRetirementVetoRetainsTheWholeLedgerAndDoesNotRetry()
    {
        SceneNode source = new("Source");
        XRScene scene = new("Authored", source) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        Transform? spare = null;
        HierarchyCloneCustody custody = Clone(source, world, store, () => spare = new Transform());
        ObjectConstructionOwnership ownership = custody.RetainedOwnership!;
        int attempts = 0;
        Func<XRObjectBase, bool> veto = _ => { attempts++; return false; };
        spare!.Destroying += veto;
        try
        {
            Undo.ClearHistory();
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership, Is.SameAs(ownership));
            Assert.That(custody.DocumentRetirement!.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(spare.IsDestroyed, Is.False);
            Assert.That(custody.Node!.IsDestroyed, Is.False);
        }
        finally { spare.Destroying -= veto; Cleanup(ownership); source.Destroy(true); }
    }

    [TestCase("WorldLifetime")]
    [TestCase("SceneLifetime")]
    [TestCase("SceneList")]
    public void SourceChangeDuringPrivateRetirementRetainsCompletedProgress(string change)
    {
        SceneNode source = new("Source");
        XRScene scene = new("Authored", source) { IsVisible = false };
        XRWorld asset = new("World", scene);
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), asset);
        HierarchyCloneCustodyStore store = new();
        Transform? spare = null;
        HierarchyCloneCustody custody = Clone(source, world, store, () => spare = new Transform());
        ObjectConstructionOwnership ownership = custody.RetainedOwnership!;
        int attempts = 0;
        Func<XRObjectBase, bool> callback = _ =>
        {
            attempts++;
            if (change == "WorldLifetime") asset.Generate();
            else if (change == "SceneLifetime") scene.Generate();
            else asset.Scenes.Remove(scene);
            return true;
        };
        spare!.Destroying += callback;
        try
        {
            Undo.ClearHistory();
            Undo.ClearHistory();
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(custody.RetainedOwnership, Is.SameAs(ownership));
            Assert.That(custody.DocumentRetirement!.State, Is.EqualTo(EOwnedHierarchyLifetimeState.Retired));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(spare.IsDestroyed, Is.True);
            Assert.That(custody.DocumentRetirementOwnership!.Contains(spare), Is.True);
            Assert.That(custody.DocumentRetirementOwnership.Contains(custody.Node!), Is.False);
            Assert.That(custody.DocumentRetirementOwnership.Identities,
                Is.EqualTo(ownership.Identities.Where(identity => custody.DocumentRetirementOwnership.Contains(identity.Target))));
            Assert.That(custody.Node!.IsDestroyed, Is.False);
            Assert.That(scene.SceneRoots, Does.Contain(custody.Node));
            Assert.Throws<InvalidOperationException>(() => store.Reserve(world));
        }
        finally { spare.Destroying -= callback; Cleanup(ownership); source.Destroy(true); }
    }

    [Test]
    public void PrivateCleanupCannotMoveAnAdoptedEndpointDuringHandoff()
    {
        SceneNode source = new("Source"), destination = new("Destination");
        XRScene scene = new("Authored", source, destination) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustodyStore store = new();
        Transform? spare = null;
        HierarchyCloneCustody custody = Clone(source, world, store, () => spare = new Transform());
        SceneNode clone = custody.Node!;
        ObjectConstructionOwnership ownership = custody.RetainedOwnership!;
        bool rejected = false;
        Func<XRObjectBase, bool> callback = _ =>
        {
            Assert.Throws<InvalidOperationException>(() => clone.Parent = destination);
            rejected = true;
            return true;
        };
        spare!.Destroying += callback;
        try
        {
            Undo.ClearHistory();
            Assert.That(rejected, Is.True);
            Assert.That(custody.RetainedOwnership, Is.Null);
            Assert.That(store.CaptureEntries(), Is.Empty);
            Assert.That(clone.Parent, Is.Null);
            Assert.That(scene.SceneRoots, Does.Contain(clone));
        }
        finally { spare.Destroying -= callback; Cleanup(ownership); source.Destroy(true); destination.Destroy(true); }
    }

    private static HierarchyCloneCustody Clone(SceneNode source, RuntimeWorld world,
        HierarchyCloneCustodyStore store, Action allocate)
    {
        HierarchyPlacement placement = HierarchyPlacement.Capture(source, world);
        return HierarchyCloneMutation.Execute("Clone", source, world,
            clone => HierarchyPlacement.RootLike(clone, placement), store: store,
            configure: (_, _, _) => allocate());
    }

    private static void Cleanup(ObjectConstructionOwnership ownership)
    {
        Undo.ClearHistory();
        foreach (SceneNode node in ownership.Objects.OfType<SceneNode>())
        {
            Undo.Untrack(node);
            if (node.IsDestroyed) continue;
            foreach (SceneRootMembership membership in node.CaptureSceneRootMemberships())
                while (membership.Collection.Remove(node)) { }
            node.Parent = null;
        }
        foreach (SceneNode node in ownership.Objects.OfType<SceneNode>())
            if (!node.IsDestroyed) node.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();
        foreach (TransformBase transform in ownership.Objects.OfType<TransformBase>())
            if (!transform.IsDestroyed && transform.SceneNode is null && transform.Parent is null)
                transform.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();
    }
}
