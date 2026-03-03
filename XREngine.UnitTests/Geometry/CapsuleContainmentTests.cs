using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class CapsuleContainmentTests
{
    [Test]
    public void ContainsCapsule_WhenOtherFullyInside_ReturnsContains()
    {
        Capsule outer = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 2.0f, halfHeight: 4.0f);
        Capsule inner = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 0.5f, halfHeight: 1.0f);

        EContainment result = outer.ContainsCapsule(inner);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsCapsule_WhenClearlySeparated_ReturnsDisjoint()
    {
        Capsule outer = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 2.0f);
        Capsule other = new(center: new Vector3(10.0f, 0.0f, 0.0f), upAxis: Vector3.UnitY, radius: 0.5f, halfHeight: 1.0f);

        EContainment result = outer.ContainsCapsule(other);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsCapsule_WhenPartiallyOverlapping_ReturnsIntersects()
    {
        Capsule outer = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 2.0f, halfHeight: 4.0f);
        Capsule other = new(center: new Vector3(2.5f, 0.0f, 0.0f), upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 1.0f);

        EContainment result = outer.ContainsCapsule(other);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainsCone_WhenConeFullyInside_ReturnsContains()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 3.0f, halfHeight: 5.0f);
        Cone cone = new(center: Vector3.Zero, up: Vector3.UnitY, height: 2.0f, radius: 1.0f);

        EContainment result = capsule.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsCone_WhenClearlySeparated_ReturnsDisjoint()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 2.0f);
        Cone cone = new(center: new Vector3(10.0f, 0.0f, 0.0f), up: Vector3.UnitY, height: 1.0f, radius: 0.5f);

        EContainment result = capsule.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsCone_WhenPartiallyOverlapping_ReturnsIntersects()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 3.0f, halfHeight: 5.0f);
        Cone cone = new(center: new Vector3(2.8f, 0.0f, 0.0f), up: Vector3.UnitY, height: 2.0f, radius: 1.0f);

        EContainment result = capsule.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainsAabb_WhenFullyInside_ReturnsContains()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 3.0f, halfHeight: 5.0f);
        AABB box = new(min: new Vector3(-0.5f, -0.5f, -0.5f), max: new Vector3(0.5f, 0.5f, 0.5f));

        EContainment result = capsule.ContainsAABB(box);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsAabb_WhenClearlySeparated_ReturnsDisjoint()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 2.0f);
        AABB box = new(min: new Vector3(10.0f, 10.0f, 10.0f), max: new Vector3(11.0f, 11.0f, 11.0f));

        EContainment result = capsule.ContainsAABB(box);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsAabb_WhenPartiallyOverlapping_ReturnsIntersects()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 2.0f, halfHeight: 3.0f);
        AABB box = new(min: new Vector3(1.8f, -0.5f, -0.5f), max: new Vector3(2.6f, 0.5f, 0.5f));

        EContainment result = capsule.ContainsAABB(box);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainsBox_WhenFullyInside_ReturnsContains()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 3.0f, halfHeight: 5.0f);
        Box box = new(center: Vector3.Zero, size: new Vector3(1.0f, 1.0f, 1.0f));

        EContainment result = capsule.Contains(box);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsBox_WhenClearlySeparated_ReturnsDisjoint()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 2.0f);
        Box box = new(center: new Vector3(10.0f, 0.0f, 0.0f), size: new Vector3(1.0f, 1.0f, 1.0f));

        EContainment result = capsule.Contains(box);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsBox_WhenPartiallyOverlapping_ReturnsIntersects()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 2.0f, halfHeight: 3.0f);
        Box box = new(center: new Vector3(2.1f, 0.0f, 0.0f), size: new Vector3(1.2f, 1.2f, 1.2f));

        EContainment result = capsule.Contains(box);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainedWithin_WhenFullyInsideAabb_ReturnsTrue()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 2.0f);
        AABB bounds = new(min: new Vector3(-5.0f, -5.0f, -5.0f), max: new Vector3(5.0f, 5.0f, 5.0f));

        bool result = capsule.ContainedWithin(bounds);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ContainedWithin_WhenCapsuleExtendsOutsideAabb_ReturnsFalse()
    {
        Capsule capsule = new(center: new Vector3(4.5f, 0.0f, 0.0f), upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 2.0f);
        AABB bounds = new(min: new Vector3(-5.0f, -5.0f, -5.0f), max: new Vector3(5.0f, 5.0f, 5.0f));

        bool result = capsule.ContainedWithin(bounds);

        Assert.That(result, Is.False);
    }

    [Test]
    public void ContainedWithin_WhenAabbTooThinForRadius_ReturnsFalse()
    {
        Capsule capsule = new(center: Vector3.Zero, upAxis: Vector3.UnitY, radius: 1.0f, halfHeight: 0.5f);
        AABB bounds = new(min: new Vector3(-0.5f, -5.0f, -5.0f), max: new Vector3(0.5f, 5.0f, 5.0f));

        bool result = capsule.ContainedWithin(bounds);

        Assert.That(result, Is.False);
    }
}