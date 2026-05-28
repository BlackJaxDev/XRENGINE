using Silk.NET.OpenGL;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using XREngine;
using XREngine.Data.Profiling;
using XREngine.Rendering.Shaders;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            internal const int BinaryCacheSchemaVersion = 2;
            private const string BinaryCacheDirectoryName = "ShaderPrograms";
            private const string BinaryCacheRootDirectoryName = "Build";
            private const string BinaryCacheSubDirectoryName = "Cache";
            private const string BinaryCacheApiDirectoryName = "OpenGL";

            private static readonly JsonSerializerOptions BinaryCacheJsonOptions = new()
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };

            private static ConcurrentDictionary<string, BinaryProgram>? BinaryCache = null;
            private static ShaderBinaryRuntimeFingerprint RuntimeFingerprint = ShaderBinaryRuntimeFingerprint.Unknown;

            internal static string GetShaderCacheDirectoryPath()
            {
                string root = ResolveRepositoryRoot(Environment.CurrentDirectory);
                return Path.Combine(
                    root,
                    BinaryCacheRootDirectoryName,
                    BinaryCacheSubDirectoryName,
                    BinaryCacheApiDirectoryName,
                    BinaryCacheDirectoryName);
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

            private static string GetBinaryShaderCacheMetaPath(string binaryFilePath)
                => $"{binaryFilePath}.json";

            internal static void ReadBinaryShaderCache(GL api)
            {
                if (BinaryCache is not null)
                    return;

                RuntimeFingerprint = CreateRuntimeFingerprint(api);
                BinaryCache = new();

                string path = GetShaderCacheDirectoryPath();
                if (!Directory.Exists(path))
                    return;

                DeleteLegacyBinaryCacheFiles(path);

                foreach (string metaPath in Directory.EnumerateFiles(path, "*.bin.json"))
                {
                    ShaderBinaryCacheMetadata? metadata = ReadMetadata(metaPath);
                    if (metadata is null)
                    {
                        DeleteCacheFiles(metaPath);
                        continue;
                    }

                    string? binaryPath = metadata.BinaryPath;
                    if (string.IsNullOrWhiteSpace(binaryPath))
                        binaryPath = metaPath[..^5];
                    if (!Path.IsPathRooted(binaryPath))
                        binaryPath = Path.Combine(path, binaryPath);

                    if (!ValidateMetadata(metadata, binaryPath, out string failureReason))
                    {
                        Debug.OpenGLWarning($"[ShaderCache] Deleting stale binary cache entry '{Path.GetFileName(binaryPath)}': {failureReason}.");
                        DeleteCacheFiles(binaryPath);
                        continue;
                    }

                    try
                    {
                        byte[] binary = File.ReadAllBytes(binaryPath);
                        if (binary.Length == 0)
                        {
                            DeleteCacheFiles(binaryPath);
                            continue;
                        }

                        UniformMetadataEntry[] uniforms = ConvertUniformMetadata(metadata.Uniforms);
                        BinaryProgram binaryProgram = new(
                            metadata.CacheKey,
                            binary,
                            (GLEnum)metadata.BinaryFormat,
                            (uint)binary.Length,
                            uniforms.Length == 0 ? null : uniforms,
                            metadata,
                            binaryPath);

                        BinaryCache.TryAdd(metadata.CacheKey, binaryProgram);
                    }
                    catch (Exception ex)
                    {
                        Debug.OpenGLWarning($"[ShaderCache] Failed to read binary cache entry '{Path.GetFileName(binaryPath)}': {ex.Message}. Deleting entry.");
                        DeleteCacheFiles(binaryPath);
                    }
                }
            }

            private static void DeleteLegacyBinaryCacheFiles(string path)
            {
                foreach (string binaryPath in Directory.EnumerateFiles(path, "*.bin"))
                {
                    string metaPath = GetBinaryShaderCacheMetaPath(binaryPath);
                    if (File.Exists(metaPath))
                        continue;

                    try
                    {
                        File.Delete(binaryPath);
                        string legacyMetaPath = $"{binaryPath}.meta";
                        if (File.Exists(legacyMetaPath))
                            File.Delete(legacyMetaPath);
                    }
                    catch
                    {
                    }
                }
            }

            private static ShaderBinaryCacheMetadata? ReadMetadata(string metaPath)
            {
                try
                {
                    return JsonSerializer.Deserialize<ShaderBinaryCacheMetadata>(
                        File.ReadAllText(metaPath),
                        BinaryCacheJsonOptions);
                }
                catch (Exception ex)
                {
                    Debug.OpenGLWarning($"[ShaderCache] Invalid binary cache metadata '{Path.GetFileName(metaPath)}': {ex.Message}.");
                    return null;
                }
            }

            private static bool ValidateMetadata(ShaderBinaryCacheMetadata metadata, string binaryPath, out string failureReason)
            {
                if (metadata.SchemaVersion != BinaryCacheSchemaVersion)
                {
                    failureReason = $"schema {metadata.SchemaVersion} != {BinaryCacheSchemaVersion}";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(metadata.CacheKey))
                {
                    failureReason = "missing cache key";
                    return false;
                }

                if (!File.Exists(binaryPath))
                {
                    failureReason = "missing binary payload";
                    return false;
                }

                if (!metadata.RuntimeFingerprint.Equals(RuntimeFingerprint))
                {
                    failureReason = "runtime fingerprint changed";
                    return false;
                }

                failureReason = string.Empty;
                return true;
            }

            private static void QueueBinaryShaderCacheWrite(BinaryProgram binary)
            {
                ThreadPool.QueueUserWorkItem(static state =>
                {
                    try
                    {
                        WriteToBinaryShaderCache((BinaryProgram)state!);
                    }
                    catch (Exception ex)
                    {
                        Debug.OpenGLWarning($"[ShaderCache] Async program binary cache write failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }, binary);
            }

            private static void WriteToBinaryShaderCache(BinaryProgram binary)
            {
                string path = GetShaderCacheDirectoryPath();
                Directory.CreateDirectory(path);

                ShaderBinaryCacheMetadata metadata = binary.Metadata with
                {
                    SchemaVersion = BinaryCacheSchemaVersion,
                    RuntimeFingerprint = RuntimeFingerprint,
                    BinaryFormat = (int)binary.Format,
                    BinaryLength = binary.Length,
                    Uniforms = ConvertUniformMetadata(binary.Uniforms),
                };

                string fileBase = $"{metadata.CacheKey}-{((int)binary.Format):X8}";
                string binaryPath = Path.Combine(path, $"{fileBase}.bin");
                metadata = metadata with
                {
                    BinaryPath = Path.GetFileName(binaryPath),
                };

                File.WriteAllBytes(binaryPath, binary.Binary);
                File.WriteAllText(GetBinaryShaderCacheMetaPath(binaryPath), JsonSerializer.Serialize(metadata, BinaryCacheJsonOptions));
            }

            public static void DeleteFromBinaryShaderCache(string cacheKey, GLEnum? format = null)
            {
                if (string.IsNullOrWhiteSpace(cacheKey))
                    return;

                BinaryCache?.TryRemove(cacheKey, out _);

                string path = GetShaderCacheDirectoryPath();
                if (!Directory.Exists(path))
                    return;

                string pattern = format.HasValue
                    ? $"{cacheKey}-{((int)format.Value):X8}.bin"
                    : $"{cacheKey}-*.bin";

                foreach (string binaryPath in Directory.EnumerateFiles(path, pattern))
                    DeleteCacheFiles(binaryPath);
            }

            private static void DeleteCacheFiles(string path)
            {
                string binaryPath = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? path[..^5]
                    : path;
                string metaPath = GetBinaryShaderCacheMetaPath(binaryPath);

                try
                {
                    if (File.Exists(binaryPath))
                        File.Delete(binaryPath);
                    if (File.Exists(metaPath))
                        File.Delete(metaPath);

                    string legacyMetaPath = $"{binaryPath}.meta";
                    if (File.Exists(legacyMetaPath))
                        File.Delete(legacyMetaPath);
                }
                catch
                {
                }
            }

            private void CacheBinary(uint bindingId, GLProgramCompileLinkQueue.ProgramBinarySnapshot? capturedBinary = null)
            {
                if (!RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching)
                    return;

                if (ShouldBypassBinaryCacheForLiveUberVariant())
                    return;

                string cacheKey = BuildBinaryCacheKey(Hash);
                if (string.IsNullOrWhiteSpace(cacheKey))
                    return;

                if (capturedBinary is { } snapshot)
                {
                    if (snapshot.Length <= 0 || snapshot.Binary.Length == 0 || snapshot.Format == GLEnum.None)
                    {
                        Debug.OpenGLWarning($"[ShaderCache] Program {bindingId} for key {cacheKey} did not expose a retrievable worker-captured program binary. Cache write skipped.");
                        LogRenderingProgramBuildEvent(
                            "BINARY_CACHE_WRITE_SKIPPED",
                            _activeBuildBackend ?? "BinaryCache",
                            "worker-captured program binary length was zero",
                            cacheKey,
                            bindingId);
                        return;
                    }

                    StoreBinaryCacheEntry(
                        cacheKey,
                        bindingId,
                        snapshot.Binary,
                        snapshot.Format,
                        snapshot.Length,
                        "captured linked program binary on shared worker");
                    return;
                }

                int len = 0;
                MeasureRenderingProgramGlCall(
                    "glGetProgramiv(GL_PROGRAM_BINARY_LENGTH)",
                    bindingId,
                    () => Api.GetProgram(bindingId, GLEnum.ProgramBinaryLength, out len),
                    $"cacheKey={cacheKey}");
                if (len <= 0)
                {
                    Debug.OpenGLWarning($"[ShaderCache] Program {bindingId} for key {cacheKey} did not expose a retrievable program binary. Cache write skipped.");
                    LogRenderingProgramBuildEvent(
                        "BINARY_CACHE_WRITE_SKIPPED",
                        _activeBuildBackend ?? "BinaryCache",
                        "program binary length was zero",
                        cacheKey,
                        bindingId);
                    return;
                }

                byte[] binary = new byte[len];
                GLEnum format;
                uint binaryLength;
                fixed (byte* ptr = binary)
                {
                    GLEnum capturedFormat = GLEnum.None;
                    uint capturedLength = 0;
                    long binaryReadStart = Stopwatch.GetTimestamp();
                    Api.GetProgramBinary(bindingId, (uint)len, &capturedLength, &capturedFormat, ptr);
                    if (ShouldLogRenderingShaderLinkVerbose())
                    {
                        double binaryReadMilliseconds = StopwatchTicksToMilliseconds(Stopwatch.GetTimestamp() - binaryReadStart);
                        LogRenderingProgramGlCall(
                            "glGetProgramBinary",
                            bindingId,
                            binaryReadMilliseconds,
                            $"requestedBytes={len} cacheKey={cacheKey}");
                    }
                    binaryLength = capturedLength;
                    format = capturedFormat;
                }

                StoreBinaryCacheEntry(
                    cacheKey,
                    bindingId,
                    binary,
                    format,
                    binaryLength,
                    "captured linked program binary");
            }

            private void StoreBinaryCacheEntry(
                string cacheKey,
                uint bindingId,
                byte[] binary,
                GLEnum format,
                uint binaryLength,
                string reason)
            {
                LogRenderingProgramBuildEvent(
                    "BINARY_CACHE_WRITE",
                    _activeBuildBackend ?? "BinaryCache",
                    reason,
                    cacheKey,
                    bindingId,
                    binaryBytes: binaryLength,
                    binaryFormat: format.ToString());

                ShaderBinaryCacheMetadata metadata = CreateBinaryCacheMetadata(Hash, cacheKey, format, binaryLength);
                UniformMetadataEntry[] uniforms = SnapshotUniformMetadata();
                BinaryProgram bin = new(
                    cacheKey,
                    binary,
                    format,
                    binaryLength,
                    uniforms.Length == 0 ? null : uniforms,
                    metadata,
                    null);

                var binaryCache = BinaryCache;
                if (binaryCache is null)
                    return;

                binaryCache[cacheKey] = bin;
                QueueBinaryShaderCacheWrite(bin);
            }

            private ShaderBinaryCacheMetadata CreateBinaryCacheMetadata(ulong sourceHash, string cacheKey, GLEnum format, uint length)
            {
                ShaderProgramVariantMetadata variant = Data.ShaderMetadata.Variant;
                string stageTopology = GetShaderStageTopology();
                return new ShaderBinaryCacheMetadata(
                    BinaryCacheSchemaVersion,
                    cacheKey,
                    sourceHash,
                    stageTopology,
                    Data.Separable,
                    variant.Kind,
                    variant.VariantHash,
                    variant.BinaryCachePolicy.ToString(),
                    RuntimeFingerprint,
                    (int)format,
                    length,
                    null,
                    []);
            }

            private string BuildBinaryCacheKey(ulong sourceHash)
            {
                ShaderProgramVariantMetadata variant = Data.ShaderMetadata.Variant;
                ShaderBinaryCacheKey key = new(
                    BinaryCacheSchemaVersion,
                    sourceHash,
                    GetShaderStageTopology(),
                    Data.Separable,
                    variant.Kind,
                    variant.VariantHash,
                    variant.BinaryCachePolicy.ToString(),
                    RuntimeFingerprint);

                return ComputeBinaryCacheKeyHash(key);
            }

            internal static string ComputeBinaryCacheKeyHash(ShaderBinaryCacheKey key)
            {
                string stableText = string.Join(
                    "\n",
                    key.SchemaVersion,
                    key.SourceHash,
                    key.StageTopology,
                    key.Separable ? "separable" : "monolithic",
                    key.VariantKind ?? string.Empty,
                    key.VariantHash,
                    key.BinaryCachePolicy ?? string.Empty,
                    key.RuntimeFingerprint.OpenGLVersion,
                    key.RuntimeFingerprint.Vendor,
                    key.RuntimeFingerprint.Renderer,
                    key.RuntimeFingerprint.ShadingLanguageVersion);

                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableText))).ToLowerInvariant();
            }

            private string GetShaderStageTopology()
            {
                if (Data.Shaders.Count == 0)
                    return string.Empty;

                var builder = new StringBuilder(Data.Shaders.Count * 16);
                for (int index = 0; index < Data.Shaders.Count; index++)
                {
                    if (index > 0)
                        builder.Append('|');

                    XRShader shader = Data.Shaders[index];
                    builder.Append(index)
                        .Append(':')
                        .Append(shader.Type);
                }

                return builder.ToString();
            }

            /// <summary>
            /// Resolves #include directives for hashing so that included-file changes invalidate the cache.
            /// Falls back to raw source text if resolution fails (e.g., missing include file).
            /// </summary>
            private string ResolveSourceForCompilation(XRShader shader)
            {
                using var sample = RuntimeEngine.Profiler.Start("GLRenderProgram.Link.ResolveSourceForCompilation", ProfilerScopeKind.OneOffInvoke);
                if (shader is null)
                    return string.Empty;

                try
                {
                    string optimized = shader.GetOptimizedSource();
                    return GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(optimized, shader.Type, Data.Separable);
                }
                catch (Exception ex)
                {
                    Debug.OpenGLWarning($"[ShaderCache] Include resolution failed for compilation (filePath={shader.Source?.FilePath ?? "null"}): {ex.Message}. Using raw source.");
                    string rawSource = shader.Source?.Text ?? string.Empty;
                    return GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(rawSource, shader.Type, Data.Separable);
                }
            }

            private string ResolveSourceForHash(GLShader shader)
            {
                using var sample = RuntimeEngine.Profiler.Start("GLRenderProgram.Link.ResolveSourceForHash", ProfilerScopeKind.OneOffInvoke);
                if (shader is null)
                    return string.Empty;

                try
                {
                    shader.PrepareCompileVariant(Data.Separable);
                    string resolved = shader.ResolveFullSource() ?? string.Empty;
                    if (resolved.Contains("#include", StringComparison.Ordinal))
                        Debug.OpenGLWarning($"[ShaderCache] Include resolution left unresolved #include in source (filePath={shader.Data.Source?.FilePath ?? "null"})");
                    return resolved;
                }
                catch (Exception ex)
                {
                    Debug.OpenGLWarning($"[ShaderCache] Include resolution failed for hash (filePath={shader.Data.Source?.FilePath ?? "null"}): {ex.Message}. Using raw source.");
                    string rawSource = shader.Data.Source?.Text ?? string.Empty;
                    return GLShaderSourceCompatibility.InjectMissingGLPerVertexBlocks(rawSource, shader.Data.Type, Data.Separable);
                }
            }

            private ulong CalcShaderSourceHash()
            {
                ulong hash = 17ul;
                hash = AccumulateHash(hash, Data.Separable ? "separable" : "monolithic");
                hash = AccumulateHash(hash, ResolvedShaderSourceOptimizer.BuildIdentitySegment());
                foreach (XRShader shaderData in Data.Shaders)
                {
                    hash = AccumulateHash(hash, shaderData.Type.ToString());
                    string resolved = _shaderCache.TryGetValue(shaderData, out GLShader? shader) && shader is not null
                        ? ResolveSourceForHash(shader)
                        : ResolveSourceForCompilation(shaderData);
                    hash = AccumulateHash(hash, resolved);
                }

                ShaderProgramVariantMetadata variant = Data.ShaderMetadata.Variant;
                hash = AccumulateHash(hash, variant.Kind);
                hash = AccumulateHash(hash, variant.VariantHash.ToString());
                hash = AccumulateHash(hash, variant.BinaryCachePolicy.ToString());
                return hash;
            }

            private static ulong AccumulateHash(ulong hash, string? item)
                => hash * 31ul + GetDeterministicHashCode(item ?? string.Empty);

            static ulong GetDeterministicHashCode(string str)
            {
                unchecked
                {
                    ulong hash1 = (5381 << 16) + 5381;
                    ulong hash2 = hash1;

                    for (int i = 0; i < str.Length; i += 2)
                    {
                        hash1 = ((hash1 << 5) + hash1) ^ str[i];
                        if (i == str.Length - 1)
                            break;
                        hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                    }

                    return hash1 + (hash2 * 1566083941ul);
                }
            }

            private static ShaderBinaryRuntimeFingerprint CreateRuntimeFingerprint(GL api)
                => new(
                    GetGLString(api, StringName.Version),
                    GetGLString(api, StringName.Vendor),
                    GetGLString(api, StringName.Renderer),
                    GetGLString(api, StringName.ShadingLanguageVersion));

            private static string GetGLString(GL api, StringName name)
            {
                try
                {
                    byte* value = api.GetString(name);
                    return value is null ? string.Empty : new string((sbyte*)value);
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static UniformMetadataEntry[] ConvertUniformMetadata(ShaderBinaryUniformMetadata[]? metadata)
            {
                if (metadata is not { Length: > 0 })
                    return [];

                var uniforms = new UniformMetadataEntry[metadata.Length];
                int count = 0;
                foreach (ShaderBinaryUniformMetadata entry in metadata)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name))
                        continue;

                    uniforms[count++] = new UniformMetadataEntry(entry.Name, (GLEnum)entry.Type, entry.Size);
                }

                if (count == uniforms.Length)
                    return uniforms;

                Array.Resize(ref uniforms, count);
                return uniforms;
            }

            private static ShaderBinaryUniformMetadata[] ConvertUniformMetadata(UniformMetadataEntry[]? metadata)
            {
                if (metadata is not { Length: > 0 })
                    return [];

                var uniforms = new ShaderBinaryUniformMetadata[metadata.Length];
                for (int i = 0; i < metadata.Length; i++)
                    uniforms[i] = new ShaderBinaryUniformMetadata(metadata[i].Name, (int)metadata[i].Type, metadata[i].Size);
                return uniforms;
            }
        }
    }

    internal readonly record struct ShaderBinaryRuntimeFingerprint(
        string OpenGLVersion,
        string Vendor,
        string Renderer,
        string ShadingLanguageVersion)
    {
        public static ShaderBinaryRuntimeFingerprint Unknown { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    }

    internal readonly record struct ShaderBinaryCacheKey(
        int SchemaVersion,
        ulong SourceHash,
        string StageTopology,
        bool Separable,
        string? VariantKind,
        ulong VariantHash,
        string? BinaryCachePolicy,
        ShaderBinaryRuntimeFingerprint RuntimeFingerprint);

    internal sealed record ShaderBinaryUniformMetadata(string Name, int Type, int Size);

    internal sealed record ShaderBinaryCacheMetadata(
        int SchemaVersion,
        string CacheKey,
        ulong SourceHash,
        string StageTopology,
        bool Separable,
        string? VariantKind,
        ulong VariantHash,
        string BinaryCachePolicy,
        ShaderBinaryRuntimeFingerprint RuntimeFingerprint,
        int BinaryFormat,
        uint BinaryLength,
        string? BinaryPath,
        ShaderBinaryUniformMetadata[] Uniforms);

    internal record struct BinaryProgram(
        string CacheKey,
        byte[] Binary,
        GLEnum Format,
        uint Length,
        OpenGLRenderer.GLRenderProgram.UniformMetadataEntry[]? Uniforms,
        ShaderBinaryCacheMetadata Metadata,
        string? BinaryPath)
    {
        public static implicit operator (byte[] bin, GLEnum fmt, uint len)(BinaryProgram value)
            => (value.Binary, value.Format, value.Length);
    }
}
