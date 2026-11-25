using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeDockSlotDetachmentTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void DetachCallbackCanMoveTheSlotToAnotherOwner(bool duringDeactivation)
    {
        SceneNode host = new("Old host");
        SceneNode next = new("New host");
        TestWorldContext world = new();
        host.SetWorldContext(world);
        next.SetWorldContext(world);
        SceneNode slot = host.NewChild("Slot");
        Dictionary<string, SceneNode> slots = new() { ["panel"] = slot };
        XRPropertyChangedEventHandler callback = (_, change) =>
        {
            if (duringDeactivation
                ? change.PropertyName == nameof(SceneNode.IsActiveSelf) && !slot.IsActiveSelf
                : change.PropertyName == nameof(TransformBase.Parent) && slot.Parent is null)
                slot.Transform.SetParent(next.Transform, false, EParentAssignmentMode.Immediate);
        };
        if (duringDeactivation) slot.PropertyChanged += callback;
        else slot.Transform.PropertyChanged += callback;
        try
        {
            if (duringDeactivation)
            {
                NativeDockSlotDetachment.DetachRegistered("panel", slot, host, slots, () => true);
                slots.ShouldBeEmpty();
            }
            else
                // The canonical parent setter reports reentry after its notification.
                Assert.Throws<InvalidOperationException>(() =>
                    NativeDockSlotDetachment.DetachRegistered("panel", slot, host, slots, () => true));
            slot.Parent.ShouldBeSameAs(next);
            slot.World.ShouldBeSameAs(world);
            NativeDockSlotDetachment.DetachRegistered("panel", slot, host, slots, () => true);
            slots.ShouldBeEmpty();
            slot.Parent.ShouldBeSameAs(next);
        }
        finally
        {
            slot.PropertyChanged -= callback;
            slot.Transform.PropertyChanged -= callback;
            host.Destroy(true);
            next.Destroy(true);
            slot.Destroy(true);
        }
    }

    [Test]
    public void DestroyedRegisteredSlotIsForgottenBeforeItsTransformIsRead()
    {
        SceneNode host = new("Host");
        SceneNode slot = host.NewChild("Slot");
        Dictionary<string, SceneNode> slots = new() { ["panel"] = slot };
        try
        {
            slot.Destroy(true);
            NativeDockSlotDetachment.DetachRegistered("panel", slot, host, slots, () => true);
            slots.ShouldBeEmpty();
            slot.IsDestroyed.ShouldBeTrue();
        }
        finally { host.Destroy(true); }
    }

    [Test]
    public void LostSlotMappingDoesNotDeactivateTheNewOwnersPanel()
    {
        SceneNode host = new("Host");
        SceneNode slot = host.NewChild("Slot");
        try
        {
            NativeDockSlotDetachment.Detach(slot, () => false);
            slot.IsActiveSelf.ShouldBeTrue();
            slot.Parent.ShouldBeSameAs(host);
        }
        finally { host.Destroy(true); }
    }

    [Test]
    public void DeactivationCallbackCanReplaceTheSlotTransform()
    {
        SceneNode host = new("Host");
        SceneNode slot = host.NewChild("Slot");
        Transform replacement = new();
        Dictionary<string, SceneNode> slots = new() { ["panel"] = slot };
        XRPropertyChangedEventHandler callback = (_, change) =>
        {
            if (change.PropertyName == nameof(SceneNode.IsActiveSelf) && !slot.IsActiveSelf)
                slot.SetTransform(replacement);
        };
        slot.PropertyChanged += callback;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                NativeDockSlotDetachment.DetachRegistered("panel", slot, host, slots, () => true));
            slot.Transform.ShouldBeSameAs(replacement);
            slot.Parent.ShouldBeSameAs(host);
            slots["panel"].ShouldBeSameAs(slot);
        }
        finally
        {
            slot.PropertyChanged -= callback;
            host.Destroy(true);
            replacement.Destroy(true);
        }
    }

    [Test]
    public void UnchangedSlotLeavesTheParentAndWorld()
    {
        SceneNode host = new("Host");
        host.SetWorldContext(new TestWorldContext());
        SceneNode slot = host.NewChild("Slot");
        try
        {
            NativeDockSlotDetachment.Detach(slot, () => true);
            slot.Parent.ShouldBeNull();
            slot.World.ShouldBeNull();
            slot.IsActiveSelf.ShouldBeFalse();
        }
        finally
        {
            host.Destroy(true);
            slot.Destroy(true);
        }
    }
}
