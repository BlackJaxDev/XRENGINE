using MemoryPack;

namespace XREngine.Data.Profiling;

/// <summary>Serializable profiler row for one stable Vulkan frame stage.</summary>
[MemoryPackable]
public sealed partial class VulkanFrameStageTelemetryData
{
    public string Name { get; set; } = string.Empty;
    public double ElapsedMs { get; set; }
    public double WorkMs { get; set; }
    public double WaitMs { get; set; }
    public double NativeDriverMs { get; set; }
    public double ExternalRuntimeMs { get; set; }
    public double DiagnosticMs { get; set; }
    public int IntervalCount { get; set; }
    public string LastIntervalClass { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string WaitReason { get; set; } = string.Empty;
}
