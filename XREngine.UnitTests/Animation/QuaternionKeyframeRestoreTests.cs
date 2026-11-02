using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class QuaternionKeyframeRestoreTests
{
    private static PropAnimQuaternion Track(ERadialInterpType inType, ERadialInterpType outType)
    {
        Quaternion start = Quaternion.Identity;
        Quaternion end = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        PropAnimQuaternion animation = new(2f, false, true);
        animation.Keyframes.Add(new QuaternionKeyframe { Second = 0f, InValue = start, OutValue = start, InterpolationTypeIn = inType, InterpolationTypeOut = outType });
        animation.Keyframes.Add(new QuaternionKeyframe { Second = 2f, InValue = end, OutValue = end, InterpolationTypeIn = inType, InterpolationTypeOut = outType });
        return animation;
    }

    private static PropAnimQuaternion RoundTrip(PropAnimQuaternion animation)
    {
        SerializedPropertyAnimationModel model = PublishedAnimationPropertyCodec.CreateModel(animation, "test")!;
        return (PropAnimQuaternion)PublishedAnimationPropertyCodec.CreateRuntimeAnimation(model, "test")!;
    }

    [TestCase(ERadialInterpType.Smooth)]
    [TestCase(ERadialInterpType.Linear)]
    public void PublishedRestore_StepInWithOtherOut_TakesOutType(ERadialInterpType outType)
    {
        PropAnimQuaternion restored = RoundTrip(Track(ERadialInterpType.Step, outType));

        foreach (QuaternionKeyframe key in restored.Keyframes)
        {
            key.InterpolationTypeIn.ShouldBe(outType);
            key.InterpolationTypeOut.ShouldBe(outType);
        }
    }

    [Test]
    public void PublishedRestore_StoredSmoothKeys_HaveNoJumpAtSpanMiddle()
    {
        PropAnimQuaternion restored = RoundTrip(Track(ERadialInterpType.Step, ERadialInterpType.Smooth));
        QuaternionKeyframe first = restored.Keyframes.First!;

        Quaternion before = first.Interpolate(0.99f);
        Quaternion after = first.Interpolate(1.01f);

        Quaternion.Dot(before, after).ShouldBeGreaterThan(0.999f);
    }

    [Test]
    public void PublishedRestore_StepOut_StaysStep()
    {
        PropAnimQuaternion restored = RoundTrip(Track(ERadialInterpType.Step, ERadialInterpType.Step));

        foreach (QuaternionKeyframe key in restored.Keyframes)
            key.InterpolationTypeIn.ShouldBe(ERadialInterpType.Step);
    }

    [Test]
    public void CookedRestore_StepInWithOtherOut_TakesOutType()
    {
        AnimationClip clip = new(new AnimationMember("Rotation", EAnimationMemberType.Property, Track(ERadialInterpType.Step, ERadialInterpType.Smooth)));
        AnimationClip copy = (AnimationClip)XREngine.Core.Files.CookedBinarySerializer.Deserialize(
            typeof(AnimationClip),
            XREngine.Core.Files.CookedBinarySerializer.Serialize(clip))!;

        foreach (QuaternionKeyframe key in ((PropAnimQuaternion)copy.RootMember!.Animation!).Keyframes)
            key.InterpolationTypeIn.ShouldBe(ERadialInterpType.Smooth);
    }
}
