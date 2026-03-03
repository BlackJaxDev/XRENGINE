using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class ConeGeometryTests
{
    [Test]
    public void ContainsPoint_UsesTaperedRadius()
    {
        Cone cone = new(center: Vector3.Zero, up: Vector3.UnitY, height: 2.0f, radius: 1.0f);

        bool nearTipOutside = cone.ContainsPoint(new Vector3(0.3f, 1.8f, 0.0f));
        bool nearBaseInside = cone.ContainsPoint(new Vector3(0.7f, 0.2f, 0.0f));

        Assert.That(nearTipOutside, Is.False);
        Assert.That(nearBaseInside, Is.True);
    }

    [Test]
    public void GetAabb_AlignedCone_ReturnsExpectedBounds()
    {
        Cone cone = new(center: Vector3.Zero, up: Vector3.UnitY, height: 2.0f, radius: 1.0f);

        AABB aabb = cone.GetAABB(true);

        Assert.That(aabb.Min.X, Is.EqualTo(-1.0f).Within(1e-5f));
        Assert.That(aabb.Min.Y, Is.EqualTo(0.0f).Within(1e-5f));
        Assert.That(aabb.Min.Z, Is.EqualTo(-1.0f).Within(1e-5f));
        Assert.That(aabb.Max.X, Is.EqualTo(1.0f).Within(1e-5f));
        Assert.That(aabb.Max.Y, Is.EqualTo(2.0f).Within(1e-5f));
        Assert.That(aabb.Max.Z, Is.EqualTo(1.0f).Within(1e-5f));
    }

    [Test]
    public void IntersectsSegment_ThroughBody_ReturnsTrue()
    {
        Cone cone = new(center: Vector3.Zero, up: Vector3.UnitY, height: 2.0f, radius: 1.0f);
        Segment segment = new(new Vector3(-2.0f, 0.5f, 0.0f), new Vector3(2.0f, 0.5f, 0.0f));

        bool intersects = cone.IntersectsSegment(segment, out Vector3[] points);

        Assert.That(intersects, Is.True);
        Assert.That(points.Length, Is.GreaterThan(0));
    }

    [Test]
    public void ContainsSphere_ClassifiesInsideAndDisjoint()
    {
        Cone cone = new(center: Vector3.Zero, up: Vector3.UnitY, height: 2.0f, radius: 1.0f);
        Sphere inside = new(new Vector3(0.0f, 0.5f, 0.0f), 0.1f);
        Sphere outside = new(new Vector3(5.0f, 5.0f, 5.0f), 0.5f);

        EContainment insideResult = cone.ContainsSphere(inside);
        EContainment outsideResult = cone.ContainsSphere(outside);

        Assert.That(insideResult, Is.EqualTo(EContainment.Contains));
        Assert.That(outsideResult, Is.EqualTo(EContainment.Disjoint));
    }
}