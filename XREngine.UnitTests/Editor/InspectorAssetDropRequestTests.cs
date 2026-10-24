using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class InspectorAssetDropRequestTests
{
    private static readonly PropertyInfo ReferenceProperty = typeof(InspectorDropTestTarget)
        .GetProperty(nameof(InspectorDropTestTarget.Reference))!;

    [SetUp] public void SetUp() => Undo.ClearHistory();
    [TearDown] public void TearDown() => Undo.ClearHistory();

    [Test]
    public void SubscriptionRetirementDetachesAfterTheAddAccessorReturns()
    {
        InspectorSubscriptionDropTarget target = new();
        PropertyInfo property = typeof(InspectorSubscriptionDropTarget)
            .GetProperty(nameof(target.Reference))!;
        InspectorAssetDropRequest request = new(new(property, [target], () => true), () => true);
        target.OnSubscribe = request.Retire;
        Assert.Throws<InvalidOperationException>(request.ObserveAndCapture);
        Assert.That(target.SubscriberCount, Is.Zero);
        request.Retire();
        Assert.That(target.SubscriberCount, Is.Zero);
    }

    [Test]
    public void GetterRetirementRejectsInitialCapture()
    {
        InspectorDropTestTarget target = new();
        InspectorPropertyDropBinding binding = new(ReferenceProperty, [target], () => true);
        InspectorAssetDropRequest request = new(binding, () => true);
        target.OnRead = binding.Retire;
        try { Assert.Throws<InvalidOperationException>(request.ObserveAndCapture); }
        finally { request.Retire(); }
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void GetterRetirementRejectsCompletionBeforeSetter()
    {
        object original = new();
        InspectorDropTestTarget target = new() { Reference = original };
        InspectorPropertyDropBinding binding = new(ReferenceProperty, [target], () => true);
        InspectorAssetDropRequest request = new(binding, () => true);
        request.ObserveAndCapture();
        Assert.That(request.TryBeginCompletion(), Is.True);
        target.OnRead = binding.Retire;
        try { Assert.Throws<InvalidOperationException>(request.ValidateValues); }
        finally { target.OnRead = null; request.Complete(); }
        Assert.That(target.Reference, Is.SameAs(original));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void OwnNotificationsAllowOneGroupedMultiTargetUndo()
    {
        object firstValue = new(), secondValue = new(), next = new();
        InspectorDropTestTarget first = new() { Reference = firstValue };
        InspectorDropTestTarget second = new() { Reference = secondValue };
        InspectorAssetDropRequest request = new(new(ReferenceProperty, [first, second], () => true), () => true);
        request.ObserveAndCapture();
        Assert.That(request.TryBeginCompletion(), Is.True);
        try
        {
            request.ValidateValues();
            request.Access.SetReferenceValue(next, request.RequireCurrent, request);
            request.ValidateValues();
            Assert.That(request.TryBeginCompletion(), Is.False);
        }
        finally { request.Complete(); }
        Assert.That(first.Reference, Is.SameAs(next));
        Assert.That(second.Reference, Is.SameAs(next));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(first.Reference, Is.SameAs(firstValue));
        Assert.That(second.Reference, Is.SameAs(secondValue));
        Assert.That(Undo.CanUndo, Is.False);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(first.Reference, Is.SameAs(next));
        Assert.That(second.Reference, Is.SameAs(next));
    }

    [Test]
    public void RetirementInsideFirstSetterKeepsItsUndoAndStopsTheNextTarget()
    {
        object firstValue = new(), secondValue = new(), next = new();
        InspectorDropTestTarget first = new() { Reference = firstValue };
        InspectorDropTestTarget second = new() { Reference = secondValue };
        InspectorPropertyDropBinding binding = new(ReferenceProperty, [first, second], () => true);
        InspectorAssetDropRequest request = new(binding, () => true);
        request.ObserveAndCapture();
        request.TryBeginCompletion();
        first.AfterWrite = binding.Retire;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                request.Access.SetReferenceValue(next, request.RequireCurrent, request));
        }
        finally { first.AfterWrite = null; request.Complete(); }
        Assert.That(first.Reference, Is.SameAs(next));
        Assert.That(second.Reference, Is.SameAs(secondValue));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(first.Reference, Is.SameAs(firstValue));
        Assert.That(second.Reference, Is.SameAs(secondValue));
    }

    [Test]
    public void LaterTargetChangeAndRestoreInvalidatesRemainingWrites()
    {
        object original = new(), next = new();
        InspectorDropTestTarget first = new() { Reference = original };
        InspectorDropTestTarget second = new() { Reference = original };
        InspectorAssetDropRequest request = new(new(ReferenceProperty, [first, second], () => true), () => true);
        request.ObserveAndCapture();
        request.TryBeginCompletion();
        first.AfterWrite = () => { second.Reference = new object(); second.Reference = original; };
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                request.Access.SetReferenceValue(next, request.RequireCurrent, request));
            Assert.That(second.Reference, Is.SameAs(original));
        }
        finally { first.AfterWrite = null; request.Complete(); }
    }

    [Test]
    public void NonNotifyingGetterCannotTurnAConflictIntoANoOp()
    {
        object original = new(), next = new();
        InspectorPlainDropTarget first = new() { Stored = original };
        InspectorPlainDropTarget second = new() { Stored = original };
        PropertyInfo property = typeof(InspectorPlainDropTarget).GetProperty(nameof(first.Reference))!;
        InspectorAssetDropRequest request = new(new(property, [first, second], () => true), () => true);
        request.ObserveAndCapture();
        request.TryBeginCompletion();
        request.ValidateValues();
        first.OnRead = () => first.Stored = next;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                request.Access.SetReferenceValue(next, request.RequireCurrent, request));
            Assert.That(first.Writes, Is.Zero);
            Assert.That(second.Writes, Is.Zero);
            Assert.That(second.Stored, Is.SameAs(original));
        }
        finally { first.OnRead = null; request.Complete(); }
    }

    [Test]
    public void ReplacedPendingRequestAndDuplicateCompletionCannotAcquireTheNewDrop()
    {
        InspectorDropTestTarget target = new();
        InspectorAssetDropRequest? pending = null;
        InspectorAssetDropRequest? first = null;
        first = new(new(ReferenceProperty, [target], () => true), () => ReferenceEquals(pending, first));
        pending = first;
        first.ObserveAndCapture();
        InspectorAssetDropRequest? replacement = null;
        replacement = new(new(ReferenceProperty, [target], () => true), () => ReferenceEquals(pending, replacement));
        pending = replacement;
        first.Retire();
        replacement.ObserveAndCapture();
        try
        {
            Assert.Throws<InvalidOperationException>(first.RequireCurrent);
            Assert.That(first.TryBeginCompletion(), Is.True);
            first.Complete();
            Assert.That(first.TryBeginCompletion(), Is.False);
            Assert.DoesNotThrow(replacement.RequireCurrent);
        }
        finally { replacement.Retire(); }
    }
}
