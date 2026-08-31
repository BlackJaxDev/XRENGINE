using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit provenance for a Vulkan persistence cohort. Native driver blobs are
/// still validated by the driver; this sidecar prevents headless evidence from
/// treating a different engine build, target mode, or shader set as equivalent.
/// </summary>
public sealed record VulkanPipelineCacheIdentity
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string EngineBuildRevision { get; init; } = string.Empty;
    public string EngineAssemblySha256 { get; init; } = string.Empty;
    public string DriverIdentity { get; init; } = string.Empty;
    public string RequestedTargetMode { get; init; } = string.Empty;
    public string EffectiveTargetMode { get; init; } = string.Empty;
    public string[] ShaderArtifactFingerprints { get; init; } = [];

    internal static string GetEngineBuildRevision()
    {
        Assembly assembly = typeof(VulkanPipelineCacheIdentity).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;
    }

    internal static string GetEngineAssemblyHash()
    {
        string path = typeof(VulkanPipelineCacheIdentity).Assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void Write(string path, VulkanPipelineCacheIdentity identity)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true }));
    }
}
