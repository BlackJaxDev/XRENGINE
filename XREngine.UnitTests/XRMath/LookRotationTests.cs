using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.UnitTests;

[TestFixture]
public class LookRotationTests
{
    private const float Tolerance = 1e-5f;

    private static void AssertAreEqual(Vector3 expected, Vector3 actual, float tol, string message = "")
    {
        Assert.That(Math.Abs(expected.X - actual.X), Is.LessThanOrEqualTo(tol), $"{message} (X mismatch)");
        Assert.That(Math.Abs(expected.Y - actual.Y), Is.LessThanOrEqualTo(tol), $"{message} (Y mismatch)");
        Assert.That(Math.Abs(expected.Z - actual.Z), Is.LessThanOrEqualTo(tol), $"{message} (Z mismatch)");
    }

    private static void AssertAreEqual(Quaternion expected, Quaternion actual, float tol, string message = "")
    {
        Assert.That(Math.Abs(expected.X - actual.X), Is.LessThanOrEqualTo(tol), $"{message} (X mismatch)");
        Assert.That(Math.Abs(expected.Y - actual.Y), Is.LessThanOrEqualTo(tol), $"{message} (Y mismatch)");
        Assert.That(Math.Abs(expected.Z - actual.Z), Is.LessThanOrEqualTo(tol), $"{message} (Z mismatch)");
        Assert.That(Math.Abs(expected.W - actual.W), Is.LessThanOrEqualTo(tol), $"{message} (W mismatch)");
    }

    [Test]
    public void ForwardZero_ReturnsIdentityQuaternion()
    {
        Vector3 forward = Vector3.Zero;
        Vector3 up = Globals.Up;

        Quaternion result = XRMath.LookRotation(forward, up);

        AssertAreEqual(Quaternion.Identity, result, Tolerance,
            "When forward is zero, we expect the identity quaternion.");
    }

    [Test]
    public void UpZero_RotatesGloballyForwardToDesiredForward()
    {
        Vector3 forward = new(1, 0, 0); // want to look along +X
        Vector3 up = Vector3.Zero;

        Quaternion result = XRMath.LookRotation(forward, up);

        // After rotation, Globals.Forward (0,0,1) should map to (1,0,0)
        Vector3 rotatedForward = Vector3.Transform(Globals.Backward, result);
        AssertAreEqual(Vector3.Normalize(forward), Vector3.Normalize(rotatedForward), Tolerance,
            "Rotated forward did not match desired forward when up was zero.");
    }

    [Test]
    public void ForwardAndUpAligned_CreatesValid90DegreeRotation()
    {
        Vector3 forward = new(0, 0, -1);
        Vector3 up = new(0, 1, 0);

        Quaternion result = XRMath.LookRotation(forward, up);

        Vector3 rotatedForward = Vector3.Transform(Globals.Backward, result);
        Vector3 rotatedUp = Vector3.Transform(Globals.Up, result);

        AssertAreEqual(Vector3.Normalize(forward), Vector3.Normalize(rotatedForward), Tolerance,
            "Forward axis did not align properly.");
        AssertAreEqual(Vector3.Normalize(up), Vector3.Normalize(rotatedUp), Tolerance,
            "Up axis did not align properly.");
    }

    [Test]
    public void ForwardUpCollinear_FallbackRotationBetweenVectors()
    {
        Vector3 forward = new(0, 1, 0);
        Vector3 up = new(0, 1, 0);

        Quaternion result = XRMath.LookRotation(forward, up);

        // Should rotate Globals.Forward (0,0,1) to (0,1,0)
        Vector3 rotatedForward = Vector3.Transform(Globals.Backward, result);
        AssertAreEqual(Vector3.Normalize(forward), Vector3.Normalize(rotatedForward), Tolerance,
            "Forward axis did not align properly when forward and up are collinear.");
    }

