namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable diagnostic snapshot for the last failed Vulkan shader compilation.
/// </summary>
internal sealed record VulkanShaderCompileFailure(
    string? ArtifactIdentity,
    EShaderCompileFailureKind FailureKind,
    string FailureReason,
    string? DiagnosticPath,
    string? RewrittenSource);
