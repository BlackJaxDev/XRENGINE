using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class RenderWorkFactoryCapabilityTests
{
    [Test]
    public void WorkerFactoryWithoutRenderCapability_FailsExplicitly()
    {
        var factory = new WorkerFactoryWithoutRenderCapability();

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() =>
            new RenderWorkDomain(0, ERenderWorkerQos.OsDefault,
                RenderWorkDomain.DefaultFrameSlotCount,
                RenderWorkDomain.DefaultQueueCapacityPerLane,
                RenderWorkDomain.DefaultBatchPoolCapacity,
                RenderWorkDomain.DefaultInitialItemCapacity,
                RenderWorkDomain.DefaultInitialDependencyCapacity,
                RenderWorkDomain.DefaultInlineItemThreshold,
                factory));

        error.Message.ShouldContain("does not provide native render-work execution");
    }

    [Test]
    public void InvalidArguments_AreRejectedBeforeFactoryCapability()
    {
        var factory = new WorkerFactoryWithoutRenderCapability();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            new RenderWorkDomain(-1, ERenderWorkerQos.OsDefault,
                RenderWorkDomain.DefaultFrameSlotCount,
                RenderWorkDomain.DefaultQueueCapacityPerLane,
                RenderWorkDomain.DefaultBatchPoolCapacity,
                RenderWorkDomain.DefaultInitialItemCapacity,
                RenderWorkDomain.DefaultInitialDependencyCapacity,
                RenderWorkDomain.DefaultInlineItemThreshold,
                factory)).ParamName.ShouldBe("backgroundWorkerCount");
    }

    private sealed class WorkerFactoryWithoutRenderCapability : IEngineWorkerDomainFactory
    {
        public IEngineGeneralWorkDomain CreateGeneralDomain(JobManager jobs, int workerCount)
            => throw new NotSupportedException();

        public IEngineJobAuxiliaryWorkDomain CreateAuxiliaryDomain(JobManager jobs)
            => throw new NotSupportedException();
    }
}
