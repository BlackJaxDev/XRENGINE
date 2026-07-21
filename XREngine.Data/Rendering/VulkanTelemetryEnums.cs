using System;

namespace XREngine;

public enum EVulkanCpuStage
{
    FrameOpPreparation,
    ResourcePlanning,
    FrameDataRefresh,
    PacketConstruction,
    PrimaryRecording,
    SecondaryRecording,
    DescriptorPublication,
    Submission,
    Count,
}

[Flags]
public enum EVulkanCommandBufferDecisionReason
{
    None = 0,
    ReusedClean = 1 << 0,
    Recorded = 1 << 1,
    ForcedDirty = 1 << 2,
    FrameOpSignature = 1 << 3,
    ResourcePlan = 1 << 4,
    ProfilerMode = 1 << 5,
    FrameData = 1 << 6,
    DynamicOverlay = 1 << 7,
    SwapchainLifecycle = 1 << 8,
    CommandChainPrimary = 1 << 9,
    PrimaryFrameState = 1 << 10,
    DescriptorGeneration = 1 << 11,
    ResourceAllocation = 1 << 12,
    Evicted = 1 << 13,
    SecondaryRecorded = 1 << 14,
    SecondaryReused = 1 << 15,
    SecondaryFrameDataRefreshed = 1 << 16,
    PipelineGeneration = 1 << 17,
    SecondaryInvalid = 1 << 18,
    VolatileCommand = 1 << 19,
}

public enum EVulkanPipelineTelemetryEvent
{
    AsyncQueued,
    QueueRejected,
    DrawNotReady,
    CompileRequired,
    CreationCompleted,
    RequiredPipelineRecordDeferred,
    RenderThreadShaderCompile,
}

public enum EVulkanDriverPipelineCacheOutcome
{
    Unknown,
    PersistedHit,
    RuntimeHit,
    Miss,
}
