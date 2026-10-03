using System.Collections.Concurrent;

namespace XREngine;

public static partial class Engine
{
    private static readonly ConcurrentQueue<Action> SimulationBoundaryWork = new();
    private static int _simulationBoundaryActive;
    private static int _simulationBoundaryQueued;

    /// <summary>
    /// Runs a bounded world operation between simulation steps. The asynchronous physics fence avoids
    /// blocking the update thread while an in-flight physics callback may itself require that thread.
    /// </summary>
    public static void EnqueueSimulationBoundaryTask(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Time.Timer.IsRunning)
        {
            action();
            return;
        }
        if (Interlocked.Increment(ref _simulationBoundaryQueued) > 256)
        {
            Interlocked.Decrement(ref _simulationBoundaryQueued);
            throw new InvalidOperationException("The networking simulation work queue is full.");
        }
        SimulationBoundaryWork.Enqueue(action);
        ScheduleSimulationBoundary();
    }

    private static void ScheduleSimulationBoundary()
    {
        if (!SimulationBoundaryWork.IsEmpty && Interlocked.CompareExchange(ref _simulationBoundaryActive, 1, 0) == 0)
            EnqueueUpdateThreadTask(BeginSimulationBoundary);
    }

    private static void BeginSimulationBoundary()
    {
        if (!Time.Timer.IsRunning)
        {
            ExecuteSimulationBoundary();
            return;
        }
        Time.Timer.SimulationBoundaryPaused = true;
        EnqueuePhysicsThreadTask(FenceSimulationBoundary);
    }

    private static void FenceSimulationBoundary() => EnqueueUpdateThreadTask(ExecuteSimulationBoundary);

    private static void ExecuteSimulationBoundary()
    {
        try
        {
            XREngine.Data.Core.XRObjectBase.ProcessPendingDestructions();
            XREngine.Scene.Transforms.TransformBase.ProcessParentReassignments();
            // Drain a bounded batch under one physics fence instead of pausing once per packet.
            int count = Math.Min(Volatile.Read(ref _simulationBoundaryQueued), 256);
            while (count-- > 0 && SimulationBoundaryWork.TryDequeue(out Action? action))
            {
                Interlocked.Decrement(ref _simulationBoundaryQueued);
                action();
                XREngine.Data.Core.XRObjectBase.ProcessPendingDestructions();
                XREngine.Scene.Transforms.TransformBase.ProcessParentReassignments();
            }
        }
        finally
        {
            Time.Timer.SimulationBoundaryPaused = false;
            Volatile.Write(ref _simulationBoundaryActive, 0);
            ScheduleSimulationBoundary();
        }
    }
}
