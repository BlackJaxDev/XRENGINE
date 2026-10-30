using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>Creates a native render-work owner for the built-in worker backend.</summary>
internal interface IEngineRenderWorkDomainFactory
{
    IRenderWorkDomainOwner CreateRenderDomain(int backgroundWorkerCount, ERenderWorkerQos qos,
        int frameSlotCount, int queueCapacityPerLane, int batchPoolCapacity,
        int initialItemCapacity, int initialDependencyCapacity, int inlineItemThreshold);
}
