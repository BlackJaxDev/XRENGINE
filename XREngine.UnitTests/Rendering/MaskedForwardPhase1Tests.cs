using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaskedForwardPhase1Tests
{
    [Test]
    public void MaskedTransparency_RoutesToMaskedForward_AndRestoresOpaquePass()
    {
        XRMaterial material = new();
        material.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;

        material.TransparencyMode = ETransparencyMode.Masked;

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.MaskedForward);
        material.RenderOptions.ShouldNotBeNull();
        material.RenderOptions!.DepthTest.ShouldNotBeNull();
        material.RenderOptions.DepthTest!.UpdateDepth.ShouldBeTrue();
        material.RenderOptions.BlendModeAllDrawBuffers.ShouldNotBeNull();
        material.RenderOptions.BlendModeAllDrawBuffers!.Enabled.ShouldBe(ERenderParamUsage.Disabled);

        material.TransparencyMode = ETransparencyMode.Opaque;

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.OpaqueDeferred);
        material.RenderOptions.DepthTest.UpdateDepth.ShouldBeTrue();
    }

    [Test]
    public void GpuSortPolicy_TreatsMaskedForwardAsOpaqueDomain()
    {
        GpuSortPolicy.ResolveSortDomain(
            (int)EDefaultRenderPass.MaskedForward,
            EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront)
            .ShouldBe(EGpuSortDomain.OpaqueFrontToBack);
    }
}