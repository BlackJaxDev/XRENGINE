using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable CPU-side result of compiling one Vulkan shader variant.
/// </summary>
internal sealed record VulkanShaderArtifact(
    string Identity,
    EShaderType ShaderType,
    string EntryPoint,
    string? SourcePath,
    string? RewrittenSource,
    byte[] SpirV,
    IReadOnlyList<DescriptorBindingInfo> DescriptorBindings,
    AutoUniformBlockInfo? AutoUniformBlock,
    IReadOnlyDictionary<string, uint> VertexInputLocations,
    ShaderStageFlags StageFlags,
    int ShaderConfigVersion,
    bool UsesVulkanClipDepthRemap,
    bool LoadedFromDiskCache = false,
    string TransformFeedbackPlanIdentity = "",
    IReadOnlyList<AutoUniformBlockInfo>? FrequencyOwnedAutoUniformBlocks = null)
{
    internal IReadOnlyList<AutoUniformBlockInfo> AutoUniformBlocks
        => FrequencyOwnedAutoUniformBlocks is { Count: > 0 } blocks
            ? blocks
            : AutoUniformBlock is null
                ? Array.Empty<AutoUniformBlockInfo>()
                : [AutoUniformBlock];
}
