using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedProductionCutoverContractTests
{
    [Test]
    public void CutoverContract_ValidatesReadiness()
    {
        bool ready = AdvancedProductionCutoverContract.EvaluateCutoverReadiness(
            hasClassification: true,
            hasNativeShading: true,
            hasTransparency: true,
            hasStereoMultiview: true,
            isClassicGBufferEliminated: true,
            isOpenXrEyeOwnershipPreserved: true,
            out string? blocker);

        ready.ShouldBeTrue();
        blocker.ShouldBeNull();
    }

    [Test]
    public void CutoverContract_DetectsMissingMilestones()
    {
        bool readyWithoutShading = AdvancedProductionCutoverContract.EvaluateCutoverReadiness(
            hasClassification: true,
            hasNativeShading: false,
            hasTransparency: true,
            hasStereoMultiview: true,
            isClassicGBufferEliminated: true,
            isOpenXrEyeOwnershipPreserved: true,
            out string? blocker);

        readyWithoutShading.ShouldBeFalse();
        blocker.ShouldNotBeNull();
        blocker.ShouldContain("ARP 07");

        bool readyWithGBuffer = AdvancedProductionCutoverContract.EvaluateCutoverReadiness(
            hasClassification: true,
            hasNativeShading: true,
            hasTransparency: true,
            hasStereoMultiview: true,
            isClassicGBufferEliminated: false,
            isOpenXrEyeOwnershipPreserved: true,
            out blocker);

        readyWithGBuffer.ShouldBeFalse();
        blocker.ShouldNotBeNull();
        blocker.ShouldContain("G-Buffer");
    }

    [Test]
    public void ArchitectureBudgetVerifier_EnforcesSteadyStateAllocations()
    {
        AdvancedArchitectureBudgetVerifier.VerifyZeroSteadyStateAllocations(0L).ShouldBeTrue();
        AdvancedArchitectureBudgetVerifier.VerifyZeroSteadyStateAllocations(1024L).ShouldBeFalse();
    }

    [Test]
    public void ArchitectureBudgetVerifier_EnforcesDescriptorSetBounds()
    {
        AdvancedArchitectureBudgetVerifier.VerifyDescriptorSetLayout(0u).ShouldBeTrue();
        AdvancedArchitectureBudgetVerifier.VerifyDescriptorSetLayout(1u).ShouldBeTrue();
        AdvancedArchitectureBudgetVerifier.VerifyDescriptorSetLayout(2u).ShouldBeTrue();
        AdvancedArchitectureBudgetVerifier.VerifyDescriptorSetLayout(3u).ShouldBeTrue();
        AdvancedArchitectureBudgetVerifier.VerifyDescriptorSetLayout(4u).ShouldBeFalse();
    }

    [Test]
    public void CutoverContract_NamesMatchProductionArchitecture()
    {
        AdvancedProductionCutoverContract.ProductionPipelineName.ShouldBe("AdvancedRenderPipeline");
        AdvancedProductionCutoverContract.ProductionOpenXrPipelineName.ShouldBe("RvcRenderPipeline");
    }
}
