namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Sealed pass/pipeline variant of one canonical resident draw. Instrumented
/// and strategy-specific artifacts never share a native template slot.
/// </summary>
internal readonly record struct VulkanResidentDrawTemplateVariantKey(
    int PassIndex,
    EMeshSubmissionStrategy SubmissionStrategy,
    ulong InstrumentationSchema,
    EVulkanResidentTemplateMeshDialect MeshDialect,
    ulong OutputProfileVariant);
