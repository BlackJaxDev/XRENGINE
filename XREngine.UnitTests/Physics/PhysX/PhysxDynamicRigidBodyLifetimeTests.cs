using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Scene.Physics.Physx;
using XREngine.Scene.Physics;
using XREngine.UnitTests.Physics.Contracts;
using MagicPhysX;

namespace XREngine.UnitTests.Physics;

public sealed class PhysxDynamicRigidBodyLifetimeTests
{
    [Test]
    [NonParallelizable]
    public void KinematicTarget_DetachedBody_ReplaysLatestTargetOnAttachment()
        => WithDetachedBody((scene, body) =>
        {
            body.KinematicTarget = (Vector3.UnitX, Quaternion.Identity);
            body.KinematicTarget = (Vector3.UnitY, Quaternion.Identity);
            scene.AddActor(body);
            body.KinematicTarget.ShouldBe((Vector3.UnitY, Quaternion.Identity));
        });

    [Test]
    [NonParallelizable]
    public void KinematicTarget_ClearedBeforeAttachment_DoesNotReplay()
        => WithDetachedBody((scene, body) =>
        {
            body.KinematicTarget = (Vector3.UnitX, Quaternion.Identity);
            body.KinematicTarget = null;
            scene.AddActor(body);
            body.KinematicTarget.ShouldBeNull();
            body.Flags.HasFlag(PxRigidBodyFlags.Kinematic).ShouldBeFalse();
        });

    [Test]
    [NonParallelizable]
    public void KinematicTarget_NewerDynamicMode_DiscardsDisabledSimulationTarget()
        => WithDetachedBody((scene, body) =>
        {
            scene.AddActor(body);
            body.SimulationEnabled = false;
            body.KinematicTarget = (Vector3.UnitX, Quaternion.Identity);
            body.Flags &= ~PxRigidBodyFlags.Kinematic;
            body.SimulationEnabled = true;
            body.KinematicTarget.ShouldBeNull();
            body.Flags.HasFlag(PxRigidBodyFlags.Kinematic).ShouldBeFalse();
        });

    private static void WithDetachedBody(Action<PhysxScene, PhysxDynamicRigidBody> action)
    {
        using IDisposable services = RuntimePhysicsServices.Install(
            new PhysicsContractRuntimeServices(RuntimePhysicsServices.Current));
        var scene = new PhysxScene();
        scene.Initialize();
        var body = new PhysxDynamicRigidBody(Vector3.Zero, Quaternion.Identity);
        try
        {
            action(scene, body);
        }
        finally
        {
            scene.RemoveActor(body);
            body.Release();
            scene.Destroy();
        }
    }

    [Test]
    [NonParallelizable]
    public void WakeAndSleep_DetachedBody_AreSafeNoOps()
    {
        var body = new PhysxDynamicRigidBody(Vector3.Zero, Quaternion.Identity);

        try
        {
            Should.NotThrow(body.WakeUp);
            Should.NotThrow(body.PutToSleep);
        }
        finally
        {
            body.Release();
        }
    }
}
