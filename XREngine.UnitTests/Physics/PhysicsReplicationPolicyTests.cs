using NUnit.Framework;
using Shouldly;
using XREngine.Components.Physics;
using XREngine.Networking;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.UnitTests.Physics;

public sealed class PhysicsReplicationPolicyTests
{
    [TestCase("dynamic")]
    [TestCase("static")]
    [TestCase("controller")]
    [TestCase("joint")]
    public void ActiveClientLease_TransfersEveryPhysicsTargetToItsOwner(string targetKind)
    {
        IPhysicsReplicationTarget target = CreateTarget(targetKind);
        NetworkEntityId entityId = NetworkEntityId.FromGuid(Guid.NewGuid());
        NetworkAuthorityLease lease = CreateLease(entityId, NetworkAuthorityMode.ClientPredicted);

        PhysicsReplicationPolicy.ApplyLease(target, lease, nowUtc: 10.0d)
            .ShouldBe(PhysicsAuthorityHandoffResult.Applied);

        target.NetworkEntityId.ShouldBe(entityId);
        target.ReplicationAuthority.ShouldBe(PhysicsReplicationAuthority.ClientAuthoritative);
        target.OwnerClientId.ShouldBe("client-a");
        target.OwnerServerPlayerIndex.ShouldBe(7);
        PhysicsReplicationPolicy.CanSimulateLocally(
            target,
            new PhysicsReplicationPeer(false, "CLIENT-A", 7)).ShouldBeTrue();
        PhysicsReplicationPolicy.CanSimulateLocally(
            target,
            new PhysicsReplicationPeer(true)).ShouldBeFalse();
    }

    [Test]
    public void RevokedOrExpiredLease_RevertsAuthorityToTheServer()
    {
        DynamicRigidBodyComponent target = new()
        {
            ReplicationAuthority = PhysicsReplicationAuthority.ClientAuthoritative,
            NetworkEntityId = NetworkEntityId.FromGuid(Guid.NewGuid()),
            OwnerClientId = "client-a",
            OwnerServerPlayerIndex = 7,
        };
        NetworkAuthorityLease lease = CreateLease(target.NetworkEntityId, NetworkAuthorityMode.ClientPredicted);
        lease.RevocationReason = NetworkAuthorityRevocationReason.OperatorRevoked;

        PhysicsReplicationPolicy.ApplyLease(target, lease, nowUtc: 10.0d)
            .ShouldBe(PhysicsAuthorityHandoffResult.RevertedToServer);

        target.ReplicationAuthority.ShouldBe(PhysicsReplicationAuthority.ServerAuthoritative);
        target.OwnerClientId.ShouldBeNull();
        target.OwnerServerPlayerIndex.ShouldBe(-1);
        PhysicsReplicationPolicy.CanSimulateLocally(target, new PhysicsReplicationPeer(true)).ShouldBeTrue();
        PhysicsReplicationPolicy.CanSimulateLocally(target, new PhysicsReplicationPeer(false, "client-a", 7))
            .ShouldBeFalse();
    }

    [Test]
    public void LeaseForAnotherEntity_IsRejectedWithoutChangingMetadata()
    {
        CharacterControllerComponent target = new()
        {
            NetworkEntityId = NetworkEntityId.FromGuid(Guid.NewGuid()),
            ReplicationAuthority = PhysicsReplicationAuthority.LocalSimulation,
        };
        NetworkAuthorityLease lease = CreateLease(
            NetworkEntityId.FromGuid(Guid.NewGuid()),
            NetworkAuthorityMode.ServerDelegated);

        PhysicsReplicationPolicy.ApplyLease(target, lease, nowUtc: 10.0d)
            .ShouldBe(PhysicsAuthorityHandoffResult.EntityMismatch);
        target.ReplicationAuthority.ShouldBe(PhysicsReplicationAuthority.LocalSimulation);
        target.OwnerClientId.ShouldBeNull();
    }

    [Test]
    public void SharedDeterministicAuthority_AllowsEveryPeerToAdvanceTheSameStep()
    {
        FixedJointComponent target = new()
        {
            ReplicationAuthority = PhysicsReplicationAuthority.SharedDeterministic,
        };

        PhysicsReplicationPolicy.CanSimulateLocally(target, new PhysicsReplicationPeer(true)).ShouldBeTrue();
        PhysicsReplicationPolicy.CanSimulateLocally(target, new PhysicsReplicationPeer(false, "client-b", 2))
            .ShouldBeTrue();
    }

    private static IPhysicsReplicationTarget CreateTarget(string targetKind)
        => targetKind switch
        {
            "dynamic" => new DynamicRigidBodyComponent(),
            "static" => new StaticRigidBodyComponent(),
            "controller" => new CharacterControllerComponent(),
            "joint" => new FixedJointComponent(),
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
        };

    private static NetworkAuthorityLease CreateLease(
        NetworkEntityId entityId,
        NetworkAuthorityMode mode)
        => new()
        {
            EntityId = entityId,
            SessionId = Guid.NewGuid(),
            OwnerClientId = "client-a",
            OwnerServerPlayerIndex = 7,
            AuthorityMode = mode,
            AuthorityLeaseExpiryUtc = 20.0d,
        };
}
