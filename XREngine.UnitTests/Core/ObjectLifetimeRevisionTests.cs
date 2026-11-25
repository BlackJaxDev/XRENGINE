using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class ObjectLifetimeRevisionTests
{
    private static readonly FieldInfo RevisionField = typeof(XRObjectBase)
        .GetField("_lifetimeRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Test]
    public void RestoringAnOldIdDoesNotRestoreItsConstructionLifetime()
    {
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(_ => new OwnedConstructionProbe());
        OwnedConstructionProbe value = result.Value!;
        ObjectConstructionIdentity identity = result.Ownership.Identities.Single();
        try
        {
            Assert.That(result.Ownership.HasSealedIdentities, Is.True);
            Assert.That(identity.IsCurrent, Is.True);
            value.Destroy(true);
            value.Generate();
            value.AdoptPersistentID(identity.Id);
            Assert.That(value.ID, Is.EqualTo(identity.Id));
            Assert.That(value.IsDestroyed, Is.False);
            Assert.That(ReadRevision(value), Is.GreaterThan(identity.LifetimeRevision));
            Assert.That(identity.IsCurrent, Is.False);
            Assert.That(result.Ownership.Identities.Single(), Is.EqualTo(identity));
        }
        finally { value.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ConstructionGuardsRejectForeignDestructionUntilIdentitiesAreSealed(bool duringPublication)
    {
        Task<Exception?>? attempt = null;
        bool completed = false;
        void Attempt(OwnedConstructionProbe value)
        {
            attempt = Task.Run(() =>
            {
                try { value.Destroy(true); return null; }
                catch (Exception failure) { return failure; }
            });
            completed = attempt.Wait(TimeSpan.FromSeconds(3));
        }
        ObjectConstructionResult<OwnedConstructionProbe> result = XRObjectBase.ConstructOwned(_ =>
        {
            OwnedConstructionProbe value = new();
            if (duringPublication) value.Publishing = () => Attempt(value);
            else Attempt(value);
            return value;
        });
        try
        {
            Assert.That(completed, Is.True, "A foreign lifetime call must reject before waiting on the cache lock.");
            Assert.That(attempt!.Result, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(result.Value!.DestroyCount, Is.Zero);
            Assert.That(result.Ownership.Identities.Single().IsCurrent, Is.True);
        }
        finally
        {
            if (attempt is not null)
                Assert.That(attempt.Wait(TimeSpan.FromSeconds(3)), Is.True);
            result.Value!.Publishing = null;
            result.Value.Destroy(true);
        }
    }

    [Test]
    public void ExhaustedRevisionRejectsBeforeTheIdentityFieldAndNotification()
    {
        OwnedConstructionProbe value = new();
        long originalRevision = ReadRevision(value);
        Guid originalId = value.ID;
        bool notified = false;
        XRPropertyChangingEventHandler changing = (_, change) => notified |= change.PropertyName == nameof(XRObjectBase.ID);
        value.PropertyChanging += changing;
        try
        {
            RevisionField.SetValue(value, long.MaxValue);
            Assert.Throws<InvalidOperationException>(() => value.AdoptPersistentID(Guid.NewGuid()));
            Assert.That(ReadRevision(value), Is.EqualTo(long.MaxValue));
            Assert.That(value.ID, Is.EqualTo(originalId));
            Assert.That(notified, Is.False);
        }
        finally
        {
            value.PropertyChanging -= changing;
            RevisionField.SetValue(value, originalRevision);
            value.Destroy(true);
        }
    }

    private static long ReadRevision(XRObjectBase value) => (long)RevisionField.GetValue(value)!;
}
