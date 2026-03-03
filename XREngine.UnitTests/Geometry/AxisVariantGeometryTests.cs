using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class AxisVariantGeometryTests
{
    [Test]
    public void ConeX_Axis_UsesXAxis()
    {
        ConeX cone = new(center: Vector3.Zero, height: 3.0f, radius: 1.0f);

        Segment axis = cone.Axis;

        Assert.That(axis.End.X, Is.EqualTo(3.0f).Within(1e-5f));
        Assert.That(axis.End.Y, Is.EqualTo(0.0f).Within(1e-5f));
        Assert.That(axis.End.Z, Is.EqualTo(0.0f).Within(1e-5f));
    }

    [Test]
    public void ConeZ_ContainsPoint_AlongZAxis()
    {
        ConeZ cone = new(center: Vector3.Zero, height: 3.0f, radius: 1.0f);

        bool inside = cone.ContainsPoint(new Vector3(0.0f, 0.0f, 1.0f));
        bool outside = cone.ContainsPoint(new Vector3(0.0f, 1.2f, 0.1f));

        Assert.That(inside, Is.True);
        Assert.That(outside, Is.False);
    }

    [Test]
    public void ConeY_ContainsSphere_BasicClassification()
    {
        ConeY cone = new(center: Vector3.Zero, height: 3.0f, radius: 1.5f);

        EContainment inside = cone.ContainsSphere(new Sphere(new Vector3(0.0f, 0.5f, 0.0f), 0.2f));
        EContainment outside = cone.ContainsSphere(new Sphere(new Vector3(10.0f, 0.0f, 0.0f), 0.5f));

        Assert.That(inside, Is.Not.EqualTo(EContainment.Disjoint));
        Assert.That(outside, Is.EqualTo(EContainment.Disjoint));
    }

    [Test]
    public void CapsuleX_ContainsPoint_AlongXAxis()
    {
        CapsuleX capsule = new(center: Vector3.Zero, radius: 1.0f, halfHeight: 2.0f);

        bool inside = capsule.ContainsPoint(new Vector3(1.5f, 0.2f, 0.0f));
        bool outside = capsule.ContainsPoint(new Vector3(0.0f, 3.0f, 0.0f));

        Assert.That(inside, Is.True);
        Assert.That(outside, Is.False);
    }

    [Test]
    public void CapsuleZ_GetAabb_ExtendsAlongZ()
    {
        CapsuleZ capsule = new(center: Vector3.Zero, radius: 1.0f, halfHeight: 2.0f);

        AABB aabb = capsule.GetAABB(true);

        Assert.That(aabb.Min.Z, Is.EqualTo(-3.0f).Within(1e-5f));
        Assert.That(aabb.Max.Z, Is.EqualTo(3.0f).Within(1e-5f));
    }

    [Test]
    public void CapsuleY_ContainsAabb_BasicClassification()
    {
        CapsuleY capsule = new(center: Vector3.Zero, radius: 2.0f, halfHeight: 3.0f);

        EContainment inside = capsule.ContainsAABB(new AABB(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f)));
        EContainment outside = capsule.ContainsAABB(new AABB(new Vector3(10.0f, 10.0f, 10.0f), new Vector3(11.0f, 11.0f, 11.0f)));

        Assert.That(inside, Is.Not.EqualTo(EContainment.Disjoint));
        Assert.That(outside, Is.EqualTo(EContainment.Disjoint));
    }
}