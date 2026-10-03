using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Data.Components.Scene;

namespace XREngine.Components.VR;

/// <summary>Assigns tracked points to body segments by nearest available pair with stable identity ties.</summary>
internal static class VrTrackerNearestAssignment
{
    public static void Assign(
        ReadOnlySpan<(VRTrackerTransform Device, Matrix4x4 World)> samples,
        ReadOnlySpan<EHumanoidIKTarget> slots,
        ReadOnlySpan<Vector3> segmentStarts,
        ReadOnlySpan<Vector3> segmentEnds,
        ReadOnlySpan<bool> segmentAvailable,
        float cutoffSquared,
        Span<VRTrackerTransform?> assignments)
    {
        if (segmentStarts.Length != slots.Length || segmentEnds.Length != slots.Length ||
            segmentAvailable.Length != slots.Length)
            throw new ArgumentException("Each tracker slot needs one body segment.");

        assignments.Clear();
        for (int pair = 0; pair < slots.Length; pair++)
        {
            VRTrackerTransform? bestTracker = null;
            EHumanoidIKTarget bestSlot = default;
            float bestDistance = cutoffSquared;
            for (int i = 0; i < samples.Length; i++)
            {
                VRTrackerTransform tracker = samples[i].Device;
                if (IsAssigned(assignments, tracker))
                    continue;
                for (int s = 0; s < slots.Length; s++)
                {
                    EHumanoidIKTarget slot = slots[s];
                    if (!segmentAvailable[s] || assignments[(int)slot] is not null)
                        continue;
                    float distance = DistanceToSegmentSquared(samples[i].World.Translation,
                        segmentStarts[s], segmentEnds[s]);
                    int identityOrder = bestTracker is null ? -1 :
                        string.CompareOrdinal(tracker.SessionIdentity, bestTracker.SessionIdentity);
                    if (distance > bestDistance || (distance == bestDistance && bestTracker is not null &&
                        (identityOrder > 0 || identityOrder == 0 && slot >= bestSlot)))
                        continue;
                    bestTracker = tracker;
                    bestSlot = slot;
                    bestDistance = distance;
                }
            }
            if (bestTracker is null)
                break;
            assignments[(int)bestSlot] = bestTracker;
        }
    }

    private static bool IsAssigned(ReadOnlySpan<VRTrackerTransform?> assignments, VRTrackerTransform tracker)
    {
        for (int i = 0; i < assignments.Length; i++)
            if (ReferenceEquals(assignments[i], tracker))
                return true;
        return false;
    }

    private static float DistanceToSegmentSquared(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 segment = b - a;
        float length = segment.LengthSquared();
        if (length < 1e-8f)
            return Vector3.DistanceSquared(point, a);
        float t = Math.Clamp(Vector3.Dot(point - a, segment) / length, 0.0f, 1.0f);
        return Vector3.DistanceSquared(point, a + segment * t);
    }
}
