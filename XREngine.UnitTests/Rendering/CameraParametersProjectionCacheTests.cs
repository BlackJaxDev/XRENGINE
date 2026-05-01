using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests;

[TestFixture]
public class CameraParametersProjectionCacheTests
{
    [Test]
    public void GetUntransformedFrustum_AsymmetricProjection_UsesCachedInverseWithoutRecursing()
    {
        XROpenXRFovCameraParameters parameters = new(0.1f, 100.0f);
        parameters.SetAngles(-0.62f, 0.74f, 0.58f, -0.53f);

        Matrix4x4 projection = parameters.GetProjectionMatrix();
        Matrix4x4 inverseProjection = parameters.GetInverseProjectionMatrix();
        Frustum frustum = parameters.GetUntransformedFrustum();

        Assert.That(Matrix4x4.Invert(projection, out Matrix4x4 expectedInverse), Is.True);
        AssertMatrixNearlyEqual(inverseProjection, expectedInverse);

        foreach (Vector3 corner in frustum.Corners)
        {
            Assert.That(float.IsFinite(corner.X), Is.True);
            Assert.That(float.IsFinite(corner.Y), Is.True);
            Assert.That(float.IsFinite(corner.Z), Is.True);
        }
    }

    private static void AssertMatrixNearlyEqual(Matrix4x4 actual, Matrix4x4 expected)
    {
        Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(1e-5f));
        Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(1e-5f));
        Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(1e-5f));
        Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(1e-5f));
    }
}
