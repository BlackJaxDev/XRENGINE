using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedAttributeReconstructionNumericalTests
{
    [Test]
    public void DerivativeContract_CoversMinificationAnisotropyDiscontinuitiesAndTinyTriangles()
    {
        Vector2 textureSize = new(4096.0f, 4096.0f);
        AdvancedReconstructionDerivativeResult constantUv =
            AdvancedReconstructionDerivativeContract.ResolveSelectedMip(
                Vector2.Zero,
                Vector2.Zero,
                textureSize,
                mipCount: 13u,
                derivativesValid: true);
        AdvancedReconstructionDerivativeResult minified =
            AdvancedReconstructionDerivativeContract.ResolveSelectedMip(
                new Vector2(0.01f, 0.0f),
                new Vector2(0.0f, 0.01f),
                textureSize,
                mipCount: 13u,
                derivativesValid: true);
        AdvancedReconstructionDerivativeResult anisotropic =
            AdvancedReconstructionDerivativeContract.ResolveSelectedMip(
                new Vector2(0.001f, 0.0f),
                new Vector2(0.0f, 0.1f),
                textureSize,
                mipCount: 13u,
                derivativesValid: true);
        AdvancedReconstructionDerivativeResult rapidlyChanging =
            AdvancedReconstructionDerivativeContract.ResolveSelectedMip(
                new Vector2(0.25f, 0.0f),
                new Vector2(0.0f, 0.25f),
                textureSize,
                mipCount: 13u,
                derivativesValid: true);
        AdvancedReconstructionDerivativeResult tinyTriangle =
            AdvancedReconstructionDerivativeContract.ResolveSelectedMip(
                new Vector2(float.PositiveInfinity),
                Vector2.Zero,
                textureSize,
                mipCount: 13u,
                derivativesValid: false);

        constantUv.UsesConservativeMip.ShouldBeFalse();
        constantUv.SelectedMip.ShouldBe(0.0f);
        minified.UsesConservativeMip.ShouldBeFalse();
        minified.SelectedMip.ShouldBeGreaterThan(0.0f);
        anisotropic.SelectedMip.ShouldBeGreaterThan(
            minified.SelectedMip);
        rapidlyChanging.SelectedMip.ShouldBeGreaterThanOrEqualTo(
            anisotropic.SelectedMip);
        tinyTriangle.UsesConservativeMip.ShouldBeTrue();
        tinyTriangle.SelectedMip.ShouldBe(12.0f);

        AdvancedVisibilityPayloadWords center = new(7u, 11u);
        AdvancedReconstructionDerivativeContract.MayCompareNeighbor(
                center,
                center)
            .ShouldBeTrue();
        AdvancedReconstructionDerivativeContract.MayCompareNeighbor(
                center,
                new AdvancedVisibilityPayloadWords(7u, 12u))
            .ShouldBeFalse();
        AdvancedReconstructionDerivativeContract.CalculateError(
                new Vector2(0.1f, 0.2f),
                new Vector2(0.3f, 0.4f),
                new Vector2(0.1f, 0.2f),
                new Vector2(0.3f, 0.4f))
            .ShouldBe(0.0f);
    }

    [Test]
    public void TangentContract_PreservesHardSmoothUvSeamAndMirroredIslandSemantics()
    {
        AdvancedReconstructionTangentSpace.TryCreate(
                Matrix4x4.Identity,
                Vector3.Zero,
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.UnitX,
                localHandedness: 1.0f,
                out AdvancedReconstructionTangentFrame smooth)
            .ShouldBeTrue();
        AdvancedReconstructionTangentSpace.TryCreate(
                Matrix4x4.Identity,
                Vector3.Zero,
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitY,
                Vector3.UnitX,
                localHandedness: 1.0f,
                out AdvancedReconstructionTangentFrame hard)
            .ShouldBeTrue();
        AdvancedReconstructionTangentSpace.TryCreate(
                Matrix4x4.Identity,
                Vector3.Zero,
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                -Vector3.UnitX,
                localHandedness: -1.0f,
                out AdvancedReconstructionTangentFrame mirroredIsland)
            .ShouldBeTrue();

        Vector3.Distance(
                smooth.GeometricNormal,
                hard.GeometricNormal)
            .ShouldBeLessThan(1.0e-6f);
        Vector3.Distance(
                smooth.ShadingNormal,
                hard.ShadingNormal)
            .ShouldBeGreaterThan(0.5f);
        Vector3.Dot(smooth.ShadingNormal, smooth.Tangent)
            .ShouldBe(0.0f, 1.0e-6);
        mirroredIsland.Handedness.ShouldBe(-1.0f);
        Vector3.Dot(
                mirroredIsland.ShadingNormal,
                mirroredIsland.Bitangent)
            .ShouldBe(0.0f, 1.0e-6);
    }

    [TestCase(EAdvancedVelocityValidityReason.NewlyVisible)]
    [TestCase(EAdvancedVelocityValidityReason.Teleported)]
    [TestCase(EAdvancedVelocityValidityReason.TopologyChanged)]
    [TestCase(EAdvancedVelocityValidityReason.VertexCountChanged)]
    [TestCase(EAdvancedVelocityValidityReason.HistoryReset)]
    [TestCase(EAdvancedVelocityValidityReason.ArenaOverflow)]
    [TestCase(EAdvancedVelocityValidityReason.FrameGap)]
    public void TemporalContract_InvalidatesEveryHistoryBreak(
        EAdvancedVelocityValidityReason reason)
    {
        AdvancedReconstructionMotion result =
            AdvancedReconstructionTemporalContract.Resolve(
                new Vector4(0.5f, 0.0f, 0.0f, 1.0f),
                new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                reason,
                maskedEdge: false);

        result.IsValid.ShouldBeFalse();
        result.NdcMotion.ShouldBe(Vector2.Zero);
        result.IsReactive.ShouldBeTrue();
    }
}
