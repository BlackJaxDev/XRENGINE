using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

/// <summary>Checks identity and cache ownership during deferred object publication.</summary>
[TestFixture]
[NonParallelizable]
public sealed class ObjectCachePublicationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void PersistentCollision_PreservesIncumbentAcrossCompletionOrAbort(bool abort)
    {
        using TextFile incumbent = new();
        TextFile restored;
        using (ObjectCachePublicationScope scope = XRObjectBase.BeginDeferredObjectCachePublication())
        {
            restored = new();
            restored.AdoptPersistentID(incumbent.ID);
            if (!abort)
                scope.Complete();
        }

        Assert.That(restored.ID, Is.EqualTo(incumbent.ID));
        Assert.That(restored.IsDestroyed, Is.EqualTo(abort));
        Assert.That(XRObjectBase.ObjectsCache[incumbent.ID], Is.SameAs(incumbent));
        restored.Destroy(now: true);
        Assert.That(XRObjectBase.ObjectsCache[incumbent.ID], Is.SameAs(incumbent));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SameValueAdoption_IsPersistentUntilRegenerated(bool adopt, bool regenerate)
    {
        using TextFile incumbent = new();
        using ObjectCachePublicationScope scope = XRObjectBase.BeginDeferredObjectCachePublication();
        using TextFile pending = new();
        if (adopt)
            pending.AdoptPersistentID(pending.ID);
        if (regenerate)
            pending.Generate();
        incumbent.AdoptPersistentID(pending.ID);
        scope.Complete();

        Assert.That(pending.ID == incumbent.ID, Is.EqualTo(adopt && !regenerate));
        Assert.That(XRObjectBase.ObjectsCache[incumbent.ID], Is.SameAs(incumbent));
        if (!adopt || regenerate)
            Assert.That(XRObjectBase.ObjectsCache[pending.ID], Is.SameAs(pending));
    }

    [Test]
    public void VetoedIdentityChange_PreservesGeneratedCollisionPolicy()
    {
        using TextFile incumbent = new();
        using ObjectCachePublicationScope scope = XRObjectBase.BeginDeferredObjectCachePublication();
        using TextFile pending = new();
        Guid generated = pending.ID;
        pending.PropertyChanging += (_, args) =>
        {
            if (args is XRPropertyChangingEventArgs<Guid> change && args.PropertyName == nameof(XRObjectBase.ID))
                change.AllowChange = false;
        };
        pending.AdoptPersistentID(Guid.NewGuid());
        Assert.That(pending.ID, Is.EqualTo(generated));
        incumbent.AdoptPersistentID(generated);
        scope.Complete();

        Assert.That(pending.ID, Is.Not.EqualTo(generated));
        Assert.That(XRObjectBase.ObjectsCache[generated], Is.SameAs(incumbent));
    }
}
