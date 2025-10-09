using NUnit.Framework;
using System.Numerics;
using XREngine.Data.Core;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests
{
    [TestFixture]
    public class RotationBetweenVectorsTests
    {
        [Test]
        public void RotationBetweenVectors_IdenticalVectors_ReturnsIdentityQuaternion()
        {
            // Arrange
            Vector3 vector1 = new(1, 0, 0);
            Vector3 vector2 = new(1, 0, 0);

            // Act
            Quaternion result = XRMath.RotationBetweenVectors(vector1, vector2);

            // Assert
            Assert.That(XRMath.IsApproximatelyIdentity(result, XRMath.Epsilon));
        }

        [Test]
        public void RotationBetweenVectors_OppositeVectors_Returns180DegreeRotation()
        {
            // Arrange
            Vector3 vector1 = new(1, 0, 0);
            Vector3 vector2 = new(-1, 0, 0);

            // Act
            Quaternion result = XRMath.RotationBetweenVectors(vector1, vector2);

            // Assert
            ToAngleAxis(result, out float angle, out _);
            Assert.That(angle, Is.EqualTo(MathF.PI).Within(XRMath.Epsilon));
        }

        [Test]
        public void RotationBetweenVectors_PerpendicularVectors_Returns90DegreeRotation()
        {
            // Arrange
            Vector3 vector1 = new(1, 0, 0);
            Vector3 vector2 = new(0, 1, 0);

            // Act
            Quaternion result = XRMath.RotationBetweenVectors(vector1, vector2);

            // Assert
            Vector3 rotatedVector = Vector3.Transform(vector1, result);
            Assert.That(XRMath.Approx(rotatedVector, vector2, XRMath.Epsilon));
        }

        [Test]
        public void RotationBetweenVectors_NonNormalizedVectors_ReturnsCorrectRotation()
        {
            // Arrange
            Vector3 vector1 = new(2, 0, 0);
            Vector3 vector2 = new(0, 3, 0);

            // Act
            Quaternion result = XRMath.RotationBetweenVectors(vector1, vector2);

            // Assert
            Vector3 rotatedVector = Vector3.Transform(Vector3.Normalize(vector1), result);
            Assert.That(XRMath.Approx(rotatedVector, Vector3.Normalize(vector2), XRMath.Epsilon));
        }

        private static void ToAngleAxis(Quaternion rotation, out float angle, out Vector3 axis)
        {
            angle = 2.0f * MathF.Acos(rotation.W);
            float den = MathF.Sqrt(1.0f - rotation.W * rotation.W);
            axis = den > XRMath.Epsilon ? new Vector3(rotation.X / den, rotation.Y / den, rotation.Z / den) : Vector3.UnitX;
        }
    }
}
