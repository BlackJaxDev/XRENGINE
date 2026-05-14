using NUnit.Framework;
using Shouldly;
using System.Runtime.CompilerServices;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class GpuIndirectPhase3ScaffoldTests
{
    [Test]
    public void EngineSettings_OcclusionMode_DefaultsToGpuHiZ()
    {
        var settings = new XREngine.Engine.Rendering.EngineSettings();

        settings.GpuOcclusionCullingMode.ShouldBe(EOcclusionCullingMode.GpuHiZ);
    }

    [Test]
    public void EngineSettings_OcclusionMode_CanSwitchBetweenModes()
    {
        var settings = new XREngine.Engine.Rendering.EngineSettings();

        settings.GpuOcclusionCullingMode = EOcclusionCullingMode.GpuHiZ;
        settings.GpuOcclusionCullingMode.ShouldBe(EOcclusionCullingMode.GpuHiZ);

        settings.GpuOcclusionCullingMode = EOcclusionCullingMode.CpuQueryAsync;
        settings.GpuOcclusionCullingMode.ShouldBe(EOcclusionCullingMode.CpuQueryAsync);

        settings.GpuOcclusionCullingMode = EOcclusionCullingMode.CpuSoftwareOcclusion;
        settings.GpuOcclusionCullingMode.ShouldBe(EOcclusionCullingMode.CpuSoftwareOcclusion);

        settings.GpuOcclusionCullingMode = EOcclusionCullingMode.Disabled;
        settings.GpuOcclusionCullingMode.ShouldBe(EOcclusionCullingMode.Disabled);
    }

    [Test]
    public void GpuRenderPass_OcclusionCounters_DefaultToZero()
    {
        var pass = (GPURenderPassCollection)RuntimeHelpers.GetUninitializedObject(typeof(GPURenderPassCollection));

        pass.OcclusionCandidatesTested.ShouldBe(0u);
        pass.OcclusionAccepted.ShouldBe(0u);
        pass.OcclusionFalsePositiveRecoveries.ShouldBe(0u);
        pass.OcclusionTemporalOverrides.ShouldBe(0u);
    }
}
