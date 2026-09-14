using XREngine;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Installs the scheduler required by native presentationless renderer setup only
/// when this fixture is the first owner of the process-wide runtime scheduler.
/// </summary>
internal sealed class VulkanPresentationIndependentHostWorkSchedulerScope : IDisposable
{
    private readonly bool _ownsScheduler;
    private bool _disposed;

    private VulkanPresentationIndependentHostWorkSchedulerScope(bool ownsScheduler)
        => _ownsScheduler = ownsScheduler;

    public static VulkanPresentationIndependentHostWorkSchedulerScope EnsureInstalled()
    {
        if (Engine.WorkScheduler is not null)
            return new VulkanPresentationIndependentHostWorkSchedulerScope(ownsScheduler: false);

        EngineExecutionTopology topology = EngineExecutionTopology.Resolve(new EngineExecutionTopologyRequest
        {
            EffectiveProcessorCount = Environment.ProcessorCount,
            GeneralWorkerThreadCount = EngineExecutionTopology.AutomaticWorkerCount,
            GeneralWorkerThreadCap = EngineExecutionTopology.DefaultGeneralWorkerCap,
            RenderWorkerThreadCount = EngineExecutionTopology.AutomaticWorkerCount,
            RenderWorkerThreadCap = EngineExecutionTopology.DefaultRenderWorkerCap,
            ReservedForegroundThreadCount = 1,
            DedicatedBackgroundThreadCount = 0,
            AllowCpuOversubscription = false,
            RenderWorkerQos = ERenderWorkerQos.OsDefault,
            ForegroundThreadNames = ["presentationless renderer test"],
        });
        RuntimeWorkScheduler.Configure(topology, generalQueueLimit: null, generalQueueWarningThreshold: null);
        return new VulkanPresentationIndependentHostWorkSchedulerScope(ownsScheduler: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsScheduler && !RuntimeWorkScheduler.Shutdown(waitForWorkers: true))
        {
            throw new InvalidOperationException(
                "The presentationless renderer test could not quiesce its owned runtime work scheduler.");
        }
    }
}
