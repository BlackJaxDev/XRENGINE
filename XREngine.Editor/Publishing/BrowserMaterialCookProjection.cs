using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;
using System.Text.RegularExpressions;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Projects built-in or exact-companion authored lit textures while the browser serializer
/// walks an ordinary engine world. Authored objects remain borrowed and are never rewritten.
/// </summary>
internal sealed class BrowserMaterialCookProjection : IDisposable
{
    private readonly string? _engineRoot;
    private readonly BrowserShaderArtifactSource? _shaderSource;
    private readonly Dictionary<XRMaterial, XRMaterial> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _canonicalSources = new(StringComparer.Ordinal);
    private bool _disposed;

    public BrowserMaterialCookProjection(string? engineRoot, BrowserShaderArtifactSource? shaderSource)
    {
        _engineRoot = engineRoot;
        _shaderSource = shaderSource;
        Callbacks = new CookedBinarySerializationCallbacks { OnSerializingValue = Project };
    }

    public CookedBinarySerializationCallbacks Callbacks { get; }

    private object? Project(object? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (value is not XRMaterial source || source is PublishedStandardLitTextureMaterial)
            return value;
        if (source.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV1)
            return ProjectAuthored(source);
        if (source.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitTextureV1)
            return value;
        if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
        if (source.GetType() != typeof(XRMaterial))
            throw new NotSupportedException($"BrowserCook.TexturedProjectionUnsupported: '{source.Name}' requires the exact built-in material type.");
        StandardLitTextureSurface surface;
        string? reason;
        bool readable = StandardLitTextureSurfaceBinding.TryReadForCook(source, EquivalentTexture, out surface, out reason);
        if (!readable)
            throw new NotSupportedException($"BrowserCook.TexturedProjectionUnsupported: '{source.Name}': {reason}");
        if (source.Shaders.Count != 0) VerifyStage(source, surface);
        (XRTexture?[] textures, MaterialSurfaceTextureBinding[] bindings) = CanonicalizeTextureAliases(source);

        // Suppression keeps a second copy of the persistent ID out of the live editor
        // cache. Only this detached material is owned here; parameters and images are
        // borrowed, and their subscriptions are removed before destruction below.
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        XRMaterial copy = new PublishedStandardLitTextureMaterial();
        try
        {
            copy.AdoptPersistentID(source.ID);
            copy.Name = source.Name;
            copy.Parameters = source.Parameters;
            copy.Textures = [.. textures];
            copy.SurfaceTextureBindings = bindings;
            copy.RenderOptions = source.RenderOptions;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            copy.EngineSemantic = source.EngineSemantic;
            if (!StandardLitTextureSurfaceBinding.TryCreate(copy, out _, out reason))
                throw new InvalidDataException($"BrowserCook.TexturedProjectionMismatch: '{source.Name}': {reason}");
            _copies.Add(source, copy);
            return copy;
        }
        catch
        {
            Release(copy);
            throw;
        }
    }

