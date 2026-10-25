using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Detaches a retained panel slot before its dock chrome is rebuilt.</summary>
internal static class NativeDockSlotDetachment
{
    internal static void DetachRegistered(string panel, SceneNode slot, SceneNode host,
        Dictionary<string, SceneNode> slots, Func<bool> hostAlive)
    {
        bool Owned() => hostAlive() && slots.TryGetValue(panel, out SceneNode? current) &&
            ReferenceEquals(current, slot);
        if (!Owned()) return;
        if (slot.IsDestroyed || slot.IsDestroyQueued)
        {
            slots.Remove(panel);
            return;
        }
        if (slot.Parent is not null && !IsUnderHost(slot, host))
        {
            slots.Remove(panel);
            return;
        }
        if (Detach(slot, Owned) || !Owned()) return;
        if (!slot.IsDestroyed && !slot.IsDestroyQueued && IsUnderHost(slot, host))
            throw new InvalidOperationException(
                "A native dock panel changed during detachment; the rebuild stopped before chrome teardown.");
        // A callback transferred this slot. A later stack build must not take it back.
        slots.Remove(panel);
    }

    private static bool IsUnderHost(SceneNode slot, SceneNode host)
    {
        for (TransformBase? parent = slot.Transform.Parent; parent is not null; parent = parent.Parent)
            if (ReferenceEquals(parent, host.Transform)) return true;
        return false;
    }

    internal static bool Detach(SceneNode slot, Func<bool> stillOwned)
    {
        if (!stillOwned() || slot.IsDestroyed || slot.IsDestroyQueued)
            return false;
        if (slot.Transform.Parent is not { } parent)
            return slot.World is null && !slot.IsActiveSelf;
        TransformBase transform = slot.Transform;
        var world = transform.World;
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(slot);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(transform);
        ObjectConstructionIdentity parentIdentity = ObjectConstructionIdentity.Capture(parent);

        bool Current(TransformBase? expectedParent) => stillOwned() &&
            nodeIdentity.IsCurrent && transformIdentity.IsCurrent && parentIdentity.IsCurrent &&
            !slot.IsDestroyed && !slot.IsDestroyQueued &&
            !transform.IsDestroyed && !transform.IsDestroyQueued &&
            !parent.IsDestroyed && !parent.IsDestroyQueued &&
            ReferenceEquals(slot.Transform, transform) &&
            ReferenceEquals(transform.Parent, expectedParent) &&
            ReferenceEquals(transform.World, world);

        slot.IsActiveSelf = false;
        if (slot.IsActiveSelf || !Current(parent))
            return false;
        transform.SetParent(null, false, EParentAssignmentMode.Immediate);
        if (slot.IsActiveSelf || !Current(null))
            return false;
        // A plain detach retains World. Clear it only while this pass still owns the detach.
        transform.SetWorldContext(null);
        return stillOwned() && nodeIdentity.IsCurrent && transformIdentity.IsCurrent &&
            !slot.IsDestroyed && !slot.IsDestroyQueued &&
            !transform.IsDestroyed && !transform.IsDestroyQueued &&
            ReferenceEquals(slot.Transform, transform) && transform.Parent is null &&
            transform.World is null && !slot.IsActiveSelf;
    }
}
