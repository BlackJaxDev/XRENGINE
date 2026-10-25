using NUnit.Framework;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeAnimationPanelViewValuesTests
{
    [Test]
    public void EmptyClipKeepsLocalPreferencesButCannotCarryForeignContent()
    {
        NativeAnimationPanelViewValues empty = new(null, null, 0, 0,
            null, null, null, null, null, null, 0, 0, false, 1, 0, true, -1);
        Assert.That(empty.HasValidScalars, Is.True);
        Assert.That((empty with { PlotZoom = float.NaN }).HasValidScalars, Is.False);
        Assert.That((empty with { MemberPage = -1 }).HasValidScalars, Is.False);
        Assert.That((empty with { Key = new FloatKeyframe() }).HasValidScalars, Is.False);
    }

    [Test]
    public void ClipAndAnimationIdentityRoundTripsInvalidateCapturedValues()
    {
        PropAnimFloat animation = new(2, false, true);
        AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
        AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group,
            member)) { LengthInSeconds = 2 };
        NativeAnimationPanelViewValues values = Values(clip, member, animation);
        Assert.That(values.HasValidScalars, Is.True);
        Guid clipId = clip.ID;
        clip.AdoptPersistentID(Guid.NewGuid());
        clip.AdoptPersistentID(clipId);
        Assert.That(values.HasValidScalars, Is.False);

        values = values with { ClipIdentity = ObjectConstructionIdentity.Capture(clip) };
        Guid animationId = animation.ID;
        animation.AdoptPersistentID(Guid.NewGuid());
        animation.AdoptPersistentID(animationId);
        Assert.That(values.HasValidScalars, Is.False);
    }

    [Test]
    public void RootReplacementInvalidatesValuesEvenWithoutASelectedMember()
    {
        AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group));
        NativeAnimationPanelViewValues values = Values(clip, null, null);
        Assert.That(values.HasValidScalars, Is.True);
        clip.RootMember = new AnimationMember("Other", EAnimationMemberType.Group);
        Assert.That(values.HasValidScalars, Is.False);
    }

    [Test]
    public void ScalarValidityAloneDoesNotProveSelectedTrackMembership()
    {
        PropAnimFloat animation = new(2, false, true);
        AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
        AnimationClip clip = new(new AnimationMember("Root", EAnimationMemberType.Group,
            member)) { LengthInSeconds = 2 };
        NativeAnimationPanelViewValues values = Values(clip, member, animation);
        Assert.That(values.Key, Is.Null);
        Assert.That(values.HasValidScalars, Is.True);
        animation.Keyframes = new KeyframeTrack<FloatKeyframe>();
        Assert.That(values.HasValidScalars, Is.True,
            "Scalar validity alone is not a membership proof; the panel receipt checks the track.");
    }

    private static NativeAnimationPanelViewValues Values(AnimationClip clip,
        AnimationMember? member, PropAnimFloat? animation)
        => new(clip, ObjectConstructionIdentity.Capture(clip), clip.LengthInSeconds,
            clip.SampleRate, clip.RootMember, member, animation,
            animation is null ? null : ObjectConstructionIdentity.Capture(animation),
            null, animation?.Keyframes, 0, 0, true, 2, 0.5f, false, -1);
}
