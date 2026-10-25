using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Retires one exact closed panel slot without replacing callback state.</summary>
internal static class NativeDockClosedSlotRetirement
{
    internal static bool TryDestroy(Dictionary<string, SceneNode> slots, string panel,
        SceneNode slot)
    {
        if (!slots.TryGetValue(panel, out SceneNode? current) ||
            !ReferenceEquals(current, slot) || slot.IsDestroyed || slot.IsDestroyQueued ||
            slot.HasDestructionAdmission)
            return false;
        try { slot.Destroy(true); }
        finally
        {
            // A veto keeps the live slot. A callback replacement owns its new entry.
            if (slot.IsDestroyed && slots.TryGetValue(panel, out current) &&
                ReferenceEquals(current, slot))
                slots.Remove(panel);
        }
        return slot.IsDestroyed;
    }
}
