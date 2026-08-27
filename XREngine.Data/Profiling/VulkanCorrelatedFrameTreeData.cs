using MemoryPack;

namespace XREngine.Data.Profiling;

/// <summary>
/// Serializable view of one allocation-free Vulkan frame publication. The
/// diagnostic packet owns any arrays; the renderer publication remains a
/// bounded value type.
/// </summary>
[MemoryPackable]
public sealed partial class VulkanCorrelatedFrameTreeData
{
    public long AuthorityId { get; set; }
    public long PublicationSequence { get; set; }
    public ulong EngineFrameNumber { get; set; }
    public ulong RenderFrameNumber { get; set; }
    public int FrameSlot { get; set; }
    public int OutputIndex { get; set; }
    public ulong OutputGeneration { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string PresentationProfile { get; set; } = string.Empty;
    public string PresentMode { get; set; } = string.Empty;
    public double ActualPresentIntervalMs { get; set; }
    public int FramesAhead { get; set; }
    public double InclusiveMs { get; set; }
    public double StageExclusiveMs { get; set; }
    public double RootExclusiveMs { get; set; }
    public double WorkMs { get; set; }
    public double WaitMs { get; set; }
    public double NativeDriverMs { get; set; }
    public double ExternalRuntimeMs { get; set; }
    public double DiagnosticMs { get; set; }
    public double WorkerOverlapMs { get; set; }
    public double RequiredOutputCriticalPathMs { get; set; }
    public double AttributedRatio { get; set; }
    public bool HasReportableGap { get; set; }
    public bool DeviceOperational { get; set; }
    public bool DeviceLost { get; set; }
    public ulong LastSuccessfulSubmissionSerial { get; set; }
    public ulong LastSuccessfulSignalTimelineValue { get; set; }
    public int DroppedCausalWaitCount { get; set; }
    public VulkanFrameStageTelemetryData[] Stages { get; set; } = [];
    public VulkanFrameCausalWaitTelemetryData[] CausalWaits { get; set; } = [];
}