    private XRMaterial ProjectAuthored(XRMaterial source)
    {
        if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
        if (source.GetType() != typeof(XRMaterial))
            throw new NotSupportedException($"BrowserCook.AuthoredLitTypeUnsupported: '{source.Name}' requires the engine XRMaterial type.");
        if (!EngineLitMaterialShaderGenerator.TryPlanForCook(source, ShaderCompileTarget.WebGPUWgsl,
            EquivalentTexture, out EngineLitMaterialShaderPlan plan, out string? reason))
            throw new NotSupportedException($"BrowserCook.AuthoredLitSurfaceUnsupported: '{source.Name}': {reason}");
        if (source.Shaders.Count != 1 || source.Shaders[0].Type != EShaderType.Fragment)
            throw new NotSupportedException($"BrowserCook.AuthoredLitStageUnsupported: '{source.Name}' requires its canonical desktop PBR fragment stage.");
        bool textured = plan.UsesBaseColorTexture;
        StandardLitTextureSurface surface = default;
        if (textured)
        {
            if (!StandardLitTextureSurfaceBinding.TryReadAuthoredForCook(source, EquivalentTexture, out surface, out reason))
                throw new NotSupportedException($"BrowserCook.AuthoredLitTextureUnsupported: '{source.Name}': {reason}");
            VerifyStage(source, surface);
        }
        else VerifyColorStage(source);
        if (_shaderSource is null || !_shaderSource.TryResolveAuthoredLit(source, plan, out ShaderProgramArtifact? artifact))
            throw new NotSupportedException($"BrowserCook.AuthoredLitCookMissing: '{source.Name}' ({source.ID}) needs schema-3 MaterialRecipe '{EngineLitMaterialShaderGenerator.CookName(source.ID)}' for '{plan.SemanticSchemaIdentity}' in BrowserShaderArtifactManifestPath.");
        string? existingIdentity = source.Shaders[0].CookedArtifactIdentity;
        if (existingIdentity is not null && existingIdentity != artifact.Identity)
            throw new NotSupportedException($"BrowserCook.AuthoredLitCompanionStale: '{source.Name}' retains '{existingIdentity}' but the selected material cook is '{artifact.Identity}'.");

        (XRTexture?[] textures, MaterialSurfaceTextureBinding[] bindings) = textured
            ? CanonicalizeTextureAliases(source) : ([], []);
        // This detached WebGPU-target copy owns its minimal exact companion; the
        // desktop material, shader source, parameters, and images remain borrowed.
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        XRMaterial copy = textured ? new PublishedStandardLitTextureMaterial() : new XRMaterial();
        try
        {
            copy.AdoptPersistentID(source.ID);
            copy.Name = source.Name;
            copy.Shaders = [new XRShader(EShaderType.Fragment) { CookedArtifactIdentity = artifact.Identity }];
            copy.Parameters = source.Parameters;
            if (textured)
            {
                copy.Textures = [.. textures];
                copy.SurfaceTextureBindings = bindings;
            }
            copy.RenderOptions = source.RenderOptions;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            copy.EngineSemantic = EngineMaterialSemanticIdentity.AuthoredLitV1;
            if (!EngineAuthoredLitMaterialAdmission.TryAdmit(copy, artifact, out _, out _, out reason))
                throw new InvalidDataException($"BrowserCook.AuthoredLitProjectionMismatch: '{source.Name}': {reason}");
            _copies.Add(source, copy);
            return copy;
        }
        catch
        {
            Release(copy);
            throw;
        }
    }

    private static (XRTexture?[] Textures, MaterialSurfaceTextureBinding[] Bindings) CanonicalizeTextureAliases(XRMaterial source)
    {
        // YAML embeds texture occurrences independently. The first legacy occurrence is
        // authoritative only after every same-ID occurrence proves full payload equality.
        // This map is local to one material; it does not intern or rewrite live editor assets.
        Dictionary<Guid, XRTexture2D> canonical = new();
        XRTexture?[] textures = new XRTexture?[source.Textures.Count];
        for (int index = 0; index < textures.Length; index++)
            textures[index] = source.Textures[index] is XRTexture2D texture ? Canonicalize(texture) : null;
        MaterialSurfaceTextureBinding[] bindings = new MaterialSurfaceTextureBinding[source.SurfaceTextureBindings.Length];
        for (int index = 0; index < bindings.Length; index++)
        {
            MaterialSurfaceTextureBinding binding = source.SurfaceTextureBindings[index];
            bindings[index] = binding with { Texture = Canonicalize((XRTexture2D)binding.Texture) };
        }
        return (textures, bindings);

        XRTexture2D Canonicalize(XRTexture2D texture)
        {
            _ = PublishedStandardLitTextureSettings.Capture(texture);
            if (texture.ID == Guid.Empty)
                throw new NotSupportedException("BrowserCook.TexturedProjectionTextureAliasMismatch: a texture has no persistent identity.");
            if (canonical.TryGetValue(texture.ID, out XRTexture2D? previous))
            {
                if (!EquivalentTexture(previous, texture))
                    throw new NotSupportedException($"BrowserCook.TexturedProjectionTextureAliasMismatch: texture '{texture.ID}' has conflicting payload or settings.");
                return previous;
            }
            canonical.Add(texture.ID, texture);
            return texture;
        }
    }

