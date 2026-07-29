namespace XREngine.Rendering.Vulkan;

internal sealed record VulkanShaderArtifactCacheMetadata
{
    public int SchemaVersion { get; init; }
    public string CacheKey { get; init; } = string.Empty;
    public VulkanShaderArtifactRuntimeFingerprint RuntimeFingerprint { get; init; } =
        VulkanShaderArtifactRuntimeFingerprint.Unknown;
    public EShaderType ShaderType { get; init; }
    public string EntryPoint { get; init; } = "main";
    public string? SourcePath { get; init; }
    public int ShaderConfigVersion { get; init; }
    public bool UsesVulkanClipDepthRemap { get; init; }
    public string RewrittenSourceHash { get; init; } = string.Empty;
    public int SpirVLength { get; init; }
    public string SpirVPath { get; init; } = string.Empty;
    public DescriptorBindingInfo[]? DescriptorBindings { get; init; }
    public Dictionary<string, uint>? VertexInputLocations { get; init; }
    public string? AutoUniformBlockName { get; init; }
    public uint? AutoUniformBlockSet { get; init; }
    public uint? AutoUniformBlockBinding { get; init; }
    public uint? AutoUniformBlockSize { get; init; }
    public DateTime CreatedUtc { get; init; }
}
