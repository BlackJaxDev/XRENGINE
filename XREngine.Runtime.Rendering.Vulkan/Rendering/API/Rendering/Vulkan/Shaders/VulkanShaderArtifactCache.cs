using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanShaderArtifactCache
{
    internal const int SchemaVersion = 2;
    private const string CacheRootDirectoryName = "Build";
    private const string CacheSubDirectoryName = "Cache";
    private const string CacheApiDirectoryName = "Vulkan";
    private const string CacheDirectoryName = "ShaderArtifacts";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ConcurrentDictionary<string, byte> PendingWrites = new(StringComparer.Ordinal);
    private static readonly VulkanShaderArtifactRuntimeFingerprint RuntimeFingerprint = CreateRuntimeFingerprint();

    internal static string GetShaderCacheDirectoryPath()
    {
        string root = ResolveRepositoryRoot(Environment.CurrentDirectory);
        return Path.Combine(
            root,
            CacheRootDirectoryName,
            CacheSubDirectoryName,
            CacheApiDirectoryName,
            CacheDirectoryName);
    }

    internal static bool TryRead(
        string artifactIdentity,
        XRShader shader,
        int shaderConfigVersion,
        bool usesVulkanClipDepthRemap,
        string rewrittenSource,
        AutoUniformBlockInfo? autoUniformBlock,
        ShaderStageFlags stageFlags,
        out VulkanRenderer.VulkanShaderArtifact artifact)
        => TryRead(
            artifactIdentity,
            shader,
            shaderConfigVersion,
            usesVulkanClipDepthRemap,
            rewrittenSource,
            autoUniformBlock,
            stageFlags,
            string.Empty,
            out artifact);

    internal static bool TryRead(
        string artifactIdentity,
        XRShader shader,
        int shaderConfigVersion,
        bool usesVulkanClipDepthRemap,
        string rewrittenSource,
        AutoUniformBlockInfo? autoUniformBlock,
        ShaderStageFlags stageFlags,
        string transformFeedbackPlanIdentity,
        out VulkanRenderer.VulkanShaderArtifact artifact)
    {
        artifact = default!;
        if (string.IsNullOrWhiteSpace(artifactIdentity))
            return false;

        string directory = GetShaderCacheDirectoryPath();
        string binaryPath = GetBinaryPath(directory, artifactIdentity);
        string metadataPath = GetMetadataPath(binaryPath);

        if (!File.Exists(metadataPath) || !File.Exists(binaryPath))
            return false;

        VulkanShaderArtifactCacheMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<VulkanShaderArtifactCacheMetadata>(
                File.ReadAllText(metadataPath),
                JsonOptions);
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[VulkanShaderCache] Invalid metadata '{Path.GetFileName(metadataPath)}': {ex.Message}. Deleting entry.");
            DeleteCacheFiles(binaryPath);
            return false;
        }

        string rewrittenSourceHash = ComputeSha256Hex(rewrittenSource);
        if (!ValidateMetadata(
            metadata,
            binaryPath,
            artifactIdentity,
            shader,
            shaderConfigVersion,
            usesVulkanClipDepthRemap,
            rewrittenSourceHash,
            out string failureReason))
        {
            Debug.VulkanWarning($"[VulkanShaderCache] Deleting stale entry '{artifactIdentity}': {failureReason}.");
            DeleteCacheFiles(binaryPath);
            return false;
        }

        try
        {
            byte[] spirv = File.ReadAllBytes(binaryPath);
            if (spirv.Length == 0 || spirv.Length != metadata!.SpirVLength)
            {
                Debug.VulkanWarning($"[VulkanShaderCache] Deleting corrupt entry '{artifactIdentity}': payload length mismatch.");
                DeleteCacheFiles(binaryPath);
                return false;
            }

            artifact = new VulkanRenderer.VulkanShaderArtifact(
                artifactIdentity,
                shader.Type,
                metadata.EntryPoint,
                metadata.SourcePath,
                rewrittenSource,
                spirv,
                metadata.DescriptorBindings ?? Array.Empty<DescriptorBindingInfo>(),
                autoUniformBlock,
                metadata.VertexInputLocations ?? new Dictionary<string, uint>(StringComparer.Ordinal),
                stageFlags,
                shaderConfigVersion,
                usesVulkanClipDepthRemap,
                LoadedFromDiskCache: true,
                TransformFeedbackPlanIdentity: transformFeedbackPlanIdentity);

            Debug.Vulkan("[VulkanShaderCache] HIT key={0} stage={1} bytes={2}.", artifactIdentity, shader.Type, spirv.Length);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[VulkanShaderCache] Failed to read SPIR-V payload '{Path.GetFileName(binaryPath)}': {ex.Message}. Deleting entry.");
            DeleteCacheFiles(binaryPath);
            return false;
        }
    }

    internal static void QueueWrite(VulkanRenderer.VulkanShaderArtifact artifact)
    {
        if (artifact.LoadedFromDiskCache || string.IsNullOrWhiteSpace(artifact.Identity) || artifact.SpirV.Length == 0)
            return;

        if (!PendingWrites.TryAdd(artifact.Identity, 0))
            return;

        ThreadPool.QueueUserWorkItem(static state =>
        {
            var shaderArtifact = (VulkanRenderer.VulkanShaderArtifact)state!;
            try
            {
                Write(shaderArtifact);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"[VulkanShaderCache] Async artifact cache write failed for '{shaderArtifact.Identity}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                PendingWrites.TryRemove(shaderArtifact.Identity, out _);
            }
        }, artifact);
    }

    internal static void WriteForTesting(VulkanRenderer.VulkanShaderArtifact artifact)
        => Write(artifact);

    internal static void Delete(string artifactIdentity)
    {
        if (string.IsNullOrWhiteSpace(artifactIdentity))
            return;

        string binaryPath = GetBinaryPath(GetShaderCacheDirectoryPath(), artifactIdentity);
        DeleteCacheFiles(binaryPath);
    }

    private static void Write(VulkanRenderer.VulkanShaderArtifact artifact)
    {
        string directory = GetShaderCacheDirectoryPath();
        Directory.CreateDirectory(directory);

        string binaryPath = GetBinaryPath(directory, artifact.Identity);
        string metadataPath = GetMetadataPath(binaryPath);
        string relativeBinaryPath = Path.GetFileName(binaryPath);

        VulkanShaderArtifactCacheMetadata metadata = new()
        {
            SchemaVersion = SchemaVersion,
            CacheKey = artifact.Identity,
            RuntimeFingerprint = RuntimeFingerprint,
            ShaderType = artifact.ShaderType,
            EntryPoint = artifact.EntryPoint,
            SourcePath = artifact.SourcePath,
            ShaderConfigVersion = artifact.ShaderConfigVersion,
            UsesVulkanClipDepthRemap = artifact.UsesVulkanClipDepthRemap,
            RewrittenSourceHash = ComputeSha256Hex(artifact.RewrittenSource ?? string.Empty),
            SpirVLength = artifact.SpirV.Length,
            SpirVPath = relativeBinaryPath,
            DescriptorBindings = [.. artifact.DescriptorBindings],
            VertexInputLocations = new Dictionary<string, uint>(artifact.VertexInputLocations, StringComparer.Ordinal),
            AutoUniformBlockName = artifact.AutoUniformBlock?.BlockName,
            AutoUniformBlockSet = artifact.AutoUniformBlock?.Set,
            AutoUniformBlockBinding = artifact.AutoUniformBlock?.Binding,
            AutoUniformBlockSize = artifact.AutoUniformBlock?.Size,
            CreatedUtc = DateTime.UtcNow,
        };

        File.WriteAllBytes(binaryPath, artifact.SpirV);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
        Debug.Vulkan("[VulkanShaderCache] WRITE key={0} stage={1} bytes={2}.", artifact.Identity, artifact.ShaderType, artifact.SpirV.Length);
    }

    private static bool ValidateMetadata(
        VulkanShaderArtifactCacheMetadata? metadata,
        string binaryPath,
        string artifactIdentity,
        XRShader shader,
        int shaderConfigVersion,
        bool usesVulkanClipDepthRemap,
        string rewrittenSourceHash,
        out string failureReason)
    {
        if (metadata is null)
        {
            failureReason = "missing metadata";
            return false;
        }

        if (metadata.SchemaVersion != SchemaVersion)
        {
            failureReason = $"schema {metadata.SchemaVersion} != {SchemaVersion}";
            return false;
        }

        if (!string.Equals(metadata.CacheKey, artifactIdentity, StringComparison.Ordinal))
        {
            failureReason = "cache key mismatch";
            return false;
        }

        if (metadata.ShaderType != shader.Type)
        {
            failureReason = "shader stage changed";
            return false;
        }

        if (metadata.ShaderConfigVersion != shaderConfigVersion)
        {
            failureReason = "shader config version changed";
            return false;
        }

        if (metadata.UsesVulkanClipDepthRemap != usesVulkanClipDepthRemap)
        {
            failureReason = "clip-depth remap policy changed";
            return false;
        }

        if (!metadata.RuntimeFingerprint.Equals(RuntimeFingerprint))
        {
            failureReason = "runtime fingerprint changed";
            return false;
        }

        if (!string.Equals(metadata.RewrittenSourceHash, rewrittenSourceHash, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "rewritten source hash changed";
            return false;
        }

        if (!File.Exists(binaryPath))
        {
            failureReason = "missing SPIR-V payload";
            return false;
        }

        if (metadata.SpirVLength <= 0)
        {
            failureReason = "invalid SPIR-V length";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static string GetBinaryPath(string directory, string artifactIdentity)
        => Path.Combine(directory, $"{SanitizeFileName(artifactIdentity)}.spv");

    private static string GetMetadataPath(string binaryPath)
        => $"{binaryPath}.json";

    private static void DeleteCacheFiles(string binaryPath)
    {
        string metadataPath = GetMetadataPath(binaryPath);
        try
        {
            if (File.Exists(binaryPath))
                File.Delete(binaryPath);
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
        catch
        {
        }
    }

    private static string ResolveRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XRENGINE.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        return startDirectory;
    }

    private static string SanitizeFileName(string fileName)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(fileName.Length);
        foreach (char ch in fileName)
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        return builder.ToString();
    }

    private static string ComputeSha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static VulkanShaderArtifactRuntimeFingerprint CreateRuntimeFingerprint()
        => new(
            ShadercAssemblyVersion: typeof(Shaderc).Assembly.GetName().Version?.ToString() ?? string.Empty,
            TargetEnvironment: "Vulkan",
            SourceLanguage: "GLSL",
            OptimizationLevel: OptimizationLevel.Performance.ToString(),
            OptimizerIdentity: ResolvedShaderSourceOptimizer.BuildIdentitySegment(),
            RewriteIdentity: "VulkanShaderAutoUniforms+VulkanShaderTransformFeedback+InjectVulkanBackendDefine+ReflectionSourcePreprocessor:v2");
}

internal sealed record VulkanShaderArtifactCacheMetadata
{
    public int SchemaVersion { get; init; }
    public string CacheKey { get; init; } = string.Empty;
    public VulkanShaderArtifactRuntimeFingerprint RuntimeFingerprint { get; init; } = VulkanShaderArtifactRuntimeFingerprint.Unknown;
    public EShaderType ShaderType { get; init; }
    public string EntryPoint { get; init; } = "main";
    public string? SourcePath { get; init; }
    public int ShaderConfigVersion { get; init; }
    public bool UsesVulkanClipDepthRemap { get; init; }
    public string RewrittenSourceHash { get; init; } = string.Empty;
    public int SpirVLength { get; init; }
    public string SpirVPath { get; init; } = string.Empty;
    public DescriptorBindingInfo[]? DescriptorBindings { get; init; }
    public Dictionary<string, uint>? VertexInputLocations { get; init; }
    public string? AutoUniformBlockName { get; init; }
    public uint? AutoUniformBlockSet { get; init; }
    public uint? AutoUniformBlockBinding { get; init; }
    public uint? AutoUniformBlockSize { get; init; }
    public DateTime CreatedUtc { get; init; }
}

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
