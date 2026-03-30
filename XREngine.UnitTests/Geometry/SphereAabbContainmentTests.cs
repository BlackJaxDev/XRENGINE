using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class SphereAabbContainmentTests
{
    [Test]
    public void ContainsAABB_WhenPointLightInfluenceSphereEnclosesSceneBounds_ReturnsContains()
    {
        Sphere sphere = new(center: new Vector3(0.0f, 2.0f, 0.0f), radius: 10000.0f);
        AABB box = new(
            new Vector3(-500.0f, -10.0f, -500.0f),
            new Vector3(500.0f, 200.0f, 500.0f));

        EContainment result = sphere.ContainsAABB(box);

        Assert.That(result, Is.EqualTo(EContainment.Contains));
    }

    [Test]
    public void ContainsAABB_WhenBoxClearlyOutside_ReturnsDisjoint()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 1.0f);
        AABB box = new(
            new Vector3(5.0f, 5.0f, 5.0f),
            new Vector3(6.0f, 6.0f, 6.0f));

        EContainment result = sphere.ContainsAABB(box);

        Assert.That(result, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsAABB_WhenBoxPartiallyOverlaps_ReturnsIntersects()
    {
        Sphere sphere = new(center: Vector3.Zero, radius: 1.0f);
        AABB box = new(
            new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(1.5f, 0.5f, 0.5f));

        EContainment result = sphere.ContainsAABB(box);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }
}
