using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class EngineExecutionTopologyTests
{
    [Test]
    public void Resolve_DefaultPhase1ARequest_PreservesLegacyGeneralWorkerBudget()
    {
        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(CreateRequest());

        topology.EffectiveProcessorCount.ShouldBe(32);
        topology.ReservedForegroundThreadCount.ShouldBe(4);
        topology.GeneralWorkerThreadCount.ShouldBe(16);
        topology.RenderWorkerThreadCount.ShouldBe(0);
        topology.DedicatedBackgroundThreadCount.ShouldBe(0);
        topology.TotalReservedThreadCount.ShouldBe(20);
        topology.IsOversubscribed.ShouldBeFalse();
    }

    [Test]
    public void Resolve_SmallProcessorBudget_PreservesOneLegacyGeneralWorker()
    {
        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(
            CreateRequest() with { EffectiveProcessorCount = 4 });

        topology.ReservedForegroundThreadCount.ShouldBe(3);
        topology.GeneralWorkerThreadCount.ShouldBe(1);
        topology.RenderWorkerThreadCount.ShouldBe(0);
        topology.TotalReservedThreadCount.ShouldBe(4);
    }

    [Test]
    public void Resolve_AutomaticRenderWorkers_UsesBoundedOneThirdPolicy()
    {
        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(
            CreateRequest() with
            {
                RenderWorkerThreadCount = EngineExecutionTopology.AutomaticWorkerCount,
            });

        topology.RenderWorkerThreadCount.ShouldBe(8);
        topology.GeneralWorkerThreadCount.ShouldBe(16);
        topology.TotalReservedThreadCount.ShouldBe(28);
    }

    [Test]
    public void Resolve_ExplicitOversubscription_ThrowsWithRequestedAndEffectiveCounts()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            EngineExecutionTopology.Resolve(
                CreateRequest() with
                {
                    GeneralWorkerThreadCount = 32,
                    GeneralWorkerThreadCap = 32,
                    RenderWorkerThreadCount = 32,
                    RenderWorkerThreadCap = 32,
                    ReservedForegroundThreadCount = 32,
                }));

        exception.Message.ShouldContain("effectiveProcessors=32");
        exception.Message.ShouldContain("total=96");
        exception.Message.ShouldContain("AllowCpuOversubscription");
    }

    [Test]
    public void Resolve_ExplicitOversubscriptionOptIn_IsRetainedAsDiagnosticState()
    {
        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(
            CreateRequest() with
            {
                GeneralWorkerThreadCount = 32,
                GeneralWorkerThreadCap = 32,
                RenderWorkerThreadCount = 32,
                RenderWorkerThreadCap = 32,
                ReservedForegroundThreadCount = 32,
                AllowCpuOversubscription = true,
            });

        topology.IsOversubscribed.ShouldBeTrue();
        topology.AllowCpuOversubscription.ShouldBeTrue();
        topology.TotalReservedThreadCount.ShouldBe(96);
    }

    [TestCase(0)]
    [TestCase(33)]
    public void Resolve_InvalidGeneralWorkerCap_FailsVisibly(int cap)
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            EngineExecutionTopology.Resolve(CreateRequest() with { GeneralWorkerThreadCap = cap }));

        exception.Message.ShouldContain(nameof(EngineExecutionTopologyRequest.GeneralWorkerThreadCap));
    }

    [Test]
    public void CreateDiagnosticSummary_ReportsSourcesAndPhase1BSchedulerBoundary()
    {
        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(
            CreateRequest() with
            {
                GeneralWorkerThreadCountSource = EEngineExecutionSettingSource.User,
                RenderWorkerThreadCountSource = EEngineExecutionSettingSource.Environment,
            });

        string summary = topology.CreateDiagnosticSummary();

        summary.ShouldContain("general:User");
        summary.ShouldContain("render:Environment");
        summary.ShouldContain("requests={foreground:-1,general:-1,generalCap:16,render:0,renderCap:8,dedicated:0}");
        summary.ShouldContain("phase1B=scheduler-active");
        summary.ShouldContain("existing Vulkan/OpenXR recording workers unchanged");
    }

    [Test]
    public void Resolve_NoRemainingAutomaticGeneralBudget_SelectsCooperativeInlineMode()
    {
        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(
            CreateRequest() with
            {
                EffectiveProcessorCount = 4,
                ReservedForegroundThreadCount = 4,
            });

        topology.GeneralWorkerThreadCount.ShouldBe(0);
        topology.TotalReservedThreadCount.ShouldBe(4);
        topology.IsOversubscribed.ShouldBeFalse();
    }

    private static EngineExecutionTopologyRequest CreateRequest()
        => new()
        {
            EffectiveProcessorCount = 32,
            GeneralWorkerThreadCount = EngineExecutionTopology.AutomaticWorkerCount,
            GeneralWorkerThreadCap = EngineExecutionTopology.DefaultGeneralWorkerCap,
            RenderWorkerThreadCount = 0,
            RenderWorkerThreadCap = EngineExecutionTopology.DefaultRenderWorkerCap,
            ReservedForegroundThreadCount = EngineExecutionTopology.AutomaticWorkerCount,
            DedicatedBackgroundThreadCount = 0,
            AllowCpuOversubscription = false,
            RenderWorkerQos = ERenderWorkerQos.OsDefault,
            GeneralWorkerThreadCountSource = EEngineExecutionSettingSource.EngineDefault,
            GeneralWorkerThreadCapSource = EEngineExecutionSettingSource.EngineDefault,
            RenderWorkerThreadCountSource = EEngineExecutionSettingSource.EngineDefault,
            RenderWorkerThreadCapSource = EEngineExecutionSettingSource.EngineDefault,
            ReservedForegroundThreadCountSource = EEngineExecutionSettingSource.EngineDefault,
            AllowCpuOversubscriptionSource = EEngineExecutionSettingSource.EngineDefault,
            RenderWorkerQosSource = EEngineExecutionSettingSource.EngineDefault,
            ForegroundThreadNames = ["render/window", "update", "fixed-update", "collect-visible/swap"],
        };
}
