using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Components.VR;

namespace XREngine.UnitTests.VR;

[TestFixture]
public sealed class VrTrackerBinderTests
{
    [Test]
    public void EightTrackers_AllEnumerationOrdersHaveTheSameOneToOneAssignment()
    {
        var slots = Enumerable.Range(0, 8).Select(i => new VrTrackerBindingSlot((EHumanoidIKTarget)i, new(i, 0, 0), new(i, 0, 0))).ToArray();
        var trackers = Enumerable.Range(0, 8).Select(i => new VrTrackerBindingCandidate("physical-" + i, new(i + 0.03f, 0, 0), true)).ToArray();
        var results = new VrTrackerBinding[8];
        int permutations = 0;
        Check(0);
        permutations.ShouldBe(40320);
        void Check(int index)
        {
            if (index == trackers.Length)
            {
                int count = VrTrackerBinder.Bind(slots, trackers, 0.25f, results);
                count.ShouldBe(8);
                for (int i = 0; i < count; i++)
                    trackers[results[i].TrackerIndex].Identity.ShouldBe("physical-" + results[i].SlotIndex);
                permutations++;
                return;
            }
            for (int i = index; i < trackers.Length; i++)
            {
                (trackers[index], trackers[i]) = (trackers[i], trackers[index]);
                Check(index + 1);
                (trackers[index], trackers[i]) = (trackers[i], trackers[index]);
            }
        }
    }

    [Test]
    public void CutoffValidityAndDuplicateIdentity_NeverAssignAnUnusableOrDistantTracker()
    {
        VrTrackerBindingSlot[] slots = [new(EHumanoidIKTarget.Hips, Vector3.Zero, Vector3.Zero), new(EHumanoidIKTarget.Chest, Vector3.UnitY, Vector3.UnitY)];
        VrTrackerBindingCandidate[] trackers = [new("same", Vector3.Zero, true), new("same", Vector3.UnitY, true),
            new("lost", Vector3.UnitY, false), new("camera", new(5, 5, 5), true)];
        Span<VrTrackerBinding> result = stackalloc VrTrackerBinding[8];
        VrTrackerBinder.Bind(slots, trackers, 0.25f, result).ShouldBe(1);
        result[0].SlotIndex.ShouldBe(0);
        VrTrackerBinder.Bind(slots, [], 0.25f, result).ShouldBe(0);
    }

    [Test]
    public void LimbSegmentsAndDistanceTies_AreStableAcrossEnumeration()
    {
        VrTrackerBindingSlot[] slots = [new(EHumanoidIKTarget.LeftElbow, Vector3.Zero, Vector3.UnitX)];
        VrTrackerBindingCandidate[] trackers = [new("b", new(0.5f, 0.1f, 0), true), new("a", new(0.5f, -0.1f, 0), true)];
        Span<VrTrackerBinding> result = stackalloc VrTrackerBinding[1];
        VrTrackerBinder.Bind(slots, trackers, 0.2f, result).ShouldBe(1);
        trackers[result[0].TrackerIndex].Identity.ShouldBe("a");
        Array.Reverse(trackers);
        VrTrackerBinder.Bind(slots, trackers, 0.2f, result).ShouldBe(1);
        trackers[result[0].TrackerIndex].Identity.ShouldBe("a");
    }
}
