namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanShaderArtifactRuntimeFingerprint(
    string ShadercAssemblyVersion,
    string ShadercNativeIdentity,
    string TargetEnvironment,
    string TargetSpirvVersion,
    string ShaderAbiIdentity,
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
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);
}
