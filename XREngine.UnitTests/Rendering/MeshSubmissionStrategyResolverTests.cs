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
    public void ResolveMeshSubmissionStrategy_ForcedStrategy_BypassesProfileAndCapabilities()
    {
        Resolve(
                forcedStrategy: EMeshSubmissionStrategy.GpuMeshlet,
                requestedGpuDispatch: false,
                supportsIndirectCountDraw: false)
            .ShouldBe(EMeshSubmissionStrategy.GpuMeshlet);
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
            SupportsMeshletDispatch: supportsMeshletDispatch);

        return Engine.Rendering.ResolveMeshSubmissionStrategy(inputs);
    }
}
