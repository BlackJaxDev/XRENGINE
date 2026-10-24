using System.Numerics;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class HierarchyCloneReplayAdmissionTests
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
    public void ClonedHistoryStepsHoldExactAdmissionAcrossOwnedSettersAndMatrixCallbacks()
    {
        SceneNode source = Source(), parent = Parent(), unrelated = new("Unrelated");
        XRScene scene = new("Hidden", source, parent) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustody custody = Place(source, parent, world, new());
        SceneNode clone = custody.Node!;
        Transform transform = (Transform)clone.Transform;
        int propertyCallbacks = 0, matrixCallbacks = 0;
        Guid unrelatedId = unrelated.ID;
        XRPropertyChangedEventHandler changed = (_, change) =>
        {
            if (change.PropertyName is not (nameof(Transform.Translation) or nameof(Transform.Scale))) return;
            propertyCallbacks++;
            Assert.Throws<InvalidOperationException>(clone.Generate);
            Assert.Throws<InvalidOperationException>(() => transform.Children.Destroy(false));
            unrelated.Name = "Allowed callback edit";
        };
        TransformBase.DelRenderMatrixChanged matrix = (_, _) =>
        {
            matrixCallbacks++;
            Assert.Throws<InvalidOperationException>(() => clone.AdoptPersistentID(Guid.NewGuid()));
            unrelated.Generate();
        };
        transform.PropertyChanged += changed;
        transform.RenderMatrixChanged += matrix;
        try
        {
            Assert.That(Undo.PendingUndo.Single().Changes.Select(change => change.PropertyName),
                Does.Contain(nameof(Transform.Translation)).And.Contain(nameof(Transform.Scale)));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Detached));
            Assert.That(propertyCallbacks, Is.GreaterThanOrEqualTo(2));
            Assert.That(matrixCallbacks, Is.GreaterThanOrEqualTo(2));
            Assert.That(unrelated.Name, Is.EqualTo("Allowed callback edit"));
            Assert.That(unrelated.ID, Is.Not.EqualTo(unrelatedId));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.Applied));
            custody.RequireIdentity();
        }
        finally
        {
            transform.PropertyChanged -= changed;
            transform.RenderMatrixChanged -= matrix;
            Cleanup(custody);
            source.Destroy(true);
            parent.Destroy(true);
            unrelated.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OwnedSetterOrMatrixFailureStopsLaterPropertiesAndKeepsRecoverySticky(bool matrixFailure)
    {
        SceneNode source = Source(), parent = Parent();
        XRScene scene = new("Hidden", source, parent) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        HierarchyCloneCustody custody = Place(source, parent, world, new());
        SceneNode clone = custody.Node!;
        Transform transform = (Transform)clone.Transform;
        Vector3 afterScale = transform.Scale;
        int attempts = 0;
        XRPropertyChangedEventHandler changed = (_, change) =>
        {
            if (matrixFailure || change.PropertyName != nameof(Transform.Translation)) return;
            attempts++;
            throw new InvalidOperationException("Expected owned setter failure.");
        };
        TransformBase.DelRenderMatrixChanged matrix = (_, _) =>
        {
            if (!matrixFailure) return;
            attempts++;
            throw new InvalidOperationException("Expected owned matrix callback failure.");
        };
        transform.PropertyChanged += changed;
        transform.RenderMatrixChanged += matrix;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(transform.Scale, Is.EqualTo(afterScale));
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(Undo.CanUndo, Is.True);
            Assert.That(Undo.CanRedo, Is.False);
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(clone.Parent, Is.SameAs(parent));
        }
        finally
        {
            transform.PropertyChanged -= changed;
            transform.RenderMatrixChanged -= matrix;
            Cleanup(custody);
            source.Destroy(true);
            parent.Destroy(true);
        }
    }

    [Test]
    public void AnInterveningCallbackStepCannotMakeTheNextOwnedPropertyAdoptAReusedId()
    {
        SceneNode source = Source(), parent = Parent();
        XRScene scene = new("Hidden", source, parent) { IsVisible = false };
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene(), new XRWorld("World", scene));
        XRPropertyChangedEventHandler? recording = null;
        int callbackSteps = 0;
        Guid originalId = Guid.Empty;
        HierarchyCloneCustody custody = Place(source, parent, world, new(), clone =>
        {
            originalId = clone.ID;
            recording = (_, change) =>
            {
                if (change.PropertyName != nameof(Transform.Translation)) return;
                Undo.RecordStructuralChange("Callback lifetime edit", () =>
                {
                    callbackSteps++;
                    clone.Generate();
                    clone.AdoptPersistentID(originalId);
                }, () => { });
            };
            // This listener precedes Undo's automatic tracker. Its step stays between
            // the captured Scale and Translation steps after exact reconciliation.
            clone.Transform.PropertyChanged += recording;
        });
        SceneNode node = custody.Node!;
        Transform transform = (Transform)node.Transform;
        transform.PropertyChanged -= recording;
        Vector3 afterScale = transform.Scale;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(callbackSteps, Is.EqualTo(1));
            Assert.That(node.ID, Is.EqualTo(originalId));
            Assert.That(transform.Scale, Is.EqualTo(afterScale));
            Assert.That(node.Parent, Is.SameAs(parent));
            Assert.That(custody.State, Is.EqualTo(EHierarchyCloneCustodyState.RecoveryRequired));
            Assert.That(Undo.CanUndo, Is.True);
        }
        finally { Cleanup(custody); source.Destroy(true); parent.Destroy(true); }
    }

    private static SceneNode Source() => new("Source", new Transform(new Vector3(2), new Vector3(8, 6, 4), Quaternion.Identity));
    private static SceneNode Parent() => new("Parent", new Transform(new Vector3(3), new Vector3(3, 2, 1), Quaternion.Identity));

    private static HierarchyCloneCustody Place(SceneNode source, SceneNode parent, RuntimeWorld world,
        HierarchyCloneCustodyStore store, Action<SceneNode>? beforeTracking = null)
    {
        HierarchyCloneCustody custody = store.Reserve(world);
        try
        {
            HierarchyMutation.Execute("Clone with world transform", () =>
            {
                SceneNode clone = custody.Clone(source);
                custody.BeginInitialAdmission();
                clone.IsActiveSelf = false;
                beforeTracking?.Invoke(clone);
                HierarchyMutationOperation operation = new(HierarchyPlacement.Capture(clone, world),
                    HierarchyPlacement.Attached(clone, world, parent.Transform), preserveWorld: true,
                    activeAfter: true, requireIdentity: custody.RequireIdentity);
                operation.SetPropertyReplayGuard(new HierarchyClonePropertyReplayGuard(custody, operation));
                return operation;
            }, cloneCustody: custody);
            return custody;
        }
        finally { custody.CompleteCommand(); }
    }

    private static void Cleanup(HierarchyCloneCustody custody)
    {
        Undo.ClearHistory();
        if (custody.Node is not { } node) return;
        Undo.Untrack(node);
        node.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();
    }
}
