using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class FrustumConeContainmentTests
{
    private static Frustum CreateTestFrustum()
        => new(width: 10.0f, height: 10.0f, nearPlane: 1.0f, farPlane: 20.0f);

    [Test]
    public void ContainsCone_FullyInside_ReturnsContains()
    {
        Frustum frustum = CreateTestFrustum();
        Cone cone = new(center: new Vector3(0.0f, 0.0f, -5.0f), up: Vector3.UnitY, height: 2.0f, radius: 1.0f);

        EContainment result = frustum.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsCone_CrossingSidePlane_ReturnsIntersects()
    {
        Frustum frustum = CreateTestFrustum();
        Cone cone = new(center: new Vector3(4.7f, 0.0f, -5.0f), up: Vector3.UnitY, height: 1.0f, radius: 1.0f);

        EContainment result = frustum.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainsCone_InFrontOfNearPlane_ReturnsDisjoint()
    {
        Frustum frustum = CreateTestFrustum();
        Cone cone = new(center: new Vector3(0.0f, 0.0f, 1.0f), up: Vector3.UnitY, height: 1.0f, radius: 0.5f);

        EContainment result = frustum.ContainsCone(cone);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }
}