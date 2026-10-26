using System.Reflection;
using NUnit.Framework;
using XREngine.Animation;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class KeyframeTrackNotificationRevisionTests
{
    [Test]
    public void NestedValueAwayAndBackAdvancesBeforeEachChangedObserver()
    {
        PropAnimFloat curve = new(2, false, true);
        FloatKeyframe key = new(1f, 1f, 0, EVectorInterpType.Linear);
        curve.Keyframes.Add(key);
        ulong before = curve.Keyframes.ChangedNotificationRevision;
        List<ulong> seen = [];
        bool nested = false;
        curve.Keyframes.Changed += track =>
        {
            seen.Add(track.ChangedNotificationRevision);
            if (nested) return;
            nested = true;
            key.OutValue = 9f;
            key.OutValue = 2f;
        };
        key.OutValue = 2f;
        Assert.That(key.OutValue, Is.EqualTo(2f));
        // The linked outer setter publishes InValue first. Its nested setters
        // run while synchronization is active, then the outer OutValue publishes.
        Assert.That(curve.Keyframes.ChangedNotificationRevision, Is.EqualTo(before + 4));
        Assert.That(seen, Is.EqualTo(new[]
        {
            before + 1, before + 2, before + 3, before + 4
        }));
    }

    [Test]
    public void ObserverThrowKeepsCommittedWriteAndAdvancedRevision()
    {
        PropAnimFloat curve = new(2, false, true);
        FloatKeyframe key = new(1f, 1f, 0, EVectorInterpType.Linear);
        curve.Keyframes.Add(key);
        ulong before = curve.Keyframes.ChangedNotificationRevision;
        curve.Keyframes.Changed += _ => throw new InvalidOperationException("observer failure");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => key.OutValue = 7f)!;
        Assert.That(error.Message, Is.EqualTo("observer failure"));
        Assert.That(key.OutValue, Is.EqualTo(7f));
        Assert.That(curve.Keyframes.ChangedNotificationRevision, Is.EqualTo(before + 1));
    }

    [Test]
    public void RevisionExhaustionLatchesBeforeAnotherObserverCall()
    {
        PropAnimFloat curve = new(2, false, true);
        FieldInfo field = typeof(BaseKeyframeTrack).GetField(
            "_changedNotificationRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(curve.Keyframes, ulong.MaxValue - 1);
        int observed = 0;
        curve.Keyframes.Changed += _ => observed++;
        curve.Keyframes.OnChanged();
        Assert.That(curve.Keyframes.ChangedNotificationRevision, Is.EqualTo(ulong.MaxValue));
        Assert.That(curve.Keyframes.CanAdvanceChangedNotificationRevision, Is.False);
        Assert.Throws<InvalidOperationException>(() => curve.Keyframes.OnChanged());
        Assert.That(observed, Is.EqualTo(1));
        Assert.That(curve.Keyframes.ChangedNotificationRevision, Is.EqualTo(ulong.MaxValue));
    }

    [Test]
    public void ReserveRequiresUsableRevisionAfterBothNotifications()
    {
        PropAnimFloat curve = new(2, false, true);
        FieldInfo field = typeof(BaseKeyframeTrack).GetField(
            "_changedNotificationRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(curve.Keyframes, ulong.MaxValue - 3);
        Assert.That(curve.Keyframes.CanReserveChangedNotifications(2), Is.True);
        curve.Keyframes.OnChanged();
        Assert.That(curve.Keyframes.CanReserveChangedNotifications(2), Is.False);
        Assert.That(curve.Keyframes.CanReserveChangedNotifications(1), Is.True);
        curve.Keyframes.OnChanged();
        Assert.That(curve.Keyframes.ChangedNotificationRevision,
            Is.EqualTo(ulong.MaxValue - 1));
        Assert.That(curve.Keyframes.CanAdvanceChangedNotificationRevision, Is.True);
        Assert.That(curve.Keyframes.CanReserveChangedNotifications(1), Is.False);
    }

    [Test]
    public void NestedExhaustionAttemptIsNotMistakenForOneOwnedNotification()
    {
        PropAnimFloat curve = new(2, false, true);
        FieldInfo field = typeof(BaseKeyframeTrack).GetField(
            "_changedNotificationRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(curve.Keyframes, ulong.MaxValue - 1);
        int observed = 0;
        curve.Keyframes.Changed += track =>
        {
            observed++;
            track.OnChanged();
        };
        Assert.Throws<InvalidOperationException>(() => curve.Keyframes.OnChanged());
        Assert.That(observed, Is.EqualTo(1));
        Assert.That(curve.Keyframes.CanAdvanceChangedNotificationRevision, Is.False);
    }
}
