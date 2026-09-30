using System.Numerics;

namespace XREngine.Components.VR;

/// <summary>Deterministic nearest-pair assignment shared by preview and capture. Does not allocate.</summary>
public static class VrTrackerBinder
{
    public static int Bind(ReadOnlySpan<VrTrackerBindingSlot> slots, ReadOnlySpan<VrTrackerBindingCandidate> trackers,
        float cutoffMeters, Span<VrTrackerBinding> result)
    {
        if (!float.IsFinite(cutoffMeters) || cutoffMeters <= 0)
            return 0;
        int count = 0;
        float cutoffSquared = cutoffMeters * cutoffMeters;
        while (count < Math.Min(slots.Length, result.Length))
        {
            int bestSlot = -1, bestTracker = -1;
            float bestDistance = cutoffSquared;
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                bool assigned = false;
                for (int i = 0; i < count; i++)
                    assigned |= result[i].SlotIndex == slotIndex;
                if (assigned)
                    continue;
                for (int trackerIndex = 0; trackerIndex < trackers.Length; trackerIndex++)
                {
                    ref readonly VrTrackerBindingCandidate tracker = ref trackers[trackerIndex];
                    if (!tracker.Usable || string.IsNullOrEmpty(tracker.Identity))
                        continue;
                    assigned = false;
                    for (int i = 0; i < count; i++)
                        assigned |= string.Equals(trackers[result[i].TrackerIndex].Identity, tracker.Identity, StringComparison.Ordinal);
                    if (assigned)
                        continue;
                    float distance = DistanceSquared(tracker.Position, slots[slotIndex].Start, slots[slotIndex].End);
                    if (!float.IsFinite(distance) || distance > bestDistance)
                        continue;
                    if (distance == bestDistance && bestTracker >= 0)
                    {
                        int identityOrder = string.CompareOrdinal(tracker.Identity, trackers[bestTracker].Identity);
                        if (identityOrder > 0 || identityOrder == 0 && slots[slotIndex].Slot >= slots[bestSlot].Slot)
                            continue;
                    }
                    bestDistance = distance;
                    bestSlot = slotIndex;
                    bestTracker = trackerIndex;
                }
            }
            if (bestTracker < 0)
                break;
            result[count++] = new(bestSlot, bestTracker);
        }
        return count;
    }

    public static float DistanceSquared(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 segment = end - start;
        float denominator = segment.LengthSquared();
        if (denominator < 1e-10f)
            return Vector3.DistanceSquared(point, start);
        float t = Math.Clamp(Vector3.Dot(point - start, segment) / denominator, 0, 1);
        return Vector3.DistanceSquared(point, start + segment * t);
    }
}
