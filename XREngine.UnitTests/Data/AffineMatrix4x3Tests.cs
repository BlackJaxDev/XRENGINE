using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Transforms;

namespace XREngine.UnitTests.Data;

[TestFixture]
public class AffineMatrix4x3Tests
{
    private const float Tolerance = 1e-4f;

    [Test]
    public void FromMatrix4x4_RoundTripsAffineMatrix()
    {
        Matrix4x4 matrix = Matrix4x4.CreateScale(new Vector3(1.5f, 2.0f, 0.75f))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(0.2f, -0.3f, 0.1f, 0.9f)))
            * Matrix4x4.CreateTranslation(new Vector3(10.0f, -5.0f, 3.0f));

        AffineMatrix4x3 affine = AffineMatrix4x3.FromMatrix4x4(matrix);
        Matrix4x4 roundTrip = affine.ToMatrix4x4();

        MatrixShouldBeClose(roundTrip, matrix);
    }

    [Test]
    public void TransformPosition_MatchesMatrix4x4AcrossRepresentativeSamples()
    {
        var random = new Random(1234);

        for (int index = 0; index < 128; index++)
        {
            Vector3 scale = new(
                0.5f + (float)random.NextDouble() * 3.0f,
                0.5f + (float)random.NextDouble() * 3.0f,
                0.5f + (float)random.NextDouble() * 3.0f);
            Quaternion rotation = Quaternion.Normalize(new Quaternion(
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble()));
            Vector3 translation = new(
                -50.0f + (float)random.NextDouble() * 100.0f,
                -50.0f + (float)random.NextDouble() * 100.0f,
                -50.0f + (float)random.NextDouble() * 100.0f);
            Vector3 point = new(
                -10.0f + (float)random.NextDouble() * 20.0f,
                -10.0f + (float)random.NextDouble() * 20.0f,
                -10.0f + (float)random.NextDouble() * 20.0f);
            Vector3 direction = Vector3.Normalize(new Vector3(
                -1.0f + (float)random.NextDouble() * 2.0f,
                -1.0f + (float)random.NextDouble() * 2.0f,
                -1.0f + (float)random.NextDouble() * 2.0f));

            Matrix4x4 matrix = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation);
            AffineMatrix4x3 affine = AffineMatrix4x3.CreateTRS(scale, rotation, translation);

            VectorShouldBeClose(affine.TransformPosition(point), Vector3.Transform(point, matrix));
            VectorShouldBeClose(affine.TransformDirection(direction), Vector3.TransformNormal(direction, matrix));
        }
    }

    [Test]
    public void Multiply_MatchesMatrix4x4Composition()
    {
        var random = new Random(4321);

        for (int index = 0; index < 64; index++)
        {
            AffineMatrix4x3 left = CreateRandomAffine(random);
            AffineMatrix4x3 right = CreateRandomAffine(random);

            Matrix4x4 expected = left.ToMatrix4x4() * right.ToMatrix4x4();
            Matrix4x4 actual = (left * right).ToMatrix4x4();

            MatrixShouldBeClose(actual, expected);
        }
    }

    [Test]
    public void Invert_MatchesMatrix4x4Inverse()
    {
        var random = new Random(2468);

        for (int index = 0; index < 64; index++)
        {
            AffineMatrix4x3 matrix = CreateRandomAffine(random);
            bool affineInverted = AffineMatrix4x3.Invert(matrix, out AffineMatrix4x3 affineInverse);
            bool matrixInverted = Matrix4x4.Invert(matrix.ToMatrix4x4(), out Matrix4x4 expectedInverse);

            affineInverted.ShouldBe(matrixInverted);
            affineInverted.ShouldBeTrue();
            MatrixShouldBeClose(affineInverse.ToMatrix4x4(), expectedInverse);

            Matrix4x4 identity = (matrix * affineInverse).ToMatrix4x4();
            MatrixShouldBeClose(identity, Matrix4x4.Identity);
        }
    }

    [Test]
    public void TryFromMatrix4x4_RejectsProjectiveMatrices()
    {
        Matrix4x4 projective = Matrix4x4.Identity;
        projective.M14 = 0.25f;

        AffineMatrix4x3.TryFromMatrix4x4(projective, out _).ShouldBeFalse();
    }

    private static AffineMatrix4x3 CreateRandomAffine(Random random)
    {
        Vector3 scale = new(
            0.25f + (float)random.NextDouble() * 4.0f,
            0.25f + (float)random.NextDouble() * 4.0f,
            0.25f + (float)random.NextDouble() * 4.0f);
        Quaternion rotation = Quaternion.Normalize(new Quaternion(
            (float)random.NextDouble(),
            (float)random.NextDouble(),
            (float)random.NextDouble(),
            (float)random.NextDouble()));
        Vector3 translation = new(
            -100.0f + (float)random.NextDouble() * 200.0f,
            -100.0f + (float)random.NextDouble() * 200.0f,
            -100.0f + (float)random.NextDouble() * 200.0f);
        return AffineMatrix4x3.CreateTRS(scale, rotation, translation);
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

    private static void VectorShouldBeClose(Vector3 actual, Vector3 expected)
    {
        actual.X.ShouldBe(expected.X, Tolerance);
        actual.Y.ShouldBe(expected.Y, Tolerance);
        actual.Z.ShouldBe(expected.Z, Tolerance);
    }
}