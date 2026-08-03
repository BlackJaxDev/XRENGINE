using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class CommandChainRecordingWorkerState(int workerIndex)
    {
        public int WorkerIndex { get; } = workerIndex;
        public readonly AutoResetEvent WorkAvailable = new(initialState: false);
        public readonly VulkanWorkerSecondaryCommandArena Arena =
            new(workerIndex);
        public Thread? Thread;
        public VulkanRenderer? Owner;
        public CommandChainRecordingBatch? Batch;
        public volatile bool StopRequested;
        public ulong LastFrameId;

        public void Start(VulkanRenderer owner)
        {
            Owner = owner;
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

                Owner!.RunCommandChainRecordingWorker(this);
            }
        }
    }
}

