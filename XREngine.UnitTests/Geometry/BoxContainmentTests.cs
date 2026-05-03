using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class BoxContainmentTests
{
    private static Box CreateBox()
        => new(center: Vector3.Zero, size: new Vector3(4.0f, 4.0f, 4.0f));

    [Test]
    public void ContainedWithin_WhenInsideAabb_ReturnsTrue()
    {
        Box box = CreateBox();
        AABB aabb = new(min: new Vector3(-3.0f, -3.0f, -3.0f), max: new Vector3(3.0f, 3.0f, 3.0f));

        bool result = box.ContainedWithin(aabb);

        Assert.That(result, Is.True);
    }

    [Test]
    public void ContainsAabb_WhenPartialOverlap_ReturnsIntersects()
    {
        Box box = CreateBox();
        AABB other = new(min: new Vector3(1.5f, -0.5f, -0.5f), max: new Vector3(2.5f, 0.5f, 0.5f));

        EContainment result = box.ContainsAABB(other);

        Assert.That(result, Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainsSphere_CoversBasicClassifications()
    {
        Box box = CreateBox();

        EContainment inside = box.ContainsSphere(new Sphere(Vector3.Zero, 0.5f));
        EContainment outside = box.ContainsSphere(new Sphere(new Vector3(10.0f, 0.0f, 0.0f), 0.5f));

        Assert.That(inside, Is.EqualTo(EContainment.Contains));
        Assert.That(outside, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsCone_CoversBasicClassifications()
    {
        Box box = CreateBox();

        EContainment inside = box.ContainsCone(new Cone(Vector3.Zero, Vector3.UnitY, 1.0f, 0.5f));
        EContainment outside = box.ContainsCone(new Cone(new Vector3(10.0f, 0.0f, 0.0f), Vector3.UnitY, 1.0f, 0.5f));

        Assert.That(inside, Is.EqualTo(EContainment.Contains));
        Assert.That(outside, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ContainsCapsule_CoversBasicClassifications()
    {
        Box box = CreateBox();

        EContainment inside = box.ContainsCapsule(new Capsule(Vector3.Zero, Vector3.UnitY, 0.5f, 0.5f));
        EContainment outside = box.ContainsCapsule(new Capsule(new Vector3(10.0f, 0.0f, 0.0f), Vector3.UnitY, 0.5f, 0.5f));

        Assert.That(inside, Is.EqualTo(EContainment.Contains));
        Assert.That(outside, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void ClosestPoint_WhenOutside_ReturnsClampedPoint()
    {
        Box box = CreateBox();
        Vector3 point = new(5.0f, 0.0f, 0.0f);

        Vector3 closest = box.ClosestPoint(point, clampToEdge: false);

        Assert.That(closest.X, Is.EqualTo(2.0f).Within(1e-5f));
        Assert.That(closest.Y, Is.EqualTo(0.0f).Within(1e-5f));
        Assert.That(closest.Z, Is.EqualTo(0.0f).Within(1e-5f));
    }

    [Test]
    public void GetAabb_WhenRotated_ReturnsBoundsFromAllWorldCorners()
    {
        Box box = new(center: Vector3.Zero, size: new Vector3(2.0f, 10.0f, 2.0f))
        {
            Transform = Matrix4x4.CreateRotationZ(MathF.PI * 0.5f)
        };

        AABB bounds = box.GetAABB(transformed: true);

        Assert.That(bounds.IsValid, Is.True);
        Assert.That(bounds.Min.X, Is.EqualTo(-5.0f).Within(1e-5f));
        Assert.That(bounds.Min.Y, Is.EqualTo(-1.0f).Within(1e-5f));
        Assert.That(bounds.Min.Z, Is.EqualTo(-1.0f).Within(1e-5f));
        Assert.That(bounds.Max.X, Is.EqualTo(5.0f).Within(1e-5f));
        Assert.That(bounds.Max.Y, Is.EqualTo(1.0f).Within(1e-5f));
        Assert.That(bounds.Max.Z, Is.EqualTo(1.0f).Within(1e-5f));
    }

    [Test]
    public void WorldMinimumMaximum_WhenRotated_ReturnOrderedBounds()
    {
        Box box = new(center: Vector3.Zero, size: new Vector3(2.0f, 10.0f, 2.0f))
        {
            Transform = Matrix4x4.CreateRotationZ(MathF.PI * 0.5f)
        };

        Vector3 min = box.WorldMinimum;
        Vector3 max = box.WorldMaximum;

        Assert.That(min.X, Is.LessThanOrEqualTo(max.X));
        Assert.That(min.Y, Is.LessThanOrEqualTo(max.Y));
        Assert.That(min.Z, Is.LessThanOrEqualTo(max.Z));
    }
}
