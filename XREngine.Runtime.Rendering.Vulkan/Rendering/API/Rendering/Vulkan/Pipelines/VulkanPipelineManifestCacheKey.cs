using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanPipelineManifestCacheKey(
    ulong PlanCompatibilityIdentity,
    ulong PipelineDemandSignature,
    EMeshSubmissionStrategy SubmissionStrategy,
    bool DynamicRendering);
