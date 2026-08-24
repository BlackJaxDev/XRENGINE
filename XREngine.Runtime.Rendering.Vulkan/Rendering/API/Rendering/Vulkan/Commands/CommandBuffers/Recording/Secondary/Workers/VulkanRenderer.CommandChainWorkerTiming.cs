using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct CommandChainWorkerTiming(
    int QueuedChains,
    int WorkersStarted,
    int WorkersCompleted,
    int PeakConcurrentWorkers,
    TimeSpan QueueDelay,
    TimeSpan WorkerRecordTime,
    TimeSpan WorkerActiveSpan,
    TimeSpan WorkerOverlapTime,
    TimeSpan WaitForWorkersTime);

