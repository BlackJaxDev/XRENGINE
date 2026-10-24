using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Animation;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class InspectorPropertyDropBindingTests
{
    [Test]
    public void ReceiptCopiesTargetsFiltersNullAndRejectsRetirement()
    {
        InspectorPlainDropTarget first = new(), second = new();
        object?[] targets = [first, null];
        InspectorPropertyDropBinding binding = new(
            typeof(InspectorPlainDropTarget).GetProperty(nameof(first.Reference))!, targets, () => true);
        targets[0] = second;
        Assert.That(binding.Targets, Is.EqualTo(new object?[] { first }));
        Assert.That(binding.IsCurrent, Is.True);
        binding.Retire();
        Assert.That(binding.IsCurrent, Is.False);
        Assert.Throws<InvalidOperationException>(binding.RequireCurrent);
        InspectorPropertyDropBinding empty = new(binding.Property, [null], () => true);
        Assert.That(empty.IsCurrent, Is.False);
    }

    [Test]
    public void ContextRetirementRejectsAnOtherwiseLiveTarget()
    {
        InspectorPlainDropTarget target = new();
        bool current = true;
        InspectorPropertyDropBinding binding = new(
            typeof(InspectorPlainDropTarget).GetProperty(nameof(target.Reference))!, [target], () => current);
        Assert.That(binding.IsCurrent, Is.True);
        current = false;
        Assert.That(binding.IsCurrent, Is.False);
    }

    [Test]
    public void QueuedIdentityRejectsRecreatedObjectEvenWhenItsIdIsRestored()
    {
        AnimationClip target = new() { Name = "Drop payload" };
        Guid id = target.ID;
        EditorDropObjectIdentity queued = new(target);
        try
        {
            Assert.That(queued.IsCurrent, Is.True);
            target.Destroy(true);
            target.Generate();
            target.AdoptPersistentID(id);
            Assert.That(target.ID, Is.EqualTo(id));
            Assert.That(queued.IsCurrent, Is.False);
            Assert.That(new EditorDropObjectIdentity(target).IsCurrent, Is.True);
        }
        finally { target.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }
}
