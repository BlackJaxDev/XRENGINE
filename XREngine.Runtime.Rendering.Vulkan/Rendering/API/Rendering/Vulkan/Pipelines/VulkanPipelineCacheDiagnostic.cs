namespace XREngine.Rendering.Vulkan;

/// <summary>Snapshot of the real Vulkan persistence stores and their current provenance.</summary>
public sealed record VulkanPipelineCacheDiagnostic
{
    public string? CacheRootOverride { get; init; }
    public string NativePipelineCachePath { get; init; } = string.Empty;
    public long NativePipelineCacheInitialBytes { get; init; }
    public string PrewarmDatabasePath { get; init; } = string.Empty;
    public int PrewarmEntryCount { get; init; }
    public bool PrewarmCaptureEnabled { get; init; }
    public string ShaderArtifactDirectory { get; init; } = string.Empty;
    public int ShaderArtifactFileCount { get; init; }
    public string IdentityPath { get; init; } = string.Empty;
    public VulkanPipelineCacheIdentity Identity { get; init; } = new();
    public VulkanPipelineTelemetrySnapshot Telemetry { get; init; } = new();
}
