using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Scene.Physics.Physx;

namespace XREngine.UnitTests.Physics;

public sealed class PhysxDynamicRigidBodyLifetimeTests
{
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
