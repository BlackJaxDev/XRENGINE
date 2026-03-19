using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderableMeshBoundsTests
{
    [Test]
    public void TransformBounds_AffineMatrix_MatchesCornerWiseMatrixTransform()
    {
        var method = typeof(RenderableMesh).GetMethod("TransformBounds", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        AABB bounds = new(new Vector3(-2f, -1f, -3f), new Vector3(2f, 4f, 5f));
        Matrix4x4 matrix = Matrix4x4.CreateScale(1.25f, 0.75f, 2.0f)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.25f, -0.3f, 0.4f)))
            * Matrix4x4.CreateTranslation(6f, -2f, 9f);

        AABB transformed = (AABB)method!.Invoke(null, [bounds, matrix])!;
        AABB expected = bounds.Transformed(p => Vector3.Transform(p, matrix));

        transformed.Min.X.ShouldBe(expected.Min.X, 0.0001f);
        transformed.Min.Y.ShouldBe(expected.Min.Y, 0.0001f);
        transformed.Min.Z.ShouldBe(expected.Min.Z, 0.0001f);
        transformed.Max.X.ShouldBe(expected.Max.X, 0.0001f);
        transformed.Max.Y.ShouldBe(expected.Max.Y, 0.0001f);
        transformed.Max.Z.ShouldBe(expected.Max.Z, 0.0001f);
    }
}