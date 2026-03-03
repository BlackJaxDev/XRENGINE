using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class SphereCapsuleContainmentTests
{
    [Test]
    public void ContainsCapsule_WhenCapsuleFullyInside_ReturnsContains()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 5.0f);
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 0.5f, halfHeight: 1.0f);

        EContainment result = sphere.ContainsCapsule(capsule);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsCapsule_WhenCapsuleClearlyOutside_ReturnsDisjoint()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 1.0f);
        Capsule capsule = new(center: new Vector3(5.0f, 0.0f, 0.0f), upAxis: Vector3.UnitY, radius: 0.5f, halfHeight: 1.0f);

        EContainment result = sphere.ContainsCapsule(capsule);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsCapsule_WhenCapsulePartiallyOverlapping_ReturnsIntersects()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 1.0f);
        Capsule capsule = new(center: new Vector3(1.2f, 0.0f, 0.0f), upAxis: Vector3.UnitY, radius: 0.5f, halfHeight: 0.5f);

        EContainment result = sphere.ContainsCapsule(capsule);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }
}