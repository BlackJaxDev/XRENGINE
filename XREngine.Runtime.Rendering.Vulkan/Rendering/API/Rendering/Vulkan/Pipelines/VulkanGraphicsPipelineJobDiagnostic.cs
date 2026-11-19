namespace XREngine.Rendering.Vulkan;

/// <summary>Describes one queued graphics pipeline without reading live shader state.</summary>
public sealed record VulkanGraphicsPipelineJobDiagnostic
{
    public string PipelineName { get; init; } = string.Empty;
    public string ProgramName { get; init; } = string.Empty;
    public long OwnerId { get; init; }
    public ulong ProgramPipelineHash { get; init; }
    public ulong InterfaceGeneration { get; init; }
    public long DependencyGeneration { get; init; }
    public bool UsesGraphicsPipelineLibraries { get; init; }
    public bool IsMeshShaderPipeline { get; init; }
    public double QueuedAgeMilliseconds { get; init; }
    public string WorkerPromiseStatus { get; init; } = string.Empty;
    public string PublicationPromiseStatus { get; init; } = string.Empty;
    public VulkanPipelineShaderStageDiagnostic[] Stages { get; init; } = [];
}
