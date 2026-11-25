using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class InspectorFloatCurveEditBatchTests
{
    [TestCase(0UL)]
    [TestCase(1UL)]
    public void PayloadAndMoveMembersApplyUndoAndRedo(ulong delta)
    {
        List<string> writes = [];
        ControlledFloatCurveEditMember a = new("a", delta, writes), b = new("b", delta, writes);
        InspectorFloatCurveEditBatch batch = new(() => true, [a, b]);
        batch.Apply(true);
        Assert.That((a.Value, b.Value), Is.EqualTo((1, 1)));
        Assert.That(batch.Matches(true), Is.True);
        batch.Apply(false);
        Assert.That((a.Value, b.Value), Is.EqualTo((0, 0)));
        Assert.That(batch.Matches(false), Is.True);
        batch.Apply(true);
        Assert.That((a.Value, b.Value), Is.EqualTo((1, 1)));
        Assert.That(writes, Is.EqualTo(new[] { "a+", "b+", "b-", "a-", "a+", "b+" }));
    }

    [Test]
    public void MixedMembersKeepNoopsAndUndoInReverseCommitOrder()
    {
        List<string> writes = [];
        ControlledFloatCurveEditMember a = new("payload", 0, writes),
            noop = new("noop", 1, writes, false), b = new("move", 1, writes);
        InspectorFloatCurveEditBatch batch = new(() => true, [a, noop, b]);
        batch.Apply(true);
        batch.Apply(false);
        Assert.That(writes, Is.EqualTo(new[] { "payload+", "move+", "move-", "payload-" }));
        Assert.That((a.Value, noop.Value, b.Value), Is.EqualTo((0, 0, 0)));
        Assert.That((noop.ExactGeneration, noop.NotificationRevision), Is.EqualTo((0UL, 0UL)));
    }

    [TestCase(0UL)]
    [TestCase(1UL)]
    public void ObserverFailureRestoresEveryOwnedCommittedMember(ulong delta)
    {
        List<string> writes = [];
        ControlledFloatCurveEditMember a = new("a", delta, writes), b = new("b", delta, writes);
        InvalidOperationException failure = new("Observer failed after commit.");
        b.AfterCommit = () => { b.AfterCommit = null; throw failure; };
        InspectorFloatCurveEditBatch batch = new(() => true, [a, b]);
        Assert.That(Assert.Throws<InvalidOperationException>(() => batch.Apply(true)), Is.SameAs(failure));
        Assert.That((a.Value, b.Value), Is.EqualTo((0, 0)));
        Assert.That(batch.Matches(false), Is.True);
        Assert.That(writes, Is.EqualTo(new[] { "a+", "b+", "b-", "a-" }));
        batch.Apply(true);
        Assert.That((a.Value, b.Value), Is.EqualTo((1, 1)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PeerAwayAndBackPreventsTheLaterMemberWrite(bool structural)
    {
        List<string> writes = [];
        ControlledFloatCurveEditMember a = new("a", 1, writes), b = new("b", 1, writes);
        a.AfterCommit = () =>
        {
            a.AfterCommit = null;
            b.ExternalWrite(7, structural);
            b.ExternalWrite(0, structural);
        };
        InspectorFloatCurveEditBatch batch = new(() => true, [a, b]);
        Assert.Throws<InvalidOperationException>(() => batch.Apply(true));
        Assert.That(b.Attempts, Is.Zero);
        Assert.That((a.Value, b.Value), Is.EqualTo((0, 0)));
        Assert.That(writes, Is.EqualTo(new[] { "a+", "a-" }));
    }

    [Test]
    public void LaterMemberBudgetRejectsBeforeAnyWrite()
    {
        List<string> writes = [];
        ControlledFloatCurveEditMember a = new("a", 0, writes), b = new("b", 1, writes)
            { BudgetAvailable = false };
        InspectorFloatCurveEditBatch batch = new(() => true, [a, b]);
        Assert.Throws<InvalidOperationException>(() => batch.Apply(true));
        Assert.That((a.Attempts, b.Attempts), Is.EqualTo((0, 0)));
        Assert.That(writes, Is.Empty);
    }

    [Test]
    public void SourceRetirementStopsLaterWritesAndCompensation()
    {
        List<string> writes = [];
        bool current = true;
        ControlledFloatCurveEditMember a = new("a", 1, writes), b = new("b", 1, writes);
        a.AfterCommit = () => current = false;
        InspectorFloatCurveEditBatch batch = new(() => current, [a, b]);
        Assert.Throws<InvalidOperationException>(() => batch.Apply(true));
        Assert.That((a.Value, b.Value), Is.EqualTo((1, 0)));
        Assert.That(b.Attempts, Is.Zero);
        Assert.That(writes, Is.EqualTo(new[] { "a+" }));
    }

    [Test]
    public void ConflictedHistoryCannotRearmAfterCallbackValuesReturn()
    {
        List<string> writes = [];
        ControlledFloatCurveEditMember a = new("a", 0, writes), b = new("b", 1, writes);
        InspectorFloatCurveEditBatch batch = new(() => true, [a, b]);
        batch.Apply(true);
        b.AfterCommit = () => { b.AfterCommit = null; a.ExternalWrite(7); };
        Assert.Throws<InvalidOperationException>(() => batch.Apply(false));
        Assert.That(a.Value, Is.EqualTo(7));
        Assert.That(a.Attempts, Is.EqualTo(1));
        a.ExternalWrite(1);
        Assert.That(batch.Matches(true), Is.False);
        Assert.That(batch.Matches(false), Is.False);
    }
}
