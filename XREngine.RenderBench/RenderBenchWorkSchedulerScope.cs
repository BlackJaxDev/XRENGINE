using XREngine;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine.RenderBench;

/// <summary>
/// Owns the RenderBench runtime scheduler only when the host did not already install one.
/// </summary>
internal sealed class RenderBenchWorkSchedulerScope : IDisposable
{
    private readonly bool _ownsScheduler;
    private bool _disposed;

    private RenderBenchWorkSchedulerScope(bool ownsScheduler)
        => _ownsScheduler = ownsScheduler;

    public static RenderBenchWorkSchedulerScope EnsureInstalled()
    {
        if (Engine.WorkScheduler is not null)
            return new RenderBenchWorkSchedulerScope(ownsScheduler: false);

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
            ForegroundThreadNames = ["renderbench explicit submission"],
        });
        RuntimeWorkScheduler.Configure(topology, generalQueueLimit: null, generalQueueWarningThreshold: null);
        return new RenderBenchWorkSchedulerScope(ownsScheduler: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsScheduler && !RuntimeWorkScheduler.Shutdown(waitForWorkers: true))
        {
            throw new InvalidOperationException(
                "RenderBench could not quiesce its owned runtime work scheduler.");
        }
    }
}
