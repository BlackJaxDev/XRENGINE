using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class AabbConeContainmentTests
{
    private static AABB CreateTestAabb()
        => new(min: new Vector3(-2.0f, -2.0f, -2.0f), max: new Vector3(2.0f, 2.0f, 2.0f));

    [Test]
    public void ContainsCone_FullyInside_ReturnsContains()
    {
        AABB box = CreateTestAabb();
        Cone cone = new(center: Vector3.Zero, up: Vector3.UnitY, height: 1.0f, radius: 1.0f);

        EContainment result = box.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsCone_OutsideBounds_ReturnsDisjoint()
    {
        AABB box = CreateTestAabb();
        Cone cone = new(center: new Vector3(6.0f, 0.0f, 0.0f), up: Vector3.UnitY, height: 1.0f, radius: 0.5f);

        EContainment result = box.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsCone_PartiallyOverlapping_ReturnsIntersects()
    {
        AABB box = CreateTestAabb();
        Cone cone = new(center: new Vector3(1.7f, 0.0f, 0.0f), up: Vector3.UnitY, height: 1.0f, radius: 1.0f);

        EContainment result = box.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainsCone_OnlyAabbOverlapsWithoutGeometryContact_ReturnsDisjoint()
    {
        AABB box = new(min: new Vector3(1.7f, 1.75f, 1.7f), max: new Vector3(1.9f, 1.95f, 1.9f));
        Cone cone = new(center: Vector3.Zero, up: Vector3.UnitY, height: 2.0f, radius: 2.0f);

        EContainment result = box.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }
}