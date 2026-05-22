using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests;

[TestFixture]
public sealed class FrustumBoxContainmentTests
{
    [Test]
    public void IntersectsAabb_WhenBoxStraddlesSidePlane_ReturnsTrue()
    {
        Frustum frustum = new(width: 10.0f, height: 10.0f, nearPlane: 1.0f, farPlane: 20.0f);
        AABB box = new(new Vector3(4.9f, -0.5f, -5.5f), new Vector3(5.5f, 0.5f, -4.5f));

        Assert.That(frustum.Intersects(box), Is.True);
        Assert.That(frustum.ContainsAABB(box), Is.EqualTo(EContainment.Intersects));
    }

    [Test]
    public void ContainsBox_WhenTopDownFrustumCrossesTallBox_ReturnsIntersects()
    {
        Frustum frustum = new Frustum(width: 10.0f, height: 10.0f, nearPlane: 1.0f, farPlane: 20.0f)
            .TransformedBy(Matrix4x4.CreateRotationX(-MathF.PI * 0.5f) * Matrix4x4.CreateTranslation(0.0f, 10.0f, 0.0f));
        Box box = new(center: Vector3.Zero, size: new Vector3(0.5f, 30.0f, 0.5f), transform: Matrix4x4.Identity);

        Assert.That(frustum.ContainsBox(box), Is.EqualTo(EContainment.Intersects));
    }
}