    [Test]
    public void ArbitraryForwardUp_BasisIsOrthogonal()
    {
        Vector3 forward = new(1, 1, 0);
        Vector3 up = new(0, 1, 0);

        Quaternion result = XRMath.LookRotation(forward, up);
        Vector3 forwardNorm = Vector3.Normalize(forward);

        Vector3 rotatedForward = Vector3.Transform(Globals.Backward, result);
        Vector3 rotatedUp = Vector3.Transform(Globals.Up, result);

        AssertAreEqual(forwardNorm, Vector3.Normalize(rotatedForward), Tolerance,
            "Forward axis misaligned for arbitrary vectors.");

        float dotForwardUp = Vector3.Dot(Vector3.Normalize(rotatedUp), forwardNorm);
        Assert.That(Math.Abs(dotForwardUp), Is.LessThanOrEqualTo(Tolerance),
            "Up axis is not perpendicular to forward for arbitrary vectors.");

        float dotUpOriginal = Vector3.Dot(Vector3.Normalize(rotatedUp), Vector3.Normalize(up));
        Assert.That(dotUpOriginal, Is.GreaterThan(0.0f),
            "Up axis flipped opposite to the desired up direction.");
    }

    [Test]
    public void ForwardAndUpOpposite_ProducesValidRotation()
    {
        Vector3 forward = new(0, 0, -1);
        Vector3 up = new(0, -1, 0);

        Quaternion result = XRMath.LookRotation(forward, up);

        Vector3 rotatedForward = Vector3.Transform(Globals.Backward, result);
        Vector3 rotatedUp = Vector3.Transform(Globals.Up, result);

        AssertAreEqual(Vector3.Normalize(forward), Vector3.Normalize(rotatedForward), Tolerance,
            "Forward axis did not align properly when up was opposite.");
        AssertAreEqual(Vector3.Normalize(up), Vector3.Normalize(rotatedUp), Tolerance,
            "Up axis did not align properly when up was opposite.");
    }

    [Test]
    public void ForwardAndUpPerpendicular_ProducesExpectedRotation()
    {
        Vector3 forward = new(1, 0, 0);
        Vector3 up = new(0, 0, 1);

        Quaternion result = XRMath.LookRotation(forward, up);

        Vector3 rotatedForward = Vector3.Transform(Globals.Backward, result);
        Vector3 rotatedUp = Vector3.Transform(Globals.Up, result);

        AssertAreEqual(Vector3.Normalize(forward), Vector3.Normalize(rotatedForward), Tolerance,
            "Forward axis did not align properly for perpendicular vectors.");
        AssertAreEqual(Vector3.Normalize(up), Vector3.Normalize(rotatedUp), Tolerance,
            "Up axis did not align properly for perpendicular vectors.");
    }

    [Test]
    public void ForwardAndUpBothZero_ReturnsIdentityQuaternion()
    {
        Vector3 forward = Vector3.Zero;
        Vector3 up = Vector3.Zero;

        Quaternion result = XRMath.LookRotation(forward, up);

        AssertAreEqual(Quaternion.Identity, result, Tolerance,
            "When both forward and up are zero, we expect the identity quaternion.");
    }

    [Test]
    public void ForwardAndUpNearlyCollinear_ProducesValidRotation()
    {
        Vector3 forward = new(0, 1, 0);
        Vector3 up = new(0, 1, 0.001f);

        Quaternion result = XRMath.LookRotation(forward, up);

        Vector3 rotatedForward = Vector3.Transform(Globals.Backward, result);
        Vector3 rotatedUp = Vector3.Transform(Globals.Up, result);

        AssertAreEqual(Vector3.Normalize(forward), Vector3.Normalize(rotatedForward), Tolerance,
            "Forward axis did not align properly for nearly collinear vectors.");
        Assert.That(Vector3.Dot(Vector3.Normalize(rotatedUp), Vector3.Normalize(up)), Is.GreaterThan(0.0f),
            "Up axis flipped opposite to the desired up direction for nearly collinear vectors.");
    }
}