using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Transforms;

namespace XREngine.UnitTests.Data;

[TestFixture]
public class SpatialCoordinateConversionTests
{
    private const float Tolerance = 1e-5f;

    private static readonly SpatialCoordinateSystem Engine = SpatialCoordinateSystem.Engine;
    private static readonly SpatialCoordinateSystem RightHandedYUp = SpatialCoordinateSystem.XRightYUpZForward;
    private static readonly SpatialCoordinateSystem ZUpRightHanded = SpatialCoordinateSystem.Blender;

    [Test]
    public void CoordinateSystems_ReportExpectedHandedness()
    {
        RightHandedYUp.IsRightHanded.ShouldBeTrue();
        Engine.IsLeftHanded.ShouldBeTrue();
        ZUpRightHanded.IsRightHanded.ShouldBeTrue();
        SpatialCoordinateSystem.Unity.ShouldBe(RightHandedYUp);
        SpatialCoordinateSystem.Mmd.ShouldBe(RightHandedYUp);
        SpatialCoordinateSystem.OpenXR.ShouldBe(Engine);
        SpatialCoordinateSystem.OpenVR.ShouldBe(Engine);
        SpatialCoordinateSystem.Unreal.IsLeftHanded.ShouldBeTrue();
    }

    [Test]
    public void Vector3_ConvertsBetweenHandedness()
    {
        Vector3 value = new(2.0f, -3.0f, 4.0f);

        Vector3 converted = SpatialCoordinateConversion.Convert(value, RightHandedYUp, Engine);

        VectorShouldBeClose(converted, new Vector3(2.0f, -3.0f, -4.0f));
    }

    [Test]
    public void PositionDirectionAndNormal_AreIdenticalForPureCoordinateSystemChanges()
    {
        Vector3 value = new(-2.0f, 5.0f, 7.0f);

        Vector3 position = SpatialCoordinateConversion.ConvertPosition(value, SpatialCoordinateSystem.Unity, SpatialCoordinateSystem.Engine);
        Vector3 direction = SpatialCoordinateConversion.ConvertDirection(value, SpatialCoordinateSystem.Unity, SpatialCoordinateSystem.Engine);
        Vector3 normal = SpatialCoordinateConversion.ConvertNormal(value, SpatialCoordinateSystem.Unity, SpatialCoordinateSystem.Engine);

        VectorShouldBeClose(position, direction);
        VectorShouldBeClose(direction, normal);
    }

    [Test]
    public void Quaternion_ConvertedRotationMatchesConvertedVectorFlow()
    {
        Vector3 sourceVector = Vector3.Normalize(new Vector3(1.0f, -2.0f, 0.5f));
        Quaternion rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.6f, -0.35f, 0.2f));

        Vector3 expected = SpatialCoordinateConversion.Convert(Vector3.Transform(sourceVector, rotation), RightHandedYUp, Engine);
        Quaternion convertedRotation = SpatialCoordinateConversion.Convert(rotation, RightHandedYUp, Engine);
        Vector3 convertedVector = SpatialCoordinateConversion.Convert(sourceVector, RightHandedYUp, Engine);
        Vector3 actual = Vector3.Transform(convertedVector, convertedRotation);

        VectorShouldBeClose(actual, expected);
    }

    [Test]
    public void Matrix4x4_ConvertedTransformMatchesConvertedPointFlow()
    {
        Matrix4x4 transform = Matrix4x4.CreateScale(new Vector3(1.5f, 0.75f, 2.25f))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, -0.2f, 0.15f)))
            * Matrix4x4.CreateTranslation(new Vector3(10.0f, -6.0f, 3.0f));
        Vector3 point = new(-2.0f, 4.0f, 1.5f);

        Matrix4x4 convertedTransform = SpatialCoordinateConversion.Convert(transform, RightHandedYUp, Engine);
        Vector3 convertedPoint = SpatialCoordinateConversion.Convert(point, RightHandedYUp, Engine);
        Vector3 expected = SpatialCoordinateConversion.Convert(Vector3.Transform(point, transform), RightHandedYUp, Engine);
        Vector3 actual = Vector3.Transform(convertedPoint, convertedTransform);

        VectorShouldBeClose(actual, expected);
    }

    [Test]
    public void AffineMatrix4x3_ConvertedTransformMatchesConvertedPointFlow()
    {
        AffineMatrix4x3 transform = AffineMatrix4x3.CreateTRS(
            new Vector3(0.8f, 1.2f, 1.5f),
            Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.5f, 0.25f, 0.3f)),
            new Vector3(-8.0f, 5.0f, 11.0f));
        Vector3 point = new(3.0f, -1.0f, 2.0f);

        AffineMatrix4x3 convertedTransform = SpatialCoordinateConversion.Convert(transform, Engine, ZUpRightHanded);
        Vector3 convertedPoint = SpatialCoordinateConversion.Convert(point, Engine, ZUpRightHanded);
        Vector3 expected = SpatialCoordinateConversion.Convert(transform.TransformPosition(point), Engine, ZUpRightHanded);
        Vector3 actual = convertedTransform.TransformPosition(convertedPoint);

        VectorShouldBeClose(actual, expected);
    }

    [Test]
    public void AxisPermutation_RoundTripsThroughDifferentUpAxis()
    {
        Vector3 value = new(3.0f, 4.0f, -2.0f);

        Vector3 converted = SpatialCoordinateConversion.Convert(value, Engine, ZUpRightHanded);
        Vector3 roundTrip = SpatialCoordinateConversion.Convert(converted, ZUpRightHanded, Engine);

        VectorShouldBeClose(roundTrip, value);

        Matrix4x4 forward = SpatialCoordinateConversion.GetVectorConversionMatrix(Engine, ZUpRightHanded);
        Matrix4x4 backward = SpatialCoordinateConversion.GetVectorConversionMatrix(ZUpRightHanded, Engine);
        MatrixShouldBeClose(forward * backward, Matrix4x4.Identity);
    }

    private static void VectorShouldBeClose(Vector3 actual, Vector3 expected)
    {
        actual.X.ShouldBe(expected.X, Tolerance);
        actual.Y.ShouldBe(expected.Y, Tolerance);
        actual.Z.ShouldBe(expected.Z, Tolerance);
    }

    private static void MatrixShouldBeClose(Matrix4x4 actual, Matrix4x4 expected)
    {
        actual.M11.ShouldBe(expected.M11, Tolerance);
        actual.M12.ShouldBe(expected.M12, Tolerance);
        actual.M13.ShouldBe(expected.M13, Tolerance);
        actual.M14.ShouldBe(expected.M14, Tolerance);
        actual.M21.ShouldBe(expected.M21, Tolerance);
        actual.M22.ShouldBe(expected.M22, Tolerance);
        actual.M23.ShouldBe(expected.M23, Tolerance);
        actual.M24.ShouldBe(expected.M24, Tolerance);
        actual.M31.ShouldBe(expected.M31, Tolerance);
        actual.M32.ShouldBe(expected.M32, Tolerance);
        actual.M33.ShouldBe(expected.M33, Tolerance);
        actual.M34.ShouldBe(expected.M34, Tolerance);
        actual.M41.ShouldBe(expected.M41, Tolerance);
        actual.M42.ShouldBe(expected.M42, Tolerance);
        actual.M43.ShouldBe(expected.M43, Tolerance);
        actual.M44.ShouldBe(expected.M44, Tolerance);
    }
}