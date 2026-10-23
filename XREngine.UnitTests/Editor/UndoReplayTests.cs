using NUnit.Framework;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class UndoReplayTests
{
    [SetUp]
    public void SetUp() => Undo.ClearHistory();

    [TearDown]
    public void TearDown() => Undo.ClearHistory();

    [Test]
    public void ThrowingUndoRetainsItsSourceEntryForRetry()
    {
        bool throwOnce = true;
        int value = 1;
        using var interaction = Undo.BeginUserInteraction();
        Undo.RecordStructuralChange("Change value", () =>
        {
            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("Undo failed");
            }
            value = 0;
        }, () => value = 1);

        Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo(1));
            Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
            Assert.That(Undo.PendingRedo, Is.Empty);
        });

        Assert.That(Undo.TryUndo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Zero);
            Assert.That(Undo.PendingUndo, Is.Empty);
            Assert.That(Undo.PendingRedo.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void ThrowingRedoRetainsItsSourceEntryForRetry()
    {
        bool throwOnce = true;
        int value = 1;
        using var interaction = Undo.BeginUserInteraction();
        Undo.RecordStructuralChange("Change value", () => value = 0, () =>
        {
            if (throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("Redo failed");
            }
            value = 1;
        });
        Assert.That(Undo.TryUndo(), Is.True);

        Assert.Throws<InvalidOperationException>(() => Undo.TryRedo());
        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Zero);
            Assert.That(Undo.PendingUndo, Is.Empty);
            Assert.That(Undo.PendingRedo.Count, Is.EqualTo(1));
        });

        Assert.That(Undo.TryRedo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo(1));
            Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
            Assert.That(Undo.PendingRedo, Is.Empty);
        });
    }

    [Test]
    public void NestedReplayIsRejectedWhileActionRuns()
    {
        bool nestedUndo = true;
        bool nestedRedo = true;
        using var interaction = Undo.BeginUserInteraction();
        Undo.RecordStructuralChange("Change value", () =>
        {
            nestedUndo = Undo.TryUndo();
            nestedRedo = Undo.TryRedo();
        }, () => { });

        Assert.That(Undo.TryUndo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(nestedUndo, Is.False);
            Assert.That(nestedRedo, Is.False);
            Assert.That(Undo.PendingUndo, Is.Empty);
            Assert.That(Undo.PendingRedo.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void ClearDuringReplayDoesNotRestoreOldEntry()
    {
        using var interaction = Undo.BeginUserInteraction();
        Undo.RecordStructuralChange("Old action", Undo.ClearHistory, () => { });

        Assert.That(Undo.TryUndo(), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(Undo.PendingUndo, Is.Empty);
            Assert.That(Undo.PendingRedo, Is.Empty);
        });
    }

    [Test]
    public void NewHistoryFromReplayNotificationRemainsCurrent()
    {
        using var interaction = Undo.BeginUserInteraction();
        Undo.RecordStructuralChange("Old action", () => { }, () => { });
        bool added = false;
        void OnHistoryChanged()
        {
            if (added)
                return;
            added = true;
            Assert.That(Undo.TryRedo(), Is.False);
            Undo.RecordStructuralChange("New action", () => { }, () => { });
        }

        Undo.HistoryChanged += OnHistoryChanged;
        try
        {
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(Undo.PendingUndo.Count, Is.EqualTo(1));
                Assert.That(Undo.PendingUndo[0].Description, Is.EqualTo("New action"));
                Assert.That(Undo.PendingRedo, Is.Empty);
            });
        }
        finally
        {
            Undo.HistoryChanged -= OnHistoryChanged;
        }
    }
}
