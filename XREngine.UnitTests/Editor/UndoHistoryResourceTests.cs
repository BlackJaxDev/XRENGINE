using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Editor;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class UndoHistoryResourceTests
{
    [SetUp]
    public void SetUp() => Undo.ClearHistory();

    [TearDown]
    public void TearDown() => Undo.ClearHistory();

    [Test]
    public void HistoryMovesKeepOneResourceUntilItsActionIsRemoved()
    {
        int releases = 0;
        object sync = typeof(Undo).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        UndoHistoryResource resource = new(() =>
        {
            Assert.That(Monitor.IsEntered(sync), Is.False);
            Assert.That(Undo.TryUndo(), Is.False);
            Assert.That(Undo.TryRedo(), Is.False);
            releases++;
        });
        Record(resource);
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(Undo.TryRedo(), Is.True);
        Assert.That(releases, Is.Zero);
        Undo.ClearHistory();
        Undo.ClearHistory();
        Assert.That(releases, Is.EqualTo(1));
        Assert.That(resource.IsReleased, Is.True);
    }

    [TestCase("Structural")]
    [TestCase("Property")]
    [TestCase("Scope")]
    public void EveryNewActionPathReleasesResourcesFromTheRedoStack(string newAction)
    {
        int releases = 0;
        Record(new(() => releases++));
        Assert.That(Undo.TryUndo(), Is.True);
        using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
        SceneNode node = new("Before");
        try
        {
            using (Undo.BeginUserInteraction())
            {
                if (newAction == "Structural")
                    Undo.RecordStructuralChange("New action", () => { }, () => { });
                else if (newAction == "Property")
                {
                    Undo.Track(node);
                    node.Name = "After";
                }
                else
                {
                    using var scope = Undo.BeginChange("New scoped action");
                    Undo.RecordStructuralChange("New action", () => { }, () => { });
                }
            }
            Assert.That(releases, Is.EqualTo(1));
            Assert.That(Undo.CanRedo, Is.False);
        }
        finally { Undo.Untrack(node); node.Destroy(true); node.Transform.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CanceledOrEmptyScopeReleasesAfterItsStructuralHold(bool cancel)
    {
        int releases = 0;
        using var interaction = Undo.BeginUserInteraction();
        Undo.ChangeScope scope = Undo.BeginChange("Canceled construction");
        StructuralChangeHandle handle = Undo.BeginStructuralMutation("Create", () => { }, () => { });
        handle.RetainResource(new(() => releases++));
        if (cancel)
            scope.Cancel();
        else
            handle.RemoveRestoredStep();
        scope.Dispose();
        Assert.That(releases, Is.Zero);
        handle.Dispose();
        Assert.That(releases, Is.EqualTo(1));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void OutOfOrderScopesReleaseBothExactOwners()
    {
        int releases = 0;
        using var interaction = Undo.BeginUserInteraction();
        Undo.ChangeScope outer = Undo.BeginChange("Outer");
        StructuralChangeHandle first = Undo.BeginStructuralMutation("Outer step", () => { }, () => { });
        first.RetainResource(new(() => releases++));
        Undo.ChangeScope inner = Undo.BeginChange("Inner");
        StructuralChangeHandle second = Undo.BeginStructuralMutation("Inner step", () => { }, () => { });
        second.RetainResource(new(() => releases++));
        outer.Dispose();
        inner.Dispose();
        second.Dispose();
        Assert.That(releases, Is.Zero);
        first.Dispose();
        Assert.That(releases, Is.EqualTo(2));
        Assert.That(Undo.CanUndo, Is.False);
    }

    [Test]
    public void FailedStepCloneDoesNotDropItsResource()
    {
        int releases = 0;
        using var interaction = Undo.BeginUserInteraction();
        Undo.ChangeScope scope = Undo.BeginChange("Fail history copy");
        StructuralChangeHandle handle = Undo.BeginStructuralMutation("Create", () => { }, () => { });
        handle.RetainResource(new(() => releases++));
        scope.AddStructuralStep(new ThrowingUndoCloneStep());
        Assert.Throws<InvalidOperationException>(scope.Dispose);
        Assert.That(releases, Is.Zero);
        handle.Dispose();
        Assert.That(releases, Is.EqualTo(1));
    }

    [Test]
    public void ClearFromACommitObserverWaitsUntilTheCommandHasFinished()
    {
        int releases = 0;
        bool callback = false;
        void Changed()
        {
            if (callback)
                return;
            callback = true;
            Undo.ClearHistory();
            Assert.That(releases, Is.Zero);
            throw new InvalidOperationException("Expected history observer failure.");
        }
        Undo.HistoryChanged += Changed;
        try
        {
            using var interaction = Undo.BeginUserInteraction();
            Undo.ChangeScope scope = Undo.BeginChange("Create");
            StructuralChangeHandle handle = Undo.BeginStructuralMutation("Create", () => { }, () => { });
            handle.RetainResource(new(() => releases++));
            Assert.Throws<InvalidOperationException>(scope.Dispose);
            Assert.That(releases, Is.Zero);
            handle.Dispose();
            Assert.That(releases, Is.EqualTo(1));
        }
        finally { Undo.HistoryChanged -= Changed; }
    }

    [Test]
    public void ReleaseCallbackCanPruneNewWorkWithoutReleasingTheCurrentTokenTwice()
    {
        int firstReleases = 0;
        int secondReleases = 0;
        UndoHistoryResource? first = null;
        first = new(() =>
        {
            firstReleases++;
            using var interaction = Undo.BeginUserInteraction();
            using (var scope = Undo.BeginChange("Nested release"))
            using (StructuralChangeHandle handle = Undo.BeginStructuralMutation("Nested", () => { }, () => { }))
            {
                Assert.Throws<InvalidOperationException>(() => handle.RetainResource(first!));
                handle.RetainResource(new(() => secondReleases++));
            }
            Undo.ClearHistory();
        });
        Record(first);
        Undo.ClearHistory();
        Assert.That(firstReleases, Is.EqualTo(1));
        Assert.That(secondReleases, Is.EqualTo(1));
    }

    [Test]
    public void FailedReleaseRemainsOwnedAndCanBeRetriedExactlyOnce()
    {
        int attempts = 0;
        bool fail = true;
        UndoHistoryResource resource = new(() =>
        {
            attempts++;
            if (fail)
                throw new InvalidOperationException("Expected resource failure.");
        });
        Record(resource);
        Undo.ClearHistory();
        Assert.That(resource.ReleaseFailure, Is.TypeOf<InvalidOperationException>());
        Assert.That(resource.IsReleased, Is.False);
        Undo.ClearHistory();
        Assert.That(attempts, Is.EqualTo(1));
        fail = false;
        Assert.That(Undo.RetryHistoryResourceRelease(resource), Is.True);
        Assert.That(resource.IsReleased, Is.True);
        Assert.That(resource.ReleaseFailure, Is.Null);
        Assert.That(Undo.RetryHistoryResourceRelease(resource), Is.False);
        Assert.That(attempts, Is.EqualTo(2));
    }

    [Test]
    public void AStructuralHandleOpenedByReleaseDefersTheNextResource()
    {
        Undo.ChangeScope? pendingScope = null;
        StructuralChangeHandle? pendingHandle = null;
        int nextReleases = 0;
        Record(new(() =>
        {
            using var interaction = Undo.BeginUserInteraction();
            pendingScope = Undo.BeginChange("Callback mutation");
            pendingHandle = Undo.BeginStructuralMutation("Callback step", () => { }, () => { });
        }));
        Record(new(() => nextReleases++));
        try
        {
            Undo.ClearHistory();
            Assert.That(pendingHandle, Is.Not.Null);
            Assert.That(nextReleases, Is.Zero);
            pendingScope!.Cancel();
            pendingScope.Dispose();
            Assert.That(nextReleases, Is.Zero);
            pendingHandle!.Dispose();
            Assert.That(nextReleases, Is.EqualTo(1));
        }
        finally
        {
            pendingScope?.Cancel();
            pendingScope?.Dispose();
            pendingHandle?.Dispose();
        }
    }

    [Test]
    public void RestoredResourceReleaseDoesNotRemoveAnUnrelatedCallbackAction()
    {
        int released = 0;
        int callbackValue = 1;
        UndoHistoryResource resource = new(() => released++);
        using (Undo.BeginUserInteraction())
        using (Undo.ChangeScope scope = Undo.BeginChange("Restored create"))
        using (StructuralChangeHandle handle = Undo.BeginStructuralMutation("Create", () => { }, () => { }))
        {
            handle.RetainResource(resource);
            Assert.Throws<InvalidOperationException>(() => handle.ReleaseRestoredResource(resource));
            Undo.RecordStructuralChange("Callback", () => callbackValue = 0, () => callbackValue = 1);
            handle.RemoveRestoredStep();
            handle.ReleaseRestoredResource(resource);
            Assert.That(released, Is.Zero);
        }
        Assert.That(released, Is.EqualTo(1));
        Assert.That(Undo.TryUndo(), Is.True);
        Assert.That(callbackValue, Is.Zero);
    }

    private static void Record(UndoHistoryResource resource)
    {
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Owned construction");
        using StructuralChangeHandle handle = Undo.BeginStructuralMutation("Create", () => { }, () => { });
        handle.RetainResource(resource);
        handle.RetainResource(resource);
    }
}
