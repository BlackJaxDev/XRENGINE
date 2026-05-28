using System.Numerics;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class ModelImporterTransformTests
{
    [Test]
    public void CalculateAssimpUnskinnedGeometryTransform_PreservesOriginalWorldPoseWhenRenderedUnderNormalizedNode()
    {
        Matrix4x4 originalNodeWorld = Matrix4x4.CreateScale(2.0f, 0.5f, 1.5f)
            * Matrix4x4.CreateFromYawPitchRoll(0.35f, -0.2f, 0.1f)
            * Matrix4x4.CreateTranslation(3.0f, 4.0f, -5.0f);
        Matrix4x4 normalizedNodeWorld = Matrix4x4.CreateFromYawPitchRoll(0.35f, -0.2f, 0.1f)
            * Matrix4x4.CreateTranslation(3.0f, 4.0f, -5.0f);

        Matrix4x4 geometryTransform = ModelImporter.CalculateAssimpUnskinnedGeometryTransform(
            originalNodeWorld,
            normalizedNodeWorld);

        Vector3 vertex = new(1.25f, -2.0f, 0.75f);
        Vector3 rendered = Vector3.Transform(Vector3.Transform(vertex, geometryTransform), normalizedNodeWorld);
        Vector3 expected = Vector3.Transform(vertex, originalNodeWorld);

        rendered.X.ShouldBe(expected.X, 0.0001f);
        rendered.Y.ShouldBe(expected.Y, 0.0001f);
        rendered.Z.ShouldBe(expected.Z, 0.0001f);
    }

    [Test]
    public void CalculateAssimpSkinnedGeometryTransform_PreservesOriginalWorldPoseWhenRenderedUnderRoot()
    {
        Matrix4x4 rootWorld = Matrix4x4.CreateFromYawPitchRoll(-0.15f, 0.4f, 0.2f)
            * Matrix4x4.CreateTranslation(-8.0f, 1.5f, 3.0f);
        Matrix4x4 originalNodeWorld = Matrix4x4.CreateScale(0.8f, 1.4f, 1.1f)
            * Matrix4x4.CreateFromYawPitchRoll(0.2f, 0.15f, -0.3f)
            * Matrix4x4.CreateTranslation(5.0f, -2.0f, 7.0f);
        Matrix4x4.Invert(rootWorld, out Matrix4x4 inverseRootWorld);

        Matrix4x4 geometryTransform = ModelImporter.CalculateAssimpSkinnedGeometryTransform(
            originalNodeWorld,
            inverseRootWorld);

        Vector3 vertex = new(-1.0f, 0.25f, 2.0f);
        Vector3 rendered = Vector3.Transform(Vector3.Transform(vertex, geometryTransform), rootWorld);
        Vector3 expected = Vector3.Transform(vertex, originalNodeWorld);

        rendered.X.ShouldBe(expected.X, 0.0001f);
        rendered.Y.ShouldBe(expected.Y, 0.0001f);
        rendered.Z.ShouldBe(expected.Z, 0.0001f);
    }
}
