namespace XREngine.Rendering.Vulkan;

/// <summary>Describes one queued compute pipeline without reading live shader state.</summary>
public sealed record VulkanComputePipelineJobDiagnostic
{
    public string ProgramName { get; init; } = string.Empty;
    public long ProgramBindingId { get; init; }
    public ulong ProgramFingerprint { get; init; }
    public ulong ProgramLinkGeneration { get; init; }
    public long DependencyGeneration { get; init; }
    public double QueuedAgeMilliseconds { get; init; }
    public string WorkerPromiseStatus { get; init; } = string.Empty;
    public VulkanPipelineShaderStageDiagnostic Stage { get; init; }
}
