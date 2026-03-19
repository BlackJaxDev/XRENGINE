using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests;

[TestFixture]
public class FrustumCornerOrderingTests
{
    [Test]
    public void InverseProjectionConstructor_ProducesDocumentedCornerOrder()
    {
        Matrix4x4 inverseProjection = Matrix4x4.Identity;

        Frustum frustum = new(inverseProjection);

        Assert.That(frustum.Corners[0], Is.EqualTo(new Vector3(-1.0f, -1.0f, 0.0f)));
        Assert.That(frustum.Corners[1], Is.EqualTo(new Vector3(-1.0f, 1.0f, 0.0f)));
        Assert.That(frustum.Corners[2], Is.EqualTo(new Vector3(1.0f, -1.0f, 0.0f)));
        Assert.That(frustum.Corners[3], Is.EqualTo(new Vector3(1.0f, 1.0f, 0.0f)));
        Assert.That(frustum.Corners[4], Is.EqualTo(new Vector3(-1.0f, -1.0f, 1.0f)));
        Assert.That(frustum.Corners[5], Is.EqualTo(new Vector3(-1.0f, 1.0f, 1.0f)));
        Assert.That(frustum.Corners[6], Is.EqualTo(new Vector3(1.0f, -1.0f, 1.0f)));
        Assert.That(frustum.Corners[7], Is.EqualTo(new Vector3(1.0f, 1.0f, 1.0f)));
    }
}