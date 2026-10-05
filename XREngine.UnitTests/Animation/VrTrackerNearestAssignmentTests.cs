using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;

namespace XREngine.UnitTests.Animation;

/// <summary>Checks proximity assignment independently of tracker enumeration order.</summary>
[TestFixture]
public sealed class VrTrackerNearestAssignmentTests
{
    [Test]
    public void Assignment_IsPermutationStableAndExcludesDistantTrackers()
    {
        VRTrackerTransform hips = Tracker("tracker-hips");
        VRTrackerTransform chest = Tracker("tracker-chest");
        VRTrackerTransform foot = Tracker("tracker-foot");
        VRTrackerTransform distant = Tracker("tracker-distant");
        EHumanoidIKTarget[] slots =
        [
            EHumanoidIKTarget.Hips, EHumanoidIKTarget.Chest,
            EHumanoidIKTarget.LeftFoot, EHumanoidIKTarget.RightFoot,
        ];
        Vector3[] points =
        [
            new(0f, 1f, 0f), new(0f, 1.5f, 0f),
            new(-0.2f, 0.05f, 0f), new(0.2f, 0.05f, 0f),
        ];
        bool[] available = [true, true, true, true];
        (VRTrackerTransform Device, Matrix4x4 World)[] samples =
        [
            (foot, Matrix4x4.CreateTranslation(-0.19f, 0.06f, 0f)),
            (hips, Matrix4x4.CreateTranslation(0.01f, 1.01f, 0f)),
            (distant, Matrix4x4.CreateTranslation(9f, 1f, 0f)),
            (chest, Matrix4x4.CreateTranslation(0.01f, 1.49f, 0f)),
        ];
        VRTrackerTransform?[] forward = new VRTrackerTransform?[11];
        VRTrackerTransform?[] reversed = new VRTrackerTransform?[11];
        reversed[(int)EHumanoidIKTarget.RightFoot] = distant;

        VrTrackerNearestAssignment.Assign(samples, slots, points, points, available, 0.1f * 0.1f, forward);
        Array.Reverse(samples);
        VrTrackerNearestAssignment.Assign(samples, slots, points, points, available, 0.1f * 0.1f, reversed);

        forward[(int)EHumanoidIKTarget.Hips].ShouldBeSameAs(hips);
        forward[(int)EHumanoidIKTarget.Chest].ShouldBeSameAs(chest);
        forward[(int)EHumanoidIKTarget.LeftFoot].ShouldBeSameAs(foot);
        forward[(int)EHumanoidIKTarget.RightFoot].ShouldBeNull();
        for (int i = 0; i < forward.Length; i++)
            reversed[i].ShouldBeSameAs(forward[i]);
    }

    [Test]
    public void EqualDistance_PrefersIdentityThenStableSlotOrder()
    {
        VRTrackerTransform first = Tracker("a-tracker");
        VRTrackerTransform second = Tracker("z-tracker");
        EHumanoidIKTarget[] slots = [EHumanoidIKTarget.Hips, EHumanoidIKTarget.Chest];
        Vector3[] points = [new(0f, 1f, 0f), new(0f, 1f, 0f)];
        bool[] available = [true, true];
        (VRTrackerTransform Device, Matrix4x4 World)[] samples =
        [
            (second, Matrix4x4.CreateTranslation(0f, 1f, 0f)),
            (first, Matrix4x4.CreateTranslation(0f, 1f, 0f)),
        ];
        VRTrackerTransform?[] assignments = new VRTrackerTransform?[11];
        VrTrackerNearestAssignment.Assign(samples, slots, points, points, available, 0.01f, assignments);

        assignments[(int)EHumanoidIKTarget.Hips].ShouldBeSameAs(first);
        assignments[(int)EHumanoidIKTarget.Chest].ShouldBeSameAs(second);
    }

    [Test]
    public void EightBodyTrackers_AllEnumerationPermutationsPreserveAssignmentsWithDistantExtras()
    {
        EHumanoidIKTarget[] slots =
        [
            EHumanoidIKTarget.Hips, EHumanoidIKTarget.Chest,
            EHumanoidIKTarget.LeftFoot, EHumanoidIKTarget.RightFoot,
            EHumanoidIKTarget.LeftElbow, EHumanoidIKTarget.RightElbow,
            EHumanoidIKTarget.LeftKnee, EHumanoidIKTarget.RightKnee,
        ];
        Vector3[] points =
        [
            new(0f, 1f, 0f), new(0f, 1.5f, 0f),
            new(-0.25f, 0.05f, 0f), new(0.25f, 0.05f, 0f),
            new(-0.55f, 1.3f, 0f), new(0.55f, 1.3f, 0f),
            new(-0.2f, 0.5f, 0f), new(0.2f, 0.5f, 0f),
        ];
        bool[] available = [true, true, true, true, true, true, true, true];
        VRTrackerTransform[] trackers = new VRTrackerTransform[10];
        for (int i = 0; i < trackers.Length; i++)
            trackers[i] = Tracker($"tracker-{i:D2}");
        (VRTrackerTransform Device, Matrix4x4 World)[] samples = new (VRTrackerTransform, Matrix4x4)[10];
        VRTrackerTransform?[] assignments = new VRTrackerTransform?[11];
        int[] order = [0, 1, 2, 3, 4, 5, 6, 7];
        int permutations = 0;
        do
        {
            for (int i = 0; i < order.Length; i++)
                samples[i] = (trackers[order[i]], Matrix4x4.CreateTranslation(points[order[i]]));
            // Distant extras exercise cutoff rejection on both sides of the body.
            samples[8] = (trackers[8], Matrix4x4.CreateTranslation(-9f, 1f, 0f));
            samples[9] = (trackers[9], Matrix4x4.CreateTranslation(9f, 1f, 0f));
            VrTrackerNearestAssignment.Assign(samples, slots, points, points, available, 0.1f * 0.1f, assignments);
            for (int i = 0; i < slots.Length; i++)
                if (!ReferenceEquals(assignments[(int)slots[i]], trackers[i]))
                    Assert.Fail($"Slot {slots[i]} changed in permutation {permutations}.");
            permutations++;
        } while (NextPermutation(order));
        permutations.ShouldBe(40320);
    }

    private static bool NextPermutation(int[] values)
    {
        int pivot = values.Length - 2;
        while (pivot >= 0 && values[pivot] >= values[pivot + 1])
            pivot--;
        if (pivot < 0)
            return false;
        int successor = values.Length - 1;
        while (values[successor] <= values[pivot])
            successor--;
        (values[pivot], values[successor]) = (values[successor], values[pivot]);
        Array.Reverse(values, pivot + 1, values.Length - pivot - 1);
        return true;
    }

    private static VRTrackerTransform Tracker(string identity)
        => new() { SyntheticPoseEnabled = true, SyntheticIdentity = identity };
}
