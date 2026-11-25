using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Prefabs;

[TestFixture, NonParallelizable]
public sealed class OwnedHierarchyConstructionSubsetTests
{
    [Test]
    public void PrivateSubsetRetiresWithOriginalReceiptsAndLeavesTheReturnedGraphLive()
    {
        Transform? spare = null;
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ =>
        {
            spare = new Transform();
            return new SceneNode("Returned");
        });
        ObjectConstructionOwnership subset = result.Ownership.CreateSubset([spare!.Children, spare]);
        OwnedHierarchyLifetime lifetime = new(subset);
        try
        {
            Assert.That(subset.HasSealedIdentities, Is.True);
            Assert.That(subset.Identities, Is.EqualTo(result.Ownership.Identities.Where(identity => subset.Contains(identity.Target))));
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            Assert.That(spare.IsDestroyed, Is.True);
            Assert.That(result.Value!.IsDestroyed, Is.False);
            Assert.That(result.Value.Transform.IsDestroyed, Is.False);
            Assert.That(result.Ownership.Identities.Single(identity => ReferenceEquals(identity.Target, spare)).IsCurrent, Is.False);
        }
        finally { spare.Destroy(true); result.Value!.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }

    [Test]
    public void SubsetDoesNotRecaptureAChangedLifetime()
    {
        ObjectConstructionResult<Transform> result = XRObjectBase.ConstructOwned(_ => new Transform());
        Transform transform = result.Value!;
        ObjectConstructionIdentity original = result.Ownership.Identities.Single(identity => ReferenceEquals(identity.Target, transform));
        transform.Generate();
        ObjectConstructionOwnership subset = result.Ownership.CreateSubset([transform, transform.Children]);
        OwnedHierarchyLifetime lifetime = new(subset);
        try
        {
            Assert.That(subset.Identities.Single(identity => ReferenceEquals(identity.Target, transform)), Is.EqualTo(original));
            Assert.That(original.IsCurrent, Is.False);
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(transform.IsDestroyed, Is.False);
        }
        finally { transform.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }

    [Test]
    public void SubsetRejectsBorrowedAndRepeatedObjects()
    {
        ObjectConstructionResult<Transform> result = XRObjectBase.ConstructOwned(_ => new Transform());
        Transform borrowed = new();
        try
        {
            Assert.Throws<InvalidOperationException>(() => result.Ownership.CreateSubset([borrowed]));
            Assert.Throws<InvalidOperationException>(() => result.Ownership.CreateSubset([result.Value!, result.Value!]));
            Assert.That(borrowed.IsDestroyed, Is.False);
        }
        finally { result.Value!.Destroy(true); borrowed.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }

    [Test]
    public void MissingPrivateStorageRejectsBeforeDestruction()
    {
        ObjectConstructionResult<Transform> result = XRObjectBase.ConstructOwned(_ => new Transform());
        int attempts = 0;
        Func<XRObjectBase, bool> observer = _ => { attempts++; return true; };
        result.Value!.Destroying += observer;
        OwnedHierarchyLifetime lifetime = new(result.Ownership.CreateSubset([result.Value]));
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(attempts, Is.Zero);
            Assert.That(result.Value.IsDestroyed, Is.False);
        }
        finally { result.Value.Destroying -= observer; result.Value.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }

    [Test]
    public void ForeignAttachmentRejectsSubsetRetirement()
    {
        ObjectConstructionResult<Transform> result = XRObjectBase.ConstructOwned(_ => new Transform());
        Transform foreign = new();
        result.Value!.Parent = foreign;
        OwnedHierarchyLifetime lifetime = new(result.Ownership.CreateSubset(result.Ownership.Objects));
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(result.Value.IsDestroyed, Is.False);
            Assert.That(result.Value.Parent, Is.SameAs(foreign));
            Assert.That(foreign.IsDestroyed, Is.False);
        }
        finally { result.Value.Parent = null; result.Value.Destroy(true); foreign.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }
}
