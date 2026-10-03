using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using XREngine.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Editor.Publishing;

/// <summary>Resolves authored descriptor identities from an explicitly selected offline cooker manifest.</summary>
internal sealed class BrowserShaderArtifactSource : IShaderProgramArtifactResolver
{
    private readonly string _directory;
    private readonly Dictionary<string, string> _descriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShaderProgramArtifact> _loaded = new(StringComparer.Ordinal);
    private readonly List<EngineMaterialVariantEntry> _materialVariants = [];
    private readonly Dictionary<string, string> _pipelineArtifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _computeArtifacts = new(StringComparer.Ordinal);
    private long _loadedBytes;

    internal IReadOnlyList<EngineMaterialVariantEntry> MaterialVariants => _materialVariants.AsReadOnly();
    internal IReadOnlyDictionary<string, string> PipelineArtifacts => _pipelineArtifacts;
    internal IReadOnlyDictionary<string, string> ComputeArtifacts => _computeArtifacts;
    internal WebPipelineArtifactCatalog PipelineCatalog { get; }
    internal string? TonemapDescriptorIdentity => _pipelineArtifacts.GetValueOrDefault("tonemap");

    internal BrowserShaderArtifactSource(string projectDirectory, string manifestPath)
    {
        if (Path.IsPathRooted(manifestPath) || Uri.TryCreate(manifestPath, UriKind.Absolute, out _))
            throw new InvalidDataException("Browser shader manifest must be project-relative.");
        string manifest = Path.GetFullPath(Path.Combine(projectDirectory, manifestPath));
        string relative = Path.GetRelativePath(projectDirectory, manifest);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Browser shader manifest escapes the authored project.");
        _directory = Path.GetDirectoryName(manifest)!;
        using JsonDocument document = JsonDocument.Parse(ReadBounded(manifest, 1024 * 1024));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 3 || root.GetProperty("backend").GetString() != "WebGPU")
            throw new InvalidDataException("Browser shader publish requires the engine schema-three WebGPU cooker manifest.");
        JsonElement artifacts = root.GetProperty("artifacts");
        if (artifacts.GetArrayLength() > 256)
            throw new InvalidDataException("Browser shader catalog exceeds 256 artifacts.");
        foreach (JsonElement entry in artifacts.EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Browser shader artifact has no name.");
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(entry.GetProperty("sha256").GetString())
                ?? throw new InvalidDataException("Browser shader artifact has no identity.");
            string descriptor = entry.GetProperty("descriptor").GetString()!;
            if (descriptor != identity + ".shader.json" || !_descriptors.TryAdd(identity, descriptor) ||
                !_names.TryAdd(name, identity))
                throw new InvalidDataException("Browser shader manifest has an invalid or duplicate descriptor identity or name.");
        }
        if (root.TryGetProperty("materialVariants", out JsonElement variants))
        {
            if (variants.ValueKind != JsonValueKind.Array || variants.GetArrayLength() > 256)
                throw new InvalidDataException("Browser material variant catalog exceeds 256 entries.");
            HashSet<EngineMaterialVariantKey> keys = [];
            foreach (JsonElement variant in variants.EnumerateArray())
            {
                EngineMaterialVariantKey key = ShaderProgramArtifactReader.ReadMaterialVariantReferenceKey(variant);
                string identity = ShaderProgramArtifactCatalog.ValidateIdentity(variant.GetProperty("descriptorIdentity").GetString())
                    ?? throw new InvalidDataException("Browser material variant has no descriptor identity.");
                if (!keys.Add(key))
                    throw new InvalidDataException($"MaterialVariant.DuplicateKey: '{key}'.");
                if (!TryResolve(identity, key.Target, out ShaderProgramArtifact? artifact))
                    throw new InvalidDataException($"MaterialVariant.ArtifactMissing: '{identity}'.");
                if (!string.Equals(artifact.Pass, key.Pass, StringComparison.Ordinal))
                    throw new InvalidDataException($"MaterialVariant.PassMismatch: '{identity}'.");
                using JsonDocument descriptor = JsonDocument.Parse(artifact.DescriptorBytes.ToArray());
                if (!descriptor.RootElement.TryGetProperty("materialVariant", out JsonElement declaration)
                    || ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, artifact.Pass, artifact.Target) != key)
                    throw new InvalidDataException($"MaterialVariant.DescriptorMismatch: '{identity}'.");
                _materialVariants.Add(new EngineMaterialVariantEntry(key, identity));
            }
        }
        if (root.TryGetProperty("pipelineArtifacts", out JsonElement pipelines))
        {
            if (pipelines.ValueKind != JsonValueKind.Array ||
                pipelines.GetArrayLength() > WebPipelineArtifactCatalog.MaximumEntries)
                throw new InvalidDataException("Browser pipeline artifact catalog exceeds its limit.");
            foreach (JsonElement pipeline in pipelines.EnumerateArray())
            {
                if (pipeline.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Browser pipeline artifact must declare a pass and descriptor identity.");
                bool hasScope = pipeline.TryGetProperty("scope", out JsonElement scopeValue);
                if (pipeline.EnumerateObject().Count() != (hasScope ? 3 : 2) ||
                    hasScope && scopeValue.ValueKind != JsonValueKind.String ||
                    !pipeline.TryGetProperty("pass", out JsonElement passValue) || passValue.ValueKind != JsonValueKind.String ||
                    !pipeline.TryGetProperty("descriptorIdentity", out JsonElement identityValue) || identityValue.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Browser pipeline artifact must declare a pass, descriptor identity, and optional scope.");
                string pass = passValue.GetString()!;
                string bindingKey = WebPipelineArtifactCatalog.GetBindingKey(hasScope ? scopeValue.GetString() : null, pass);
                string identity = ShaderProgramArtifactCatalog.ValidateIdentity(
                    identityValue.GetString())
                    ?? throw new InvalidDataException($"Browser pipeline artifact '{bindingKey}' has no descriptor identity.");
                if (!TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
                    throw new InvalidDataException($"Browser pipeline artifact '{bindingKey}' has no verified WebGPU program.");
                WebPipelineArtifactCatalog.ValidateProgram(bindingKey, artifact);
                if (!_pipelineArtifacts.TryAdd(bindingKey, identity))
                    throw new InvalidDataException($"Browser pipeline artifact binding '{bindingKey}' is duplicated.");
            }
        }
        if (root.TryGetProperty("computeArtifacts", out JsonElement computes))
        {
            // The shared catalog determines the supported kernel set. Keep a
            // separate input bound without baking one release's kernel count
            // into the offline publisher.
            if (computes.ValueKind != JsonValueKind.Array || computes.GetArrayLength() > 16)
                throw new InvalidDataException("Browser compute artifact catalog exceeds 16 entries.");
            foreach (JsonElement compute in computes.EnumerateArray())
            {
                if (compute.ValueKind != JsonValueKind.Object || compute.EnumerateObject().Count() != 2 ||
                    !compute.TryGetProperty("kernel", out JsonElement kernelValue) || kernelValue.ValueKind != JsonValueKind.String ||
                    !WebComputeArtifactCatalog.IsSupportedKernel(kernelValue.GetString()) ||
                    !compute.TryGetProperty("descriptorIdentity", out JsonElement identityValue) || identityValue.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Browser compute artifact must declare a known kernel and descriptor identity.");
                string kernel = kernelValue.GetString()!;
                string identity = ShaderProgramArtifactCatalog.ValidateIdentity(identityValue.GetString())
                    ?? throw new InvalidDataException($"Browser compute artifact '{kernel}' has no descriptor identity.");
                if (!TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
                    throw new InvalidDataException($"Browser compute artifact '{kernel}' is unavailable.");
                WebComputeArtifactCatalog.ValidateKernel(kernel, artifact);
                if (!_computeArtifacts.TryAdd(kernel, identity))
                    throw new InvalidDataException($"Browser compute artifact kernel '{kernel}' is duplicated.");
            }
        }
        // Apply the runtime catalog's complete descriptor contract while all pass
        // identities are still attributable to the authored publish manifest.
        PipelineCatalog = new WebPipelineArtifactCatalog(_pipelineArtifacts,
            new ShaderProgramArtifactCatalog(_loaded.Values));
    }

    public bool TryResolve(string identity, ShaderCompileTarget target, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        artifact = null;
        if (target != ShaderCompileTarget.WebGPUWgsl || !_descriptors.TryGetValue(identity, out string? descriptorName))
            return false;
        if (_loaded.TryGetValue(identity, out artifact)) return true;
        byte[] descriptor = ReadBounded(Path.Combine(_directory, descriptorName), 1024 * 1024);
        using JsonDocument document = JsonDocument.Parse(descriptor);
        string sourceName = document.RootElement.GetProperty("source").GetProperty("url").GetString()!;
        string sourceHash = Path.GetFileNameWithoutExtension(sourceName);
        ShaderProgramArtifactCatalog.ValidateIdentity(sourceHash);
        if (sourceName != sourceHash + ".wgsl")
            throw new InvalidDataException("Browser shader source URL must be a local content-addressed WGSL filename.");
        byte[] source = ReadBounded(Path.Combine(_directory, sourceName), 4 * 1024 * 1024);
        _loadedBytes += descriptor.Length + source.Length;
        if (_loadedBytes > 64L * 1024 * 1024)
            throw new InvalidDataException("Browser shader catalog exceeds 64 MiB.");
        artifact = ShaderProgramArtifactReader.Read(descriptor, source);
        if (artifact.Identity != identity)
            throw new InvalidDataException($"Browser shader descriptor hash mismatch for '{identity}'.");
        _loaded.Add(identity, artifact);
        return true;
    }

    /// <summary>Finds the explicit per-material cook, then verifies its modeled surface profile.</summary>
    internal bool TryResolveMaterialVariant(EngineMaterialVariantKey key,
        [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        key.Validate();
        foreach (EngineMaterialVariantEntry entry in _materialVariants)
            if (entry.Key == key)
                return TryResolve(entry.DescriptorIdentity, key.Target, out artifact);
        artifact = null;
        return false;
    }

    /// <summary>Finds the explicit per-material cook, then verifies its modeled surface profile.</summary>
    internal bool TryResolveAuthoredLit(XRMaterial material, EngineLitMaterialShaderPlan plan,
        [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
    {
        artifact = null;
        string name = EngineLitMaterialShaderGenerator.CookName(material.ID);
        return _names.TryGetValue(name, out string? identity) &&
            TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out artifact) &&
            artifact.Name == name && artifact.SourceLanguage == "MaterialRecipe" &&
            artifact.SemanticSchemaIdentity == plan.SemanticSchemaIdentity &&
            artifact.Pass == plan.Pass;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        for (string? parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            FileInfo info = new(parent);
            if (info.LinkTarget is not null)
                throw new InvalidDataException("Browser shader artifact paths cannot contain symbolic links.");
        }
        using FileStream stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > maximum)
            throw new InvalidDataException($"Browser shader payload '{Path.GetFileName(path)}' exceeds its size bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Browser shader payload changed during reading.");
        return bytes;
    }
}
