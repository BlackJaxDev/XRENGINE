using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeDockClosedSlotRetirementTests
{
    [Test]
    public void VetoKeepsTheExactLiveSlot()
    {
        SceneNode slot = new("Closed panel slot");
        Dictionary<string, SceneNode> slots = new() { ["panel"] = slot };
        Func<XRObjectBase, bool> veto = _ => false;
        slot.Destroying += veto;
        try
        {
            NativeDockClosedSlotRetirement.TryDestroy(slots, "panel", slot).ShouldBeFalse();
            slot.IsDestroyed.ShouldBeFalse();
            slots["panel"].ShouldBeSameAs(slot);
        }
        finally
        {
            slot.Destroying -= veto;
            slot.Destroy(true);
        }
    }

    [Test]
    public void CallbackReplacementIsNeverRemoved()
    {
        SceneNode slot = new("Old panel slot");
        SceneNode replacement = new("New panel slot");
        Dictionary<string, SceneNode> slots = new() { ["panel"] = slot };
        slot.Destroying += _ =>
        {
            slots["panel"] = replacement;
            return true;
        };
        try
        {
            NativeDockClosedSlotRetirement.TryDestroy(slots, "panel", slot).ShouldBeTrue();
            slot.IsDestroyed.ShouldBeTrue();
            slots["panel"].ShouldBeSameAs(replacement);
        }
        finally
        {
            if (!slot.IsDestroyed) slot.Destroy(true);
            replacement.Destroy(true);
        }
    }

    [Test]
    public void ThrowingDestroyKeepsTheUnfinishedSlot()
    {
        SceneNode slot = new("Throwing panel slot");
        Dictionary<string, SceneNode> slots = new() { ["panel"] = slot };
        InvalidOperationException failure = new("Expected destroying failure.");
        Func<XRObjectBase, bool> throwing = _ => throw failure;
        slot.Destroying += throwing;
        try
        {
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                NativeDockClosedSlotRetirement.TryDestroy(slots, "panel", slot)),
                Is.SameAs(failure));
            slot.IsDestroyed.ShouldBeFalse();
            slots["panel"].ShouldBeSameAs(slot);
        }
        finally
        {
            slot.Destroying -= throwing;
            slot.Destroy(true);
        }
    }

    [Test]
    public void NestedRetirementDoesNotReenterTheSameDestruction()
    {
        SceneNode slot = new("Reentrant panel slot");
        Dictionary<string, SceneNode> slots = new() { ["panel"] = slot };
        int destroyingCalls = 0;
        slot.Destroying += _ =>
        {
            destroyingCalls++;
            NativeDockClosedSlotRetirement.TryDestroy(slots, "panel", slot).ShouldBeFalse();
            slots["panel"].ShouldBeSameAs(slot);
            return true;
        };
        try
        {
            NativeDockClosedSlotRetirement.TryDestroy(slots, "panel", slot).ShouldBeTrue();
            destroyingCalls.ShouldBe(1);
            slot.IsDestroyed.ShouldBeTrue();
            slots.ContainsKey("panel").ShouldBeFalse();
        }
        finally { if (!slot.IsDestroyed) slot.Destroy(true); }
    }
}
