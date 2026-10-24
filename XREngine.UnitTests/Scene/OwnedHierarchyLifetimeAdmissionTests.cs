using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class OwnedHierarchyLifetimeAdmissionTests
{
    private static readonly Type AdmissionType = typeof(SceneNode).Assembly
        .GetType("XREngine.Scene.Prefabs.HierarchyLifetimeAdmission", throwOnError: true)!;
    private static readonly MethodInfo AcquireAdmission = AdmissionType
        .GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RequireAdmissionCurrent = AdmissionType
        .GetMethod("RequireCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo AcquireReservation = typeof(SceneNode).Assembly
        .GetType("XREngine.Scene.Prefabs.HierarchyStructuralReservation", throwOnError: true)!
        .GetMethod("TryAcquire", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Test]
    public void ExactAdmissionBlocksOwnedLifetimesAndListsButAllowsOrdinaryEdits()
    {
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ => new SceneNode("Owned"));
        SceneNode node = result.Value!;
        SceneNode unrelated = new("Unrelated");
        Guid unrelatedId = unrelated.ID;
        try
        {
            using (IDisposable admission = AcquireLifetime(result.Ownership))
            {
                Assert.Throws<InvalidOperationException>(node.Generate);
                Assert.Throws<InvalidOperationException>(() => node.Destroy(true));
                Assert.Throws<InvalidOperationException>(() => node.Transform.Children.Destroy(false));
                Assert.Throws<InvalidOperationException>(((XRObjectBase)node.Components).Generate);
                Assert.That(node.IsDestroyQueued, Is.False);
                Assert.That(node.Transform.Children.IsDestroyQueued, Is.False);
                node.Name = "Edited";
                ((Transform)node.Transform).Translation = Vector3.One;
                node.Parent = unrelated;
                node.Parent = null;
                unrelated.Generate();
                RequireAdmissionCurrent.Invoke(admission, BindingFlags.DoNotWrapExceptions, null, null, null);
                Assert.That(node.Name, Is.EqualTo("Edited"));
                Assert.That(((Transform)node.Transform).Translation, Is.EqualTo(Vector3.One));
                Assert.That(unrelated.ID, Is.Not.EqualTo(unrelatedId));
            }
            OwnedHierarchyLifetime lifetime = new(result.Ownership);
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
        }
        finally { node.Destroy(true); unrelated.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InFlightObjectOrInstalledListIdentityPreventsAdmission(bool listIdentity)
    {
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ => new SceneNode("Owned"));
        SceneNode node = result.Value!;
        XRObjectBase target = listIdentity ? node.Transform.Children : node.Transform;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        XRPropertyChangingEventHandler changing = (_, change) =>
        {
            if (change.PropertyName != nameof(XRObjectBase.ID)) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test did not release the lifetime callback.");
        };
        target.PropertyChanging += changing;
        Task writer = Task.Run(() => target.AdoptPersistentID(Guid.NewGuid()));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.Throws<InvalidOperationException>(() => AcquireLifetime(result.Ownership));
            using IDisposable? reservation = TryReserve(node, node.Transform);
            Assert.That(reservation, Is.Null);
            release.Set();
            Assert.That(writer.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.Throws<InvalidOperationException>(() => AcquireLifetime(result.Ownership));
            OwnedHierarchyLifetime lifetime = new(result.Ownership);
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(node.IsDestroyed, Is.False);
        }
        finally
        {
            release.Set();
            Assert.That(writer.Wait(TimeSpan.FromSeconds(5)), Is.True);
            target.PropertyChanging -= changing;
            node.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void RetirementRejectsSameIdAfterResurrectionBeforeDestructionCallbacks()
    {
        ObjectConstructionResult<Transform> result = XRObjectBase.ConstructOwned(_ => new Transform());
        Transform transform = result.Value!;
        Guid original = transform.ID;
        transform.Destroy(true);
        transform.Generate();
        transform.AdoptPersistentID(original);
        int attempts = 0;
        Func<XRObjectBase, bool> destroying = _ => { attempts++; return true; };
        transform.Destroying += destroying;
        try
        {
            OwnedHierarchyLifetime lifetime = new(result.Ownership);
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(lifetime.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
            Assert.That(transform.ID, Is.EqualTo(original));
            Assert.That(transform.IsDestroyed, Is.False);
            Assert.That(attempts, Is.Zero);
        }
        finally
        {
            transform.Destroying -= destroying;
            transform.Children.Generate();
            transform.Destroy(true);
        }
    }

    [Test]
    public void PermittedRetirementAdvancesOnlyItsStructuralLifetimeProgress()
    {
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ => new SceneNode("Owned"));
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            foreach (ObjectConstructionIdentity identity in result.Ownership.Identities)
                if (identity.Target is XRWorldObjectBase)
                {
                    Assert.That(identity.Target.IsDestroyed, Is.True);
                    Assert.That(identity.Target.ID, Is.EqualTo(identity.Id));
                    Assert.That(identity.IsCurrent, Is.False);
                }
                else
                    Assert.That(identity.IsCurrent, Is.True);
            Assert.That(lifetime.TryRetire(), Is.True);
        }
        finally { result.Value!.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }

    private static IDisposable AcquireLifetime(ObjectConstructionOwnership ownership)
        => (IDisposable)AcquireAdmission.Invoke(null, BindingFlags.DoNotWrapExceptions, null, [ownership], null)!;

    private static IDisposable? TryReserve(params XRWorldObjectBase[] values)
        => (IDisposable?)AcquireReservation.Invoke(null, BindingFlags.DoNotWrapExceptions, null, [values], null);
}