    private static bool EquivalentTexture(XRTexture2D left, XRTexture2D right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.GetType() != typeof(XRTexture2D) || right.GetType() != typeof(XRTexture2D) ||
            left.ID == Guid.Empty || left.ID != right.ID)
            return false;
        // The typed texture codec has stable field/mip ordering and raw image bytes.
        // Its omitted authored settings are compared separately and carried explicitly.
        if (PublishedStandardLitTextureSettings.Capture(left) != PublishedStandardLitTextureSettings.Capture(right))
            throw new NotSupportedException($"BrowserCook.TexturedProjectionTextureAliasMismatch: texture '{left.ID}' has conflicting sampler or import settings.");
        byte[] leftPayload = RuntimeCookedBinarySerializer.Serialize(left);
        byte[] rightPayload = RuntimeCookedBinarySerializer.Serialize(right);
        if (!leftPayload.AsSpan().SequenceEqual(rightPayload))
            throw new NotSupportedException($"BrowserCook.TexturedProjectionTextureAliasMismatch: texture '{left.ID}' has conflicting serialized metadata or image bytes.");
        return true;
    }

    private void VerifyColorStage(XRMaterial material)
    {
        if (_engineRoot is null || material.Shaders.Count != 1)
            throw new NotSupportedException($"BrowserCook.AuthoredLitColorStageUnsupported: '{material.Name}' has no canonical desktop stage.");
        XRShader shader = material.Shaders[0];
        if (shader.Type != EShaderType.Fragment || shader.SourceLanguage != ShaderSourceLanguage.Glsl ||
            shader.EntryPoint != "main" || shader.IsGeneratedUberVariant || shader.GeneratedUberVariantHash != 0 ||
            !shader.SlangOptions.Defines.IsDefaultOrEmpty || !shader.SlangOptions.Includes.IsDefaultOrEmpty ||
            !shader.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty || !shader.SlangOptions.Resources.IsDefaultOrEmpty)
            throw new NotSupportedException($"BrowserCook.AuthoredLitColorStageUnsupported: '{material.Name}' requires the canonical engine GLSL PBR fragment without extensions.");
        string path = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders", "Common", "ColoredDeferred.fs"));
        string? sourcePath = shader.Source?.FilePath ?? shader.FilePath;
        if (sourcePath is null || !string.Equals(Path.GetFullPath(sourcePath), path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.AuthoredLitColorStageUnsupported: '{material.Name}' has a noncanonical desktop GLSL source path.");
        if (!_canonicalSources.TryGetValue(path, out string? canonical))
        {
            canonical = File.ReadAllText(path);
            _canonicalSources.Add(path, canonical);
        }
        if (!string.Equals(shader.Source?.Text, canonical, StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.AuthoredLitColorStageUnsupported: '{material.Name}' desktop GLSL source differs from the engine PBR fragment.");
        VerifySnippets(material, shader, canonical);
    }

    private void VerifyStage(XRMaterial material, in StandardLitTextureSurface surface)
    {
        if (_engineRoot is null || material.Shaders.Count != 1)
            throw Unsupported(material);
        XRShader shader = material.Shaders[0];
        if (shader.Type != EShaderType.Fragment || shader.SourceLanguage != ShaderSourceLanguage.Glsl || shader.EntryPoint != "main" ||
            shader.IsGeneratedUberVariant || shader.GeneratedUberVariantHash != 0 ||
            !shader.SlangOptions.Defines.IsDefaultOrEmpty || !shader.SlangOptions.Includes.IsDefaultOrEmpty ||
            !shader.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty || !shader.SlangOptions.Resources.IsDefaultOrEmpty)
            throw Unsupported(material);
        string file = surface.Normal is not null
            ? surface.Roughness is not null ? "TexturedNormalMetallicRoughnessDeferred.fs"
                : surface.Metallic is not null ? "TexturedNormalMetallicDeferred.fs" : "TexturedNormalDeferred.fs"
            : surface.Metallic is not null && surface.Roughness is not null ? "TexturedMetallicRoughnessDeferred.fs"
                : surface.Metallic is not null ? "TexturedMetallicDeferred.fs"
                : surface.Roughness is not null ? "TexturedRoughnessDeferred.fs" : "TexturedDeferred.fs";
        string canonicalPath = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders", "Common", file));
        string? sourcePath = shader.Source?.FilePath ?? shader.FilePath;
        if (sourcePath is null || !string.Equals(Path.GetFullPath(sourcePath), canonicalPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Unsupported(material);
        if (!_canonicalSources.TryGetValue(canonicalPath, out string? canonical))
        {
            canonical = File.ReadAllText(canonicalPath);
            _canonicalSources.Add(canonicalPath, canonical);
        }
        if (!string.Equals(shader.Source?.Text, canonical, StringComparison.Ordinal))
            throw Unsupported(material);
        VerifySnippets(material, shader, canonical);
    }

    private void VerifySnippets(XRMaterial material, XRShader shader, string canonical)
    {
        if (!shader.TryGetResolvedSource(out string resolved, annotateIncludes: true, logFailures: false))
            throw Unsupported(material);
        // These exact deferred programs currently use flat engine snippets. A future
        // include or nested snippet is a contract change requiring explicit review.
        if (Regex.IsMatch(canonical, @"(?m)^\s*#\s*include\b")) throw Unsupported(material);
        Dictionary<string, string> canonicalSnippets = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(canonical, "(?m)^[ \\t]*#[ \\t]*pragma[ \\t]+snippet[ \\t]+\"(?<name>[A-Za-z][A-Za-z0-9_]*)\""))
        {
            string name = match.Groups["name"].Value;
            string path = Path.Combine(_engineRoot!, "Shaders", "Snippets", name + ".glsl");
            if (!_canonicalSources.TryGetValue(path, out string? snippet))
            {
                snippet = File.ReadAllText(path);
                _canonicalSources.Add(path, snippet);
            }
            if (Regex.IsMatch(snippet, @"(?m)^\s*#\s*(include|pragma\s+snippet)\b") ||
                !ShaderSnippets.TryGet(name, out string? active) || !string.Equals(active, snippet, StringComparison.Ordinal))
                throw new NotSupportedException($"BrowserCook.TexturedProjectionSnippetMismatch: '{material.Name}' uses a noncanonical '{name}' snippet or unresolved dependency.");
            canonicalSnippets.TryAdd(name, snippet);
        }
        string expected = ShaderSnippets.ResolveCanonical(canonical, canonicalSnippets);
        if (!string.Equals(resolved, expected, StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.TexturedProjectionResolvedSourceMismatch: '{material.Name}' does not resolve to the complete canonical engine program.");
    }

    private static NotSupportedException Unsupported(XRMaterial material)
        => new($"BrowserCook.TexturedProjectionStageMismatch: '{material.Name}' must retain its exact canonical engine fragment source, main entry point, and no extra stages or compiler extensions.");

    private static void Release(XRMaterial copy)
    {
        copy.Parameters = [];
        copy.Textures = [];
        copy.SurfaceTextureBindings = [];
        copy.Shaders.Clear();
        copy.DestroyShaderPipelineProgram();
        copy.Destroy(now: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (XRMaterial copy in _copies.Values) Release(copy);
        _copies.Clear();
        _canonicalSources.Clear();
        _disposed = true;
    }
}
