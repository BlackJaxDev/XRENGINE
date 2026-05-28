using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class SkinnedMeshBoundsCalculatorTests
{
    [Test]
    public void CalculateWorldToSkinnedBoundsBasis_ConvertsWorldPrepassPositionsToBoundsLocal()
    {
        Matrix4x4 basis = Matrix4x4.CreateFromYawPitchRoll(0.45f, -0.25f, 0.15f)
            * Matrix4x4.CreateTranslation(2.0f, 6.0f, -3.0f);
        Matrix4x4 toBoundsLocal = SkinnedMeshBoundsCalculator.CalculateWorldToSkinnedBoundsBasis(basis);

        Vector3 local = new(-1.5f, 2.25f, 0.5f);
        Vector3 world = Vector3.Transform(local, basis);
        Vector3 recoveredLocal = Vector3.Transform(world, toBoundsLocal);

        recoveredLocal.X.ShouldBe(local.X, 0.0001f);
        recoveredLocal.Y.ShouldBe(local.Y, 0.0001f);
        recoveredLocal.Z.ShouldBe(local.Z, 0.0001f);
    }

    [Test]
    public void CalculateWorldToSkinnedBoundsBasis_UsesIdentityForNonInvertibleBasis()
    {
        Matrix4x4 basis = Matrix4x4.CreateScale(0.0f, 1.0f, 1.0f);

        SkinnedMeshBoundsCalculator.CalculateWorldToSkinnedBoundsBasis(basis)
            .ShouldBe(Matrix4x4.Identity);
    }
}
