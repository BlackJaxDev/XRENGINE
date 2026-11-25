using NUnit.Framework;
using System.Collections;
using System.Numerics;
using System.Reflection;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class UndoReplayTests
{
    [SetUp]
    public void SetUp() => Undo.ClearHistory();

    [TearDown]
    public void TearDown() => Undo.ClearHistory();

    [Test]
    public void OpenStructuralMutationRejectsReplayBeforeItCanFlushTheScope()
    {
        SceneNode node = new("Before");
        try
        {
            Undo.Track(node);
            using (Undo.BeginUserInteraction())
            using (Undo.BeginChange("Structural command"))
            {
                using StructuralChangeHandle handle = Undo.BeginStructuralMutation("Membership", () => { }, () => { });
                node.Name = "Callback change";
                Assert.That(Undo.TryUndo(), Is.False);
                Assert.That(Undo.TryRedo(), Is.False);
                node.Name = "After callback";
                Assert.That(handle.RemoveRestoredStep(), Is.True);
            }
            Assert.That(Undo.PendingUndo, Has.Count.EqualTo(1));
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(node.Name, Is.EqualTo("Before"));
            Assert.That(Undo.TryRedo(), Is.True);
            Assert.That(node.Name, Is.EqualTo("After callback"));
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void RemovingARestoredInversePreservesOtherStructuralCallbackChanges()
    {
        int callbackValue = 1;
        using (Undo.BeginUserInteraction())
        using (Undo.BeginChange("Outer command"))
        {
            using StructuralChangeHandle handle = Undo.BeginStructuralMutation("Restored command", () => { }, () => { });
            Undo.RecordStructuralChange("Callback command", () => callbackValue = 0, () => callbackValue = 1);
            Assert.That(handle.RemoveRestoredStep(), Is.True);
        }
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(callbackValue, Is.Zero);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(callbackValue, Is.EqualTo(1));
    }

    [Test]
    public void CapturedPropertyReconciliationRemovesOnlyTheRestoredOwnedProperty()
    {
        SceneNode node = new("Before");
        try
        {
            Undo.TrackSceneNode(node);
            using (Undo.BeginUserInteraction())
            using (Undo.BeginChange("Observer failure"))
            {
                using StructuralChangeHandle handle = Undo.BeginStructuralMutation("Membership", () => { }, () => { });
                node.IsActiveSelf = false;
                node.Name = "Unrelated edit";
                // Simulate the exact original and final values available to a
                // receipt after an earlier property event did not reach Undo.
                handle.ReconcileProperty(node, nameof(SceneNode.IsActiveSelf), false, false);
                handle.RemoveRestoredStep();
            }
            Assert.That(Undo.TryUndo(), Is.True);
            Assert.That(node.Name, Is.EqualTo("Before"));
            Assert.That(node.IsActiveSelf, Is.False);
            Assert.That(Undo.CanUndo, Is.False);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void RejectedHierarchyAdmissionClosesItsOwnChangeScope()
    {
        bool prepared = false;
        using (Undo.BeginUserInteraction())
            Undo.RecordStructuralChange("Outer edit", () =>
            {
                Assert.Throws<InvalidOperationException>(() => HierarchyMutation.Execute("Rejected edit", () =>
                {
                    prepared = true;
                    throw new InvalidOperationException("Preparation must not run during replay.");
                }));
            }, () => { });
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(prepared, Is.False);
        ICollection scopes = (ICollection)typeof(Undo).GetField("_scopeStack", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.That(scopes.Count, Is.Zero);
        Assert.That(Undo.CanRedo, Is.True);
    }

    [Test]
    public void CapturedTransformReplayKeepsPostApplyFailureSticky()
    {
        SceneNode node = new("Node");
        Transform transform = (Transform)node.Transform;
        bool fail = true;
        try
        {
            using (Undo.BeginUserInteraction())
            using (Undo.BeginChange("Move"))
            {
                using StructuralChangeHandle handle = Undo.BeginStructuralMutation("Membership", () => { }, () => { });
                transform.Translation = Vector3.One;
                handle.ReconcileProperty(transform, nameof(Transform.Translation), Vector3.Zero, Vector3.One);
                handle.RemoveRestoredStep();
            }
            transform.RenderMatrixChanged += (_, _) =>
            {
                if (fail)
                    throw new InvalidOperationException("Expected render matrix observer failure.");
            };
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            fail = false;
            transform.Translation = Vector3.One;
            Assert.Throws<InvalidOperationException>(() => Undo.TryUndo());
            Assert.That(transform.Translation, Is.EqualTo(Vector3.One));
            Assert.That(Undo.CanUndo, Is.True);
        }
        finally { fail = false; node.Destroy(true); }
    }

    [Test]
    public void PartialStructuralFailureRetainsItsInverseAndAlwaysReleasesReplayHold()
    {
        int membership = 0;
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("Partial change");
            using StructuralChangeHandle handle = Undo.BeginStructuralMutation("Membership", () => membership = 0, () => membership = 1);
            membership = 1;
            throw new InvalidOperationException("The rollback did not complete.");
        });
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(membership, Is.Zero);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(membership, Is.EqualTo(1));
    }

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
