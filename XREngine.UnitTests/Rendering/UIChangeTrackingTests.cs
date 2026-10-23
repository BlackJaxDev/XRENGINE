using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIChangeTrackingTests
{
    private readonly List<UITransform> _created = [];

    [TearDown]
    public void DestroyTransforms()
    {
        for (int i = _created.Count - 1; i >= 0; i--)
            _created[i].Destroy(true);
        _created.Clear();
    }

    [Test]
    public void MeasureMutation_DirtiesOnlyAncestorPath()
    {
        var canvas = Track(new UICanvasTransform { Width = 100, Height = 100 });
        var branch = Track(new UIBoundableTransform { Parent = canvas });
        var leaf = Track(new UIBoundableTransform { Parent = branch });
        var sibling = Track(new UIBoundableTransform { Parent = canvas });
        AcceptLayout(canvas);
        sibling.NeedsMeasure.ShouldBeFalse();
        var siblingRevision = sibling.ChangeRevisions;
        var branchRevision = branch.ChangeRevisions;
        var canvasRevision = canvas.ChangeRevisions;
        Drain(canvas);

        leaf.Width = 25;

        leaf.NeedsMeasure.ShouldBeTrue();
        branch.NeedsMeasure.ShouldBeTrue();
        canvas.NeedsMeasure.ShouldBeTrue();
        sibling.NeedsMeasure.ShouldBeFalse();
        sibling.ChangeRevisions.ShouldBe(siblingRevision);
        branch.ChangeRevisions.ShouldBe(branchRevision);
        canvas.ChangeRevisions.ShouldBe(canvasRevision);
        var changes = Drain(canvas);
        changes.Length.ShouldBe(1);
        changes[0].Element.ShouldBe(leaf.ElementIdentity);
        changes[0].Parent.ShouldBe(branch.ElementIdentity);
        changes[0].Domains.HasFlag(UIChangeDomain.Measure).ShouldBeTrue();
    }

    [Test]
    public void ArrangeMutation_RetainsMeasureCache()
    {
        var canvas = Track(new UICanvasTransform { Width = 100, Height = 100 });
        var leaf = Track(new UIBoundableTransform { Parent = canvas });
        AcceptLayout(canvas);
        leaf.NeedsMeasure.ShouldBeFalse();
        canvas.NeedsMeasure.ShouldBeFalse();
        Drain(canvas);

        leaf.Translation = new Vector2(5, 8);

        leaf.NeedsMeasure.ShouldBeFalse();
        canvas.NeedsMeasure.ShouldBeFalse();
        leaf.NeedsArrange.ShouldBeTrue();
        canvas.NeedsArrange.ShouldBeTrue();
        var changes = Drain(canvas);
        changes.Length.ShouldBe(1);
        changes[0].Domains.HasFlag(UIChangeDomain.Measure).ShouldBeFalse();
        changes[0].Domains.HasFlag(UIChangeDomain.Arrange).ShouldBeTrue();
    }

    [Test]
    public void VisualOnlyChange_DoesNotInvalidateLayout()
    {
        var canvas = Track(new UICanvasTransform { Width = 100, Height = 100 });
        var leaf = Track(new UITransform { Parent = canvas });
        AcceptLayout(canvas);
        Drain(canvas);
        var before = leaf.ChangeRevisions;
        bool arrangeDirtyBefore = leaf.NeedsArrange;

        leaf.RecordUIChange(UIChangeDomain.Visual);

        leaf.NeedsMeasure.ShouldBeFalse();
        leaf.NeedsArrange.ShouldBe(arrangeDirtyBefore);
        canvas.NeedsMeasure.ShouldBeFalse();
        leaf.ChangeRevisions.Visual.ShouldBe(before.Visual + 1);
        leaf.ChangeRevisions.Measure.ShouldBe(before.Measure);
        var changes = Drain(canvas);
        changes.Length.ShouldBe(1);
        changes[0].Domains.ShouldBe(UIChangeDomain.Visual);
    }

    [Test]
    public void DetachAndReattach_RetiresOldCanvasIdentity()
    {
        var oldCanvas = Track(new UICanvasTransform());
        var newCanvas = Track(new UICanvasTransform());
        var leaf = Track(new UITransform { Parent = oldCanvas });
        Drain(oldCanvas);
        var oldIdentity = leaf.ElementIdentity;

        leaf.Parent = null;

        leaf.ElementIdentity.Id.ShouldBe(oldIdentity.Id);
        leaf.ElementIdentity.Incarnation.ShouldBeGreaterThan(oldIdentity.Incarnation);
        var removal = Drain(oldCanvas);
        Array.Exists(removal, change => change.Element == oldIdentity && change.IsRemoved).ShouldBeTrue();

        var detachedIdentity = leaf.ElementIdentity;
        leaf.Parent = newCanvas;
        leaf.ElementIdentity.Incarnation.ShouldBeGreaterThan(detachedIdentity.Incarnation);
        var attached = Drain(newCanvas);
        Array.Exists(attached, change => change.Element == leaf.ElementIdentity && !change.IsRemoved).ShouldBeTrue();
    }

    [Test]
    public void CanvasTransaction_CoalescesChangesToOneRevision()
    {
        var canvas = Track(new UICanvasTransform());
        var leaf = Track(new UIBoundableTransform { Parent = canvas });
        Drain(canvas);
        long previousRevision = canvas.ChangeJournal.Revision;

        using (canvas.BeginMutationTransaction())
        {
            leaf.Width = 20;
            leaf.RecordUIChange(UIChangeDomain.Visual);
            canvas.ChangeJournal.Revision.ShouldBe(previousRevision);
        }

        canvas.ChangeJournal.Revision.ShouldBe(previousRevision + 1);
        var changes = Drain(canvas);
        changes.Length.ShouldBe(1);
        changes[0].Element.ShouldBe(leaf.ElementIdentity);
        changes[0].Domains.HasFlag(UIChangeDomain.Measure | UIChangeDomain.Visual).ShouldBeTrue();
    }

    [Test]
    public void SeparateTransforms_HaveDistinctRawIdentities()
    {
        var first = Track(new UITransform());
        var second = Track(new UIBoundableTransform());
        var third = Track(new UICanvasTransform());

        first.ElementIdentity.IsValid.ShouldBeTrue();
        second.ElementIdentity.IsValid.ShouldBeTrue();
        third.ElementIdentity.IsValid.ShouldBeTrue();
        first.ElementIdentity.Id.ShouldNotBe(second.ElementIdentity.Id);
        second.ElementIdentity.Id.ShouldNotBe(third.ElementIdentity.Id);
        first.ElementIdentity.Id.ShouldNotBe(third.ElementIdentity.Id);
    }

    [Test]
    public void ForeignThreadCannotSetAttachedPropertyDuringTransaction()
    {
        var canvas = Track(new UICanvasTransform());
        var leaf = Track(new UIBoundableTransform { Parent = canvas });
        float? originalWidth = leaf.Width;
        using (canvas.BeginMutationTransaction())
        {
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try { leaf.Width = 42; }
                catch (Exception exception) { failure = exception; }
            });
            worker.Start();
            worker.Join();
            failure.ShouldBeOfType<InvalidOperationException>();
            leaf.Width.ShouldBe(originalWidth);
            leaf.Width = 21;
        }
        leaf.Width.ShouldBe(21f);
    }

    [Test]
    public void MovingStandaloneNestedCanvas_RetiresItsOwnAndChildIdsInOldOwner()
    {
        var outer = Track(new UICanvasTransform());
        var nested = Track(new UICanvasTransform());
        var child = Track(new UITransform { Parent = nested });
        nested.RecordUIChange(UIChangeDomain.Visual);
        child.RecordUIChange(UIChangeDomain.Visual);
        Drain(nested);
        var oldNestedId = nested.ElementIdentity;
        var oldChildId = child.ElementIdentity;

        nested.Parent = outer;

        var oldChanges = Drain(nested);
        Array.Exists(oldChanges, change => change.Element == oldNestedId && change.IsRemoved).ShouldBeTrue();
        Array.Exists(oldChanges, change => change.Element == oldChildId && change.IsRemoved).ShouldBeTrue();
        nested.ElementIdentity.Incarnation.ShouldBeGreaterThan(oldNestedId.Incarnation);
        child.ElementIdentity.Incarnation.ShouldBeGreaterThan(oldChildId.Incarnation);
        var newChanges = Drain(outer);
        Array.Exists(newChanges, change => change.Element == nested.ElementIdentity && !change.IsRemoved).ShouldBeTrue();
        Array.Exists(newChanges, change => change.Element == child.ElementIdentity && !change.IsRemoved).ShouldBeTrue();
    }

    [Test]
    public void MovingNestedCanvasBetweenRoots_RetiresOldIdsInPreviousRoot()
    {
        var firstRoot = Track(new UICanvasTransform());
        var secondRoot = Track(new UICanvasTransform());
        var nested = Track(new UICanvasTransform { Parent = firstRoot });
        var child = Track(new UITransform { Parent = nested });
        nested.RecordUIChange(UIChangeDomain.Visual);
        child.RecordUIChange(UIChangeDomain.Visual);
        Drain(firstRoot);
        var oldNestedId = nested.ElementIdentity;
        var oldChildId = child.ElementIdentity;

        nested.Parent = secondRoot;

        var oldChanges = Drain(firstRoot);
        Array.Exists(oldChanges, change => change.Element == oldNestedId && change.IsRemoved).ShouldBeTrue();
        Array.Exists(oldChanges, change => change.Element == oldChildId && change.IsRemoved).ShouldBeTrue();
        var newChanges = Drain(secondRoot);
        Array.Exists(newChanges, change => change.Element == nested.ElementIdentity && !change.IsRemoved).ShouldBeTrue();
        Array.Exists(newChanges, change => change.Element == child.ElementIdentity && !change.IsRemoved).ShouldBeTrue();
    }

    [Test]
    public void ReversingChildren_RecordsTopologyAndInvalidatesLayout()
    {
        var canvas = Track(new UICanvasTransform { Width = 100, Height = 100 });
        var first = Track(new UITransform { Parent = canvas });
        var second = Track(new UITransform { Parent = canvas });
        AcceptLayout(canvas);
        canvas.NeedsMeasure.ShouldBeFalse();
        Drain(canvas);

        canvas.Children.Reverse();

        canvas.Children[0].ShouldBeSameAs(second);
        canvas.Children[1].ShouldBeSameAs(first);
        canvas.NeedsMeasure.ShouldBeTrue();
        var changes = Drain(canvas);
        Array.Exists(changes, change => change.Element == canvas.ElementIdentity &&
            change.Domains.HasFlag(UIChangeDomain.Topology)).ShouldBeTrue();
    }

    [Test]
    public void ForeignThreadChildListEdits_LeaveOrderAndParentsUnchanged()
    {
        var canvas = Track(new UICanvasTransform());
        var first = Track(new UITransform { Parent = canvas });
        var second = Track(new UITransform { Parent = canvas });
        var candidate = Track(new UITransform());
        using (canvas.BeginMutationTransaction())
        {
            var failures = new Exception?[3];
            var worker = new Thread(() =>
            {
                failures[0] = Capture(() => canvas.Children.Add(candidate));
                failures[1] = Capture(() => canvas.Children.Remove(first));
                failures[2] = Capture(canvas.Children.Reverse);
            });
            worker.Start();
            worker.Join();
            foreach (var failure in failures)
                failure.ShouldBeOfType<InvalidOperationException>();
            canvas.Children.Count.ShouldBe(2);
            canvas.Children[0].ShouldBeSameAs(first);
            canvas.Children[1].ShouldBeSameAs(second);
            first.Parent.ShouldBeSameAs(canvas);
            second.Parent.ShouldBeSameAs(canvas);
            candidate.Parent.ShouldBeNull();
            canvas.Children.Reverse();
        }
        canvas.Children[0].ShouldBeSameAs(second);
    }

    [Test]
    public void ChildIndexReplacement_RetiresOldChildAndSelfAssignmentKeepsReplacement()
    {
        var canvas = Track(new UICanvasTransform());
        var oldChild = Track(new UITransform { Parent = canvas });
        var replacement = Track(new UITransform());
        Drain(canvas);
        var oldId = oldChild.ElementIdentity;

        canvas.Children[0] = replacement;

        oldChild.Parent.ShouldBeNull();
        replacement.Parent.ShouldBeSameAs(canvas);
        canvas.Children[0].ShouldBeSameAs(replacement);
        var replaced = Drain(canvas);
        Array.Exists(replaced, change => change.Element == oldId && change.IsRemoved).ShouldBeTrue();
        Array.Exists(replaced, change => change.Element == replacement.ElementIdentity && !change.IsRemoved).ShouldBeTrue();

        var replacementId = replacement.ElementIdentity;
        canvas.Children[0] = replacement;

        replacement.Parent.ShouldBeSameAs(canvas);
        replacement.ElementIdentity.ShouldBe(replacementId);
        canvas.Children[0].ShouldBeSameAs(replacement);
        var selfAssigned = Drain(canvas);
        Array.Exists(selfAssigned, change => change.Element == replacementId && change.IsRemoved).ShouldBeFalse();
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private static void AcceptLayout(UICanvasTransform canvas)
    {
        canvas.UpdateLayoutAsync();
        SpinWait.SpinUntil(() => canvas.PumpLayoutResults(), TimeSpan.FromSeconds(5)).ShouldBeTrue();
        canvas.CurrentLayoutGeneration.ShouldBeGreaterThan(0UL);
    }

    private static UIElementChange[] Drain(UICanvasTransform canvas)
    {
        var destination = new UIElementChange[canvas.ChangeJournal.Capacity];
        canvas.ChangeJournal.TryDrain(destination, out var batch).ShouldBeTrue();
        return destination.AsSpan(0, batch.Count).ToArray();
    }

    private T Track<T>(T transform) where T : UITransform
    {
        _created.Add(transform);
        return transform;
    }
}
