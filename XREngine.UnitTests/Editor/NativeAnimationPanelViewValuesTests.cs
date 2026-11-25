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
            null, null, null, null, null, null, 0, 0, false, 1, 0, true, -1, new(null, null, string.Empty, 0, null));
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

    [Test]
    public void CandidateQueryRejectsMissingTextAndInvalidRootlessPosition()
    {
        NativeAnimationCandidateView empty = new(null, null, "filter", 0, null);
        Assert.That(empty.IsCurrent, Is.True);
        Assert.That((empty with { Query = null! }).IsCurrent, Is.False);
        Assert.That((empty with { Page = -1 }).IsCurrent, Is.False);
        Assert.That((empty with { Page = 1 }).IsCurrent, Is.False);
        Assert.That((empty with { SelectedId = "foreign" }).IsCurrent, Is.False);
    }

    [TestCase("identity")]
    [TestCase("queued")]
    [TestCase("destroyed")]
    public void CandidateRootLifetimeInvalidatesItsCapturedPosition(string change)
    {
        XREngine.Scene.SceneNode root = new("Candidate root");
        try
        {
            NativeAnimationCandidateView view = new(root,
                ObjectConstructionIdentity.Capture(root), "filter", 3, "path");
            Assert.That(view.IsCurrent, Is.True);
            if (change == "identity") root.Generate();
            else root.Destroy(change == "destroyed");
            Assert.That(view.IsCurrent, Is.False);
        }
        finally { root.Destroy(true); }
    }

    [Test]
    public void CandidateRootCannotUseAnotherRootsIdentity()
    {
        XREngine.Scene.SceneNode first = new("First"), second = new("Second");
        try
        {
            NativeAnimationCandidateView view = new(first,
                ObjectConstructionIdentity.Capture(second), string.Empty, 0, null);
            Assert.That(view.IsCurrent, Is.False);
        }
        finally { first.Destroy(true); second.Destroy(true); }
    }

    private static NativeAnimationPanelViewValues Values(AnimationClip clip,
        AnimationMember? member, PropAnimFloat? animation)
        => new(clip, ObjectConstructionIdentity.Capture(clip), clip.LengthInSeconds,
            clip.SampleRate, clip.RootMember, member, animation,
            animation is null ? null : ObjectConstructionIdentity.Capture(animation),
            null, animation?.Keyframes, 0, 0, true, 2, 0.5f, false, -1, new(null, null, string.Empty, 0, null));
}
