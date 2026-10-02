namespace XREngine.RenderBench;

/// <summary>Versioned evidence for repeatable runtime measurements.</summary>
public sealed class RuntimeDataLayoutReport
{
    public int Version { get; } = 1;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string[] CommandLine { get; } = Environment.GetCommandLineArgs();
    public string Runtime { get; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string OperatingSystem { get; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public int ProcessId { get; } = Environment.ProcessId;
    public int ProcessorCount { get; } = Environment.ProcessorCount;
    public bool ServerGc { get; } = System.Runtime.GCSettings.IsServerGC;
    public string? ManifestSha256 { get; set; }
    public List<RuntimeAssetMeasurement> Assets { get; } = [];
    public List<RuntimeNetworkMeasurement> Networking { get; } = [];
    public List<RuntimeTransformMeasurement> Transforms { get; } = [];
    public string[] MeasurementNotes { get; } =
    [
        "GC.GetGCMemoryInfo reports the last GC's LOH size, not LOH allocation counts or bytes. Exact LOH allocation events require a separate allocation trace.",
        "Codec networking lanes exclude sockets, identity admission, and simulation dispatch; use the local two-client lane for end-to-end acceptance.",
        "Production frame cost includes renderer submission and canonical publication; propagation is timed separately before submission. Canonical publication measures world/viewport swap and canonical-package finalization together.",
        "Synthetic local rotations exercise the imported hierarchy deterministically; this is not an animation-clip playback or headset result."
    ];
    public string? Failure { get; set; }
}
