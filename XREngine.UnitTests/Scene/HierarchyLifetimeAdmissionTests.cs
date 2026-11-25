using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class HierarchyLifetimeAdmissionTests
{
    private static readonly MethodInfo AcquireReservation = typeof(SceneNode).Assembly
        .GetType("XREngine.Scene.Prefabs.HierarchyStructuralReservation", throwOnError: true)!
        .GetMethod("TryAcquire", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Test]
    public void ReservedObjectsRejectIdentityPublicationAndDestructionBeforeEffects()
    {
        HierarchyLifetimeProbeTransform transform = new();
        Guid original = transform.ID;
        long pending = XRObjectBase.PendingDestructionCount;
        bool notified = false;
        Func<XRObjectBase, bool> destroying = _ => { notified = true; return true; };
        transform.Destroying += destroying;
        try
        {
            using (IDisposable? reservation = TryReserve(transform))
            {
                Assert.That(reservation, Is.Not.Null);
                Assert.Throws<InvalidOperationException>(() => transform.AdoptPersistentID(Guid.NewGuid()));
                Assert.Throws<InvalidOperationException>(transform.Generate);
                Assert.Throws<InvalidOperationException>(transform.RequestPublication);
                Assert.Throws<InvalidOperationException>(() => transform.Destroy(false));
                Assert.Throws<InvalidOperationException>(() => transform.Destroy(true));
                Assert.That(transform.ID, Is.EqualTo(original));
                Assert.That(transform.IsDestroyed, Is.False);
                Assert.That(transform.IsDestroyQueued, Is.False);
                Assert.That(XRObjectBase.PendingDestructionCount, Is.EqualTo(pending));
                Assert.That(notified, Is.False);
                Assert.That(XRObjectBase.ObjectsCache[original], Is.SameAs(transform));
            }
            transform.AdoptPersistentID(Guid.NewGuid());
            Assert.That(transform.ID, Is.Not.EqualTo(original));
        }
        finally
        {
            transform.Destroying -= destroying;
            transform.Destroy(true);
        }
    }

    [Test]
    public void IdentityCallbackInFlightPreventsReservationUntilTheWholeWriteFinishes()
    {
        HierarchyLifetimeProbeTransform transform = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        XRPropertyChangingEventHandler observer = (_, change) =>
        {
            if (change.PropertyName != nameof(XRObjectBase.ID)) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test did not release the identity callback.");
        };
        transform.PropertyChanging += observer;
        Guid replacement = Guid.NewGuid();
        Task write = Task.Run(() => transform.AdoptPersistentID(replacement));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            using IDisposable? blocked = TryReserve(transform);
            Assert.That(blocked, Is.Null);
            release.Set();
            Assert.That(write.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(transform.ID, Is.EqualTo(replacement));
            using IDisposable? settled = TryReserve(transform);
            Assert.That(settled, Is.Not.Null);
        }
        finally
        {
            release.Set();
            Assert.That(write.Wait(TimeSpan.FromSeconds(5)), Is.True);
            transform.PropertyChanging -= observer;
            transform.Destroy(true);
        }
    }

    [Test]
    public void ExplicitPublicationHoldsAdmissionAcrossItsOverride()
    {
        HierarchyLifetimeProbeTransform transform = new();
        bool observed = false;
        transform.Publishing = () =>
        {
            observed = true;
            using IDisposable? reservation = TryReserve(transform);
            Assert.That(reservation, Is.Null);
        };
        try
        {
            transform.RequestPublication();
            Assert.That(observed, Is.True);
            using IDisposable? settled = TryReserve(transform);
            Assert.That(settled, Is.Not.Null);
        }
        finally
        {
            transform.Publishing = null;
            transform.Destroy(true);
        }
    }

    [Test]
    public void OwnedBatchPublicationHoldsAdmissionBeforeTheVirtualOverride()
    {
        bool observed = false;
        ObjectConstructionResult<HierarchyLifetimeProbeTransform> result = XRObjectBase.ConstructOwned(_ =>
        {
            HierarchyLifetimeProbeTransform transform = new();
            transform.Publishing = () =>
            {
                observed = true;
                using IDisposable? reservation = TryReserve(transform);
                Assert.That(reservation, Is.Null);
            };
            return transform;
        });
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(observed, Is.True);
            using IDisposable? settled = TryReserve(result.Value!);
            Assert.That(settled, Is.Not.Null);
        }
        finally
        {
            foreach (XRObjectBase value in result.Ownership.Objects)
                if (value is HierarchyLifetimeProbeTransform transform)
                {
                    transform.Publishing = null;
                    transform.Destroy(true);
                }
        }
    }

    [Test]
    public void RejectedRollbackRetainsItsEntryAndStillReleasesOtherExactEntries()
    {
        IDisposable? reservation = null;
        ObjectConstructionResult<HierarchyLifetimeProbeTransform[]> result = XRObjectBase.ConstructOwned(
            _ => new[] { new HierarchyLifetimeProbeTransform(), new HierarchyLifetimeProbeTransform() },
            values =>
            {
                reservation = TryReserve(values[1]);
                Assert.That(reservation, Is.Not.Null);
                throw new InvalidOperationException("Expected validation failure after reservation.");
            });
        try
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.TypeOf<AggregateException>());
            Assert.That(XRObjectBase.ObjectsCache.ContainsKey(result.Value![0].ID), Is.False);
            Assert.That(XRObjectBase.ObjectsCache[result.Value[1].ID], Is.SameAs(result.Value[1]));
            Assert.That(result.Ownership.Contains(result.Value[1]), Is.True);
            Assert.That(result.Value[1].IsDestroyed, Is.False);
        }
        finally
        {
            reservation?.Dispose();
            foreach (XRObjectBase value in result.Ownership.Objects)
                if (value is HierarchyLifetimeProbeTransform transform)
                    transform.Destroy(true);
        }
    }

    private static IDisposable? TryReserve(params XRWorldObjectBase[] values)
        => (IDisposable?)AcquireReservation.Invoke(null, [values]);
}
