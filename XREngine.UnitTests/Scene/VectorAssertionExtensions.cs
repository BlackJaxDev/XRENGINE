using System.Numerics;
using Shouldly;

namespace XREngine.UnitTests.Scene;

/// <summary>Tolerance assertions for vectors, which Shouldly does not provide.</summary>
internal static class VectorAssertionExtensions
{
    /// <summary>Asserts that each component of <paramref name="actual"/> is within <paramref name="tolerance"/> of <paramref name="expected"/>.</summary>
    public static void ShouldBeNear(this Vector3 actual, Vector3 expected, float tolerance)
    {
        actual.X.ShouldBe(expected.X, tolerance, $"X of {actual} (expected {expected})");
        actual.Y.ShouldBe(expected.Y, tolerance, $"Y of {actual} (expected {expected})");
        actual.Z.ShouldBe(expected.Z, tolerance, $"Z of {actual} (expected {expected})");
    }
}
