using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChainRecordingWorkerState(int workerIndex)
{
    public int WorkerIndex { get; } = workerIndex;
    public readonly AutoResetEvent WorkAvailable = new(initialState: false);
    public readonly VulkanWorkerSecondaryCommandArena Arena =
        new(workerIndex);
    public Thread? Thread;
    public VulkanCommandChainRecordingBatch? Batch;
    public volatile bool StopRequested;
    public ulong LastFrameId;

    public void Start()
    {
        Thread = new Thread(static state => ((CommandChainRecordingWorkerState)state!).Run())
        {
            IsBackground = true,
            Name = $"Vulkan Command Chain {WorkerIndex}",
        };
        Thread.Start(this);
    }

    private void Run()
    {
        while (true)
        {
            WorkAvailable.WaitOne();
            if (StopRequested)
                return;

            VulkanCommandChainRecordingBatch? batch = Batch;
            batch?.WorkerProcedure?.Invoke(this);
        }
    }
}

