using System.Numerics;
using NUnit.Framework;
using XREngine.Animation;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class NativeAnimationPlotPointerTests
{
    [Test]
    public void SnapshotKeepsFrozenKeysAndRejectsChangedLaneAttachment()
    {
        PropAnimFloat animation = new(2, false, true);
        AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
        AnimationClip clip = new(member) { LengthInSeconds = 2 };
        FloatKeyframe key = new() { Second = 1 };
        animation.Keyframes.Add(key);
        AnimationPlotPointerSnapshot.Lane[] lanes =
            [new(member, animation, 6, 146)];
        AnimationPlotPointerSnapshot.KeyHit[] hits =
            [new(member, key, key.OwningTrack, 1, new Vector2(92, 50))];
        AnimationPlotPointerSnapshot snapshot = new(new object(), new object(),
            clip, 7, 3, 11, 180, 180,
            0, 2, false, lanes, hits);

        lanes[0] = default;
        hits[0] = default;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.HasCurrentLane(member), Is.True);
            Assert.That(snapshot.TryGetLane(new Vector2(92, 50), out _), Is.True);
            Assert.That(snapshot.TryHitKey(new Vector2(93, 51), out var selected) &&
                ReferenceEquals(selected.Key, key), Is.True);
            Assert.That(snapshot.TimeForX(90), Is.EqualTo(1).Within(0.001f));
            Assert.That(snapshot.TryHitKey(new Vector2(float.NaN, 50), out _), Is.False);
        });

        member.Animation = new PropAnimFloat(2, false, true);
        Assert.That(snapshot.HasCurrentLane(member), Is.False);
    }

    [Test]
    public void GraphMarkerBandIsInsideItsAcceptedLane()
    {
        PropAnimFloat animation = new(2, false, true);
        AnimationMember member = new("Value", EAnimationMemberType.Property, animation);
        AnimationClip clip = new(member) { LengthInSeconds = 2 };
        FloatKeyframe key = new() { Second = 1 };
        animation.Keyframes.Add(key);
        AnimationPlotPointerSnapshot snapshot = new(new object(), new object(),
            clip, 1, 1, 1, 180, 180, 0, 2, true,
            [new(member, animation, 6, 174)],
            [new(member, key, key.OwningTrack, 1, new Vector2(90, 166))]);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.TryGetLane(new Vector2(90, 166), out var lane) &&
                ReferenceEquals(lane, member), Is.True);
            Assert.That(snapshot.TryHitKey(new Vector2(90, 166), out var hit) &&
                ReferenceEquals(hit.Key, key), Is.True);
        });
    }

    [Test]
    public void PinnedDragAcceptsOwnPanRevisionAndRejectsResizeOrUnrelatedView()
    {
        AnimationMember member = new("Value");
        AnimationClip clip = new(member) { LengthInSeconds = 2 };
        AnimationPlotPointerSnapshot snapshot = new(new object(), new object(),
            clip, 1, 4, 1, 180, 180, 0, 2, false,
            [new(member, null, 6, 146)], []);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.CanContinueDrag(5, 5, new Vector2(180, 180)), Is.True);
            Assert.That(snapshot.CanContinueDrag(5, 5, new Vector2(181, 180)), Is.False);
            Assert.That(snapshot.CanContinueDrag(5, 6, new Vector2(180, 180)), Is.False);
        });
    }
}
