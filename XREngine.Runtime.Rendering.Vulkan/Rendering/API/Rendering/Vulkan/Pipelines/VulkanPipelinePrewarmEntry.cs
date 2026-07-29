namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanPipelinePrewarmEntry
{
    public VulkanPipelinePrewarmEntryKind Kind { get; set; }
    public string Key { get; set; } = string.Empty;
    public int PassIndex { get; set; }
    public string PassName { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;
    public string MeshName { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    public string ProgramName { get; set; } = string.Empty;
    public string EffectName { get; set; } = string.Empty;
    public string Topology { get; set; } = string.Empty;
    public bool UseDynamicRendering { get; set; }
    public string RenderPassSignature { get; set; } = string.Empty;
    public string ColorAttachmentFormat { get; set; } = string.Empty;
    public string DepthAttachmentFormat { get; set; } = string.Empty;
    public ulong ProgramPipelineHash { get; set; }
    public ulong VertexLayoutHash { get; set; }
    public ulong DescriptorLayoutHash { get; set; }
    public ulong PassMetadataHash { get; set; }
    public ulong FeatureProfileHash { get; set; }
    public ulong FixedFunctionStateHash { get; set; }
    public string RasterizationSamples { get; set; } = string.Empty;
    public bool DepthTestEnabled { get; set; }
    public bool BlendEnabled { get; set; }
    public bool AlphaToCoverageEnabled { get; set; }
    public string ColorWriteMask { get; set; } = string.Empty;
    public string FeatureProfile { get; set; } = string.Empty;
    public int SeenCount { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public string ToProfilerSummary(bool knownAtStartup)
    {
        string status = knownAtStartup ? "known" : "new";
        return Kind == VulkanPipelinePrewarmEntryKind.Compute
            ? $"{status}:compute pass={PassIndex}:{PassName} pipe={PipelineName} program={ProgramName}"
            : $"{status}:graphics pass={PassIndex}:{PassName} pipe={PipelineName} mesh={MeshName} material={MaterialName} program={ProgramName} effect={EffectName}";
    }
}
