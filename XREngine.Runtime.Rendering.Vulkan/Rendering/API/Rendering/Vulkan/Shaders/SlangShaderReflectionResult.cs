namespace XREngine.Rendering.Vulkan;

/// <summary>Physical reflection joined to validated engine binding providers.</summary>
internal sealed record SlangShaderReflectionResult(
    IReadOnlyList<DescriptorBindingInfo> DescriptorBindings,
    IReadOnlyList<AutoUniformBlockInfo> AutoUniformBlocks,
    IReadOnlyDictionary<string, uint> VertexInputLocations);
