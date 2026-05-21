using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MeshSubmissionStrategyResolverTests
{
    [Test]
    public void ResolveMeshSubmissionStrategy_GpuDispatchDisabled_UsesCpuDirect()
    {
        Resolve(requestedGpuDispatch: false)
            .ShouldBe(EMeshSubmissionStrategy.CpuDirect);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedNonMeshletStrategy_BypassesProfileAndCapabilities()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuIndirectZeroReadback,
                requestedGpuDispatch: false,
                supportsIndirectCountDraw: false)
            .ShouldBe(EMeshSubmissionStrategy.GpuIndirectZeroReadback);
    }

    [Test]
    public void MeshSubmissionStrategyPredicates_ClassifyMeshletVariants()
    {
        EMeshSubmissionStrategy.GpuMeshletZeroReadback.IsAnyMeshletStrategy().ShouldBeTrue();
        EMeshSubmissionStrategy.GpuMeshletInstrumented.IsAnyMeshletStrategy().ShouldBeTrue();
        EMeshSubmissionStrategy.GpuIndirectZeroReadback.IsAnyMeshletStrategy().ShouldBeFalse();

        EMeshSubmissionStrategy.GpuMeshletInstrumented.IsInstrumentedMeshletStrategy().ShouldBeTrue();
        EMeshSubmissionStrategy.GpuMeshletZeroReadback.IsInstrumentedMeshletStrategy().ShouldBeFalse();

        EMeshSubmissionStrategy.GpuMeshletZeroReadback.IsZeroReadbackMeshletStrategy().ShouldBeTrue();
        EMeshSubmissionStrategy.GpuMeshletInstrumented.IsZeroReadbackMeshletStrategy().ShouldBeFalse();
        EMeshSubmissionStrategy.GpuMeshletInstrumented.ToZeroReadbackMeshletStrategy()
            .ShouldBe(EMeshSubmissionStrategy.GpuMeshletZeroReadback);
    }

    [Test]
    public void MeshSubmissionStrategyParser_AcceptsLegacyGpuMeshletTokenAsZeroReadback()
    {
        EMeshSubmissionStrategyExtensions.TryParseMeshSubmissionStrategy(
                "GpuMeshlet",
                out EMeshSubmissionStrategy strategy,
                out bool usedLegacyName)
            .ShouldBeTrue();

        strategy.ShouldBe(EMeshSubmissionStrategy.GpuMeshletZeroReadback);
        usedLegacyName.ShouldBeTrue();
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedGpuMeshletZeroReadbackWithProductionSupport_UsesZeroReadback()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshletZeroReadback,
                meshShaderDialect: EMeshShaderDialect.VulkanEXT,
                supportsIndirectCountDraw: true,
                supportsIndirectCountMeshTaskDispatch: true,
                supportsMeshletDispatch: true)
            .ShouldBe(EMeshSubmissionStrategy.GpuMeshletZeroReadback);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedGpuMeshletInstrumentedWithDiagnostics_UsesInstrumented()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshletInstrumented,
                meshShaderDialect: EMeshShaderDialect.VulkanEXT,
                activeProfile: EVulkanGpuDrivenProfile.Diagnostics,
                vulkanProfileActive: true,
                supportsIndirectCountDraw: true,
                supportsIndirectCountMeshTaskDispatch: true,
                supportsMeshletDispatch: true)
            .ShouldBe(EMeshSubmissionStrategy.GpuMeshletInstrumented);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedGpuMeshletInstrumentedWithoutDiagnostics_FallsBackToZeroReadback()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshletInstrumented,
                meshShaderDialect: EMeshShaderDialect.VulkanEXT,
                supportsIndirectCountDraw: true,
                supportsIndirectCountMeshTaskDispatch: true,
                supportsMeshletDispatch: true)
            .ShouldBe(EMeshSubmissionStrategy.GpuMeshletZeroReadback);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedGpuMeshletUnsupportedWithIndirectCount_FallsBackToZeroReadback()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshletZeroReadback,
                meshShaderDialect: EMeshShaderDialect.None,
                supportsIndirectCountDraw: true,
                supportsMeshletDispatch: false)
            .ShouldBe(EMeshSubmissionStrategy.GpuIndirectZeroReadback);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedGpuMeshletDiagnosticDirectDispatch_FallsBackToZeroReadback()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshletZeroReadback,
                meshShaderDialect: EMeshShaderDialect.OpenGLNV,
                supportsIndirectCountDraw: true,
                supportsDirectMeshTaskDispatch: true,
                supportsIndirectCountMeshTaskDispatch: false,
                supportsMeshletDispatch: false)
            .ShouldBe(EMeshSubmissionStrategy.GpuIndirectZeroReadback);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedGpuMeshletUnsupportedWithoutIndirectCountStrict_UsesCpuDirect()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshletZeroReadback,
                supportsIndirectCountDraw: false,
                enforceStrictNoFallbacks: true,
                supportsMeshletDispatch: false)
            .ShouldBe(EMeshSubmissionStrategy.CpuDirect);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ForcedGpuMeshletUnsupportedWithoutIndirectCountPermissive_UsesInstrumented()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshletZeroReadback,
                supportsIndirectCountDraw: false,
                enforceStrictNoFallbacks: false,
                supportsMeshletDispatch: false)
            .ShouldBe(EMeshSubmissionStrategy.GpuIndirectInstrumented);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_DiagnosticsProfile_UsesInstrumentedIndirect()
    {
        Resolve(
                activeProfile: EVulkanGpuDrivenProfile.Diagnostics,
                vulkanProfileActive: true)
            .ShouldBe(EMeshSubmissionStrategy.GpuIndirectInstrumented);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ShippingFastWithCountDraw_UsesZeroReadback()
    {
        Resolve(
                activeProfile: EVulkanGpuDrivenProfile.ShippingFast,
                vulkanProfileActive: true,
                supportsIndirectCountDraw: true)
            .ShouldBe(EMeshSubmissionStrategy.GpuIndirectZeroReadback);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ShippingFastWithoutCountDrawStrict_DowngradesToCpuDirect()
    {
        Resolve(
                activeProfile: EVulkanGpuDrivenProfile.ShippingFast,
                vulkanProfileActive: true,
                enforceStrictNoFallbacks: true,
                supportsIndirectCountDraw: false)
            .ShouldBe(EMeshSubmissionStrategy.CpuDirect);
    }

    [Test]
    public void ResolveMeshSubmissionStrategy_ShippingFastWithoutCountDrawPermissive_DowngradesToInstrumented()
    {
        Resolve(
                activeProfile: EVulkanGpuDrivenProfile.ShippingFast,
                vulkanProfileActive: true,
                enforceStrictNoFallbacks: false,
                supportsIndirectCountDraw: false)
            .ShouldBe(EMeshSubmissionStrategy.GpuIndirectInstrumented);
    }

    private static EMeshSubmissionStrategy Resolve(
        bool requestedGpuDispatch = true,
        EMeshSubmissionStrategy? forcedStrategy = null,
        bool enableGpuIndirectDebugLogging = false,
        bool enableGpuIndirectValidationLogging = false,
        bool enableGpuIndirectCpuFallback = false,
        bool enableZeroReadbackMaterialScatter = false,
        bool enableEditorZeroReadbackMaterialScatter = false,
        bool vulkanProfileActive = false,
        EVulkanGpuDrivenProfile activeProfile = EVulkanGpuDrivenProfile.DevParity,
        bool enforceStrictNoFallbacks = false,
        bool supportsIndirectCountDraw = true,
        EMeshShaderDialect meshShaderDialect = EMeshShaderDialect.None,
        bool supportsDirectMeshTaskDispatch = false,
        bool supportsIndirectCountMeshTaskDispatch = false,
        bool supportsMeshletDispatch = false)
    {
        var inputs = new Engine.Rendering.MeshSubmissionStrategyResolverInputs(
            RequestedGpuDispatch: requestedGpuDispatch,
            ForcedStrategy: forcedStrategy,
            EnableGpuIndirectDebugLogging: enableGpuIndirectDebugLogging,
            EnableGpuIndirectValidationLogging: enableGpuIndirectValidationLogging,
            EnableGpuIndirectCpuFallback: enableGpuIndirectCpuFallback,
            EnableZeroReadbackMaterialScatter: enableZeroReadbackMaterialScatter,
            EnableEditorZeroReadbackMaterialScatter: enableEditorZeroReadbackMaterialScatter,
            VulkanFeatureProfileActive: vulkanProfileActive,
            ActiveVulkanProfile: activeProfile,
            EnforceStrictNoFallbacks: enforceStrictNoFallbacks,
            SupportsIndirectCountDraw: supportsIndirectCountDraw,
            MeshShaderDialect: meshShaderDialect,
            SupportsDirectMeshTaskDispatch: supportsDirectMeshTaskDispatch,
            SupportsIndirectCountMeshTaskDispatch: supportsIndirectCountMeshTaskDispatch,
            SupportsMeshletDispatch: supportsMeshletDispatch);

        return Engine.Rendering.ResolveMeshSubmissionStrategy(inputs);
    }
}
