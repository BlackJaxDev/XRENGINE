using System.Security.Cryptography;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Content-validated cache for Slang-produced Vulkan SPIR-V modules.
/// </summary>
internal static class SlangVulkanShaderCache
{
    private const int SchemaVersion = 1;
    private static readonly object PublicationLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string GetRootPath()
        => Path.Combine(VulkanShaderArtifactCache.GetShaderCacheDirectoryPath(), "slang");

    internal static bool TryRead(
        string lookupKey,
        string compilerIdentity,
        out ShaderCompileResult result)
    {
        result = default!;
        string directory = GetRootPath();
        string binaryPath = Path.Combine(directory, lookupKey + ".spv");
        string metadataPath = Path.Combine(directory, lookupKey + ".json");
        if (!File.Exists(binaryPath) || !File.Exists(metadataPath))
            return false;

        try
        {
            Metadata? metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath), JsonOptions);
            if (metadata is null || metadata.SchemaVersion != SchemaVersion ||
                !string.Equals(metadata.CompilerIdentity, compilerIdentity, StringComparison.Ordinal) ||
                !DependenciesAreCurrent(metadata.Dependencies))
                return false;

            byte[] spirv = File.ReadAllBytes(binaryPath);
            if (spirv.Length == 0 || !string.Equals(Hash(spirv), metadata.SpirVHash, StringComparison.Ordinal))
                return false;

            VulkanShaderCompiler.ValidateModuleWhenRequested(metadata.EntryPoint, spirv);
            result = new ShaderCompileResult(
                spirv,
                metadata.EntryPoint,
                metadata.ArtifactIdentity,
                metadata.CompilerIdentity,
                metadata.ReflectionJson,
                metadata.Dependencies,
                metadata.Diagnostics,
                LoadedFromCache: true,
                TimeSpan.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void Write(string lookupKey, ShaderCompileResult result)
    {
        // Serialize the two-file commit so same-key background jobs cannot
        // interleave binary and metadata publication within this process.
        lock (PublicationLock)
            WriteCore(lookupKey, result);
    }

    private static void WriteCore(string lookupKey, ShaderCompileResult result)
    {
        string directory = GetRootPath();
        Directory.CreateDirectory(directory);
        string binaryPath = Path.Combine(directory, lookupKey + ".spv");
        string metadataPath = Path.Combine(directory, lookupKey + ".json");
        string token = Guid.NewGuid().ToString("N");
        string binaryTemporaryPath = binaryPath + "." + token + ".tmp";
        string metadataTemporaryPath = metadataPath + "." + token + ".tmp";

        Metadata metadata = new()
        {
            SchemaVersion = SchemaVersion,
            ArtifactIdentity = result.ArtifactIdentity,
            CompilerIdentity = result.CompilerIdentity,
            EntryPoint = result.EntryPoint,
            ReflectionJson = result.ReflectionJson,
            Dependencies = [.. result.Dependencies],
            Diagnostics = [.. result.Diagnostics],
            SpirVHash = Hash(result.SpirV),
        };

        try
        {
            File.WriteAllBytes(binaryTemporaryPath, result.SpirV);
            File.WriteAllText(metadataTemporaryPath, JsonSerializer.Serialize(metadata, JsonOptions));
            File.Move(binaryTemporaryPath, binaryPath, overwrite: true);
            File.Move(metadataTemporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            DeleteIfPresent(binaryTemporaryPath);
            DeleteIfPresent(metadataTemporaryPath);
        }
    }

    private static bool DependenciesAreCurrent(IReadOnlyList<ShaderCompileDependency> dependencies)
    {
        foreach (ShaderCompileDependency dependency in dependencies)
        {
            if (!File.Exists(dependency.Path) ||
                !string.Equals(Hash(File.ReadAllBytes(dependency.Path)), dependency.Sha256, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    internal static string Hash(ReadOnlySpan<byte> contents)
        => Convert.ToHexString(SHA256.HashData(contents));

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record Metadata
    {
        public int SchemaVersion { get; init; }
        public string ArtifactIdentity { get; init; } = string.Empty;
        public string CompilerIdentity { get; init; } = string.Empty;
        public string EntryPoint { get; init; } = "main";
        public string? ReflectionJson { get; init; }
        public ShaderCompileDependency[] Dependencies { get; init; } = [];
        public ShaderCompileDiagnostic[] Diagnostics { get; init; } = [];
        public string SpirVHash { get; init; } = string.Empty;
    }
}
