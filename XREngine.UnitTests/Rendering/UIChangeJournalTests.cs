using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.UI.State;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class UIChangeJournalTests
{
    [Test]
    public void Record_CoalescesDomainsAndKeepsNewestParent()
    {
        var journal = new UIChangeJournal(2);
        var element = new UIElementIdentity(1, 1);
        var firstParent = new UIElementIdentity(2, 1);
        var newParent = new UIElementIdentity(3, 1);
        journal.Record(element, firstParent, UIChangeDomain.Measure);
        journal.Record(element, newParent, UIChangeDomain.Visual);

        var destination = new UIElementChange[2];
        journal.TryDrain(destination, out var batch).ShouldBeTrue();
        batch.Count.ShouldBe(1);
        batch.Revision.ShouldBe(2);
        batch.Domains.ShouldBe(UIChangeDomain.Measure | UIChangeDomain.Visual);
        destination[0].Parent.ShouldBe(newParent);
        destination[0].Domains.ShouldBe(batch.Domains);
    }

    [Test]
    public void NestedTransactions_CommitOneRevisionAndCannotDrainUntilClosed()
    {
        var journal = new UIChangeJournal(2);
        var element = new UIElementIdentity(1, 1);
        var destination = new UIElementChange[2];
        using (journal.BeginTransaction())
        {
            journal.Record(element, default, UIChangeDomain.Measure);
            using (journal.BeginTransaction())
                journal.Record(element, default, UIChangeDomain.Arrange);
            journal.Revision.ShouldBe(0);
            journal.TryDrain(destination, out var pending).ShouldBeFalse();
            pending.Count.ShouldBe(1);
            destination[0].ShouldBe(default);
        }
        journal.Revision.ShouldBe(1);
        journal.TryDrain(destination, out var committed).ShouldBeTrue();
        committed.Count.ShouldBe(1);
        destination[0].Domains.ShouldBe(UIChangeDomain.Measure | UIChangeDomain.Arrange);
    }

    [Test]
    public void ShortDestination_DoesNotConsumePendingChanges()
    {
        var journal = new UIChangeJournal(2);
        journal.Record(new UIElementIdentity(1, 1), default, UIChangeDomain.Measure);
        journal.Record(new UIElementIdentity(2, 1), default, UIChangeDomain.Visual);
        journal.TryDrain(new UIElementChange[1], out var pending).ShouldBeFalse();
        pending.Count.ShouldBe(2);
        journal.PendingCount.ShouldBe(2);

        var destination = new UIElementChange[2];
        journal.TryDrain(destination, out var drained).ShouldBeTrue();
        drained.Count.ShouldBe(2);
        journal.PendingCount.ShouldBe(0);
    }

    [Test]
    public void Overflow_RequestsFullResyncUntilSuccessfulDrain()
    {
        var journal = new UIChangeJournal(1);
        journal.Record(new UIElementIdentity(1, 1), default, UIChangeDomain.Measure);
        journal.Record(new UIElementIdentity(2, 1), default, UIChangeDomain.Visual);
        journal.PendingCount.ShouldBe(1);
        journal.RequiresFullResync.ShouldBeTrue();
        journal.TryDrain(Span<UIElementChange>.Empty, out var pending).ShouldBeFalse();
        pending.RequiresFullResync.ShouldBeTrue();
        pending.Domains.ShouldBe(UIChangeDomain.Measure | UIChangeDomain.Visual);
        journal.TryDrain(new UIElementChange[1], out var drained).ShouldBeTrue();
        drained.RequiresFullResync.ShouldBeTrue();
        journal.RequiresFullResync.ShouldBeFalse();
    }

    [Test]
    public void Removal_WinsOverLaterUpdatesForSameIncarnation()
    {
        var journal = new UIChangeJournal(1);
        var element = new UIElementIdentity(1, 1);
        journal.RecordRemoval(element);
        journal.Record(element, new UIElementIdentity(2, 1), UIChangeDomain.Visual);
        var destination = new UIElementChange[1];
        journal.TryDrain(destination, out _).ShouldBeTrue();
        destination[0].IsRemoved.ShouldBeTrue();
        destination[0].Domains.ShouldBe(UIChangeDomain.All);
    }

    [Test]
    public void Scope_DisposeTwiceCommitsOnce()
    {
        var journal = new UIChangeJournal(1);
        var scope = journal.BeginTransaction();
        journal.Record(new UIElementIdentity(1, 1), default, UIChangeDomain.Visual);
        scope.Dispose();
        scope.Dispose();
        journal.Revision.ShouldBe(1);
        journal.Record(new UIElementIdentity(1, 1), default, UIChangeDomain.Visual);
        journal.Revision.ShouldBe(2);
    }

    [Test]
    public void WrongThreadCannotMutateOrDisposeOwnerScope()
    {
        var journal = new UIChangeJournal(1);
        var scope = journal.BeginTransaction();
        var element = new UIElementIdentity(1, 1);
        var failures = new Exception?[3];
        var worker = new Thread(() =>
        {
            failures[0] = Capture(() => journal.VerifyMutationAccess());
            failures[1] = Capture(() => journal.Record(element, default, UIChangeDomain.Visual));
            failures[2] = Capture(scope.Dispose);
        });
        worker.Start();
        worker.Join();
        foreach (var failure in failures)
            failure.ShouldBeOfType<InvalidOperationException>();
        journal.Record(element, default, UIChangeDomain.Measure);
        scope.Dispose();
        journal.Revision.ShouldBe(1);
        journal.TryDrain(new UIElementChange[1], out _).ShouldBeTrue();
    }

    [Test]
    public void WarmedRecordAndDrain_AllocateNoManagedMemory()
    {
        var journal = new UIChangeJournal(1);
        var element = new UIElementIdentity(1, 1);
        var destination = new UIElementChange[1];
        for (int i = 0; i < 100; i++)
        {
            journal.Record(element, default, UIChangeDomain.Visual);
            journal.TryDrain(destination, out _);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            journal.Record(element, default, UIChangeDomain.Visual);
            journal.TryDrain(destination, out _);
        }
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
    }

    [Test]
    public void DisabledCounters_KeepCumulativeValue()
    {
        bool previous = UIRuntimeCounters.Enabled;
        try
        {
            UIRuntimeCounters.Enabled = false;
            long before = UIRuntimeCounters.Read(UIWorkCounter.MutationsRecorded);
            UIRuntimeCounters.Add(UIWorkCounter.MutationsRecorded, 3);
            UIRuntimeCounters.Read(UIWorkCounter.MutationsRecorded).ShouldBe(before);
        }
        finally { UIRuntimeCounters.Enabled = previous; }
    }

    [Test]
    public void EnabledCounters_IncrementAndCopyToCallerStorage()
    {
        bool previous = UIRuntimeCounters.Enabled;
        try
        {
            UIRuntimeCounters.Enabled = true;
            long before = UIRuntimeCounters.Read(UIWorkCounter.GlyphsCopied);
            UIRuntimeCounters.Add(UIWorkCounter.GlyphsCopied, 3);
            UIRuntimeCounters.Read(UIWorkCounter.GlyphsCopied).ShouldBe(before + 3);
            var destination = new long[(int)UIWorkCounter.Count];
            UIRuntimeCounters.CopyTo(destination);
            destination[(int)UIWorkCounter.GlyphsCopied].ShouldBe(before + 3);
            var shortDestination = new long[destination.Length - 1];
            Array.Fill(shortDestination, -1);
            Should.Throw<ArgumentException>(() => UIRuntimeCounters.CopyTo(shortDestination));
            foreach (long value in shortDestination)
                value.ShouldBe(-1);
        }
        finally { UIRuntimeCounters.Enabled = previous; }
    }

    [Test]
    public void WarmedCounterAddAndCopy_AllocateNoManagedMemory()
    {
        bool previous = UIRuntimeCounters.Enabled;
        var destination = new long[(int)UIWorkCounter.Count];
        try
        {
            UIRuntimeCounters.Enabled = true;
            for (int i = 0; i < 100; i++)
            {
                UIRuntimeCounters.Add(UIWorkCounter.HitQueries);
                UIRuntimeCounters.CopyTo(destination);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                UIRuntimeCounters.Add(UIWorkCounter.HitQueries);
                UIRuntimeCounters.CopyTo(destination);
            }
            (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0);
        }
        finally { UIRuntimeCounters.Enabled = previous; }
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (Exception exception) { return exception; }
    }
}
