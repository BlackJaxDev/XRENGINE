using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class SphereConeContainmentTests
{
    [Test]
    public void ContainsCone_WhenConeFullyInside_ReturnsContains()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 5.0f);
        Cone cone = new(center: new Vector3(0.0f, -1.0f, 0.0f), up: Vector3.UnitY, height: 2.0f, radius: 1.0f);

        EContainment result = sphere.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsCone_WhenConeClearlyOutside_ReturnsDisjoint()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 1.0f);
        Cone cone = new(center: new Vector3(5.0f, 0.0f, 0.0f), up: Vector3.UnitY, height: 1.0f, radius: 0.5f);

        EContainment result = sphere.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsCone_WhenConePartiallyOverlaps_ReturnsIntersects()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 1.0f);
        Cone cone = new(center: new Vector3(0.8f, 0.0f, 0.0f), up: Vector3.UnitY, height: 1.0f, radius: 0.8f);

        EContainment result = sphere.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }
}