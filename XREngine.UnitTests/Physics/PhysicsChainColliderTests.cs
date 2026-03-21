using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainColliderTests
{
    [Test]
    public void SphereCollider_CollideWithoutTransform_ReturnsFalse()
    {
        var collider = new PhysicsChainSphereCollider();
        var position = Vector3.Zero;

        collider.Collide(ref position, 0.5f).ShouldBeFalse();
    }

    [Test]
    public void CapsuleCollider_CollideWithoutTransform_ReturnsFalse()
    {
        var collider = new PhysicsChainCapsuleCollider();
        var position = Vector3.Zero;

        collider.Collide(ref position, 0.5f).ShouldBeFalse();
    }

    [Test]
    public void BoxCollider_CollideWithoutTransform_ReturnsFalse()
    {
        var collider = new PhysicsChainBoxCollider();
        var position = Vector3.Zero;

        collider.Collide(ref position, 0.5f).ShouldBeFalse();
    }

    [Test]
    public void PlaneCollider_CollideWithoutTransform_ReturnsFalse()
    {
        var collider = new PhysicsChainPlaneCollider();
        var position = Vector3.Zero;

        collider.Prepare();
        collider.Collide(ref position, 0.5f).ShouldBeFalse();
    }

    [Test]
    public void LegacyCollider_PrepareWithoutTransform_DoesNotThrow()
    {
        var collider = new PhysicsChainCollider();

        Should.NotThrow(collider.Prepare);
    }
}