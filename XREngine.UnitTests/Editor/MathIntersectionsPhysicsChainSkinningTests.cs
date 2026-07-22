using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class MathIntersectionsPhysicsChainSkinningTests
{
    [Test]
    public void MeshLocalInverseBind_PreservesBindPoseUnderRotatedParent()
    {
        Matrix4x4 visualParentWorld =
            Matrix4x4.CreateFromYawPitchRoll(0.45f, -0.2f, 0.1f)
            * Matrix4x4.CreateTranslation(7.0f, 3.0f, -5.0f);
        Matrix4x4 boneLocal =
            Matrix4x4.CreateRotationX(0.3f)
            * Matrix4x4.CreateTranslation(0.0f, 0.5f, 1.2f);
        Matrix4x4 boneBindWorld = boneLocal * visualParentWorld;
        Matrix4x4.Invert(boneBindWorld, out Matrix4x4 boneInverseWorld).ShouldBeTrue();

        Matrix4x4 inverseBind = EditorUnitTests.ComposePhysicsChainMeshLocalInverseBind(
            visualParentWorld,
            boneInverseWorld);
        Vector3 bindVertex = new(0.2f, -0.1f, 1.8f);
        Vector3 expectedWorld = Vector3.Transform(bindVertex, visualParentWorld);
        Vector3 actualWorld = Vector3.Transform(bindVertex, inverseBind * boneBindWorld);

        Vector3.Distance(actualWorld, expectedWorld).ShouldBeLessThan(1.0e-5f);
    }
}
