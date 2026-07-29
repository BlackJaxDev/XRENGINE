namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanShaderArtifactRuntimeFingerprint(
    string ShadercAssemblyVersion,
    string TargetEnvironment,
    string SourceLanguage,
    string OptimizationLevel,
    string OptimizerIdentity,
    string RewriteIdentity)
{
    public static VulkanShaderArtifactRuntimeFingerprint Unknown { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);
}
