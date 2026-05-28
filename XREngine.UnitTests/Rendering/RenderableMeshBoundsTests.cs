using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Geometry;
using XREngine.Rendering.Compute;
using XREngine.Scene.Transforms;

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

    [Test]
    public void HasUsableSkinnedBoundsResult_AcceptsGpuOnlyBoundsWithoutCpuPositions()
    {
        AABB bounds = new(new Vector3(-1f, -2f, -3f), new Vector3(1f, 2f, 3f));
        var result = new SkinnedMeshBoundsCalculator.Result([], bounds, Matrix4x4.Identity);

        RenderableMesh.HasUsableSkinnedBoundsResult(result).ShouldBeTrue();
    }

    [Test]
    public void HasUsableSkinnedBoundsResult_RejectsDefaultEmptyBoundsWithoutCpuPositions()
    {
        var result = new SkinnedMeshBoundsCalculator.Result([], default, Matrix4x4.Identity);

        RenderableMesh.HasUsableSkinnedBoundsResult(result).ShouldBeFalse();
    }

    [Test]
    public void ResolveSkinnedRootBoneTransform_PrefersSerializedRootBoneOverInferredAncestor()
    {
        var serializedRootBone = new Transform();
        var inferredAncestor = new Transform();

        TransformBase? rootBone = RenderableMesh.ResolveSkinnedRootBoneTransform(
            serializedRootBone,
            inferredAncestor);

        ReferenceEquals(rootBone, serializedRootBone).ShouldBeTrue();
    }

    [Test]
    public void ResolveSkinnedBoundsBasisTransform_PrefersRootBoneOverImportRoot()
    {
        var importRoot = new Transform();
        var rootBone = new Transform();
        TransformBase? basis = RenderableMesh.ResolveSkinnedBoundsBasisTransform(
            rootBone,
            importRoot);

        ReferenceEquals(basis, rootBone).ShouldBeTrue();
    }

    [Test]
    public void ResolveSkinnedBoundsBasisTransform_FallsBackToImportRootWithoutRootBone()
    {
        var importRoot = new Transform();
        TransformBase? basis = RenderableMesh.ResolveSkinnedBoundsBasisTransform(null, importRoot);

        ReferenceEquals(basis, importRoot).ShouldBeTrue();
    }
}
