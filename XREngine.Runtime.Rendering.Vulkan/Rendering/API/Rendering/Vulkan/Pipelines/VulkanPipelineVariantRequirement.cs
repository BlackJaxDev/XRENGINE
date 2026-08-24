namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanPipelineVariantRequirement(
    int OpIndex,
    int PassIndex,
    string PassName,
    bool Required,
    EMeshSubmissionStrategy SubmissionStrategy,
    bool Shadow,
    bool Velocity,
    bool EditorId,
    bool MaterialOverride,
    bool Stereo,
    bool Multiview,
    bool DynamicRendering,
    bool LegacyRenderPass,
    ulong PreparationCompatibilitySignature);
