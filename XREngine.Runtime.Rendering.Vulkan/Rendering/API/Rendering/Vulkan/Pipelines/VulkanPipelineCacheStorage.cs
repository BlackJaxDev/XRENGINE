namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resolves the three Vulkan pipeline persistence stores. A process-local root
/// override makes headless cold/warm cohorts independent without changing the
/// normal editor cache locations.
/// </summary>
internal static class VulkanPipelineCacheStorage
{
    internal static string GetNativePipelineCacheDirectory()
        => ResolveDirectory(
            "PipelineCache",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XREngine",
            "Vulkan",
            "PipelineCache");

    internal static string GetPrewarmDirectory()
        => ResolveDirectory(
            "PipelinePrewarm",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XREngine",
            "Vulkan",
            "PipelinePrewarm");

    internal static string GetShaderArtifactDirectory(string repositoryRoot)
        => ResolveDirectory(
            "ShaderArtifacts",
            repositoryRoot,
            "Build",
            "Cache",
            "Vulkan",
            "ShaderArtifacts");

    internal static string? GetRootOverride()
    {
        string? configured = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanPipelineCacheRoot);
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
    }

    private static string ResolveDirectory(string isolatedName, params string[] defaultSegments)
    {
        string? root = GetRootOverride();
        return root is null ? Path.Combine(defaultSegments) : Path.Combine(root, isolatedName);
    }
}
