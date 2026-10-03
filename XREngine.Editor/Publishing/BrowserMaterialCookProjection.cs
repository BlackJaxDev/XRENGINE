using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;
using XREngine.Rendering.UI;
using System.Numerics;
using System.Text.RegularExpressions;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Projects built-in UI images and built-in or exact-companion authored lit textures while the browser serializer
/// walks an ordinary engine world. Authored objects remain borrowed and are never rewritten.
/// </summary>
internal sealed partial class BrowserMaterialCookProjection : IDisposable
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
        if (value is not XRMaterial source || source is PublishedStandardLitTextureMaterial or PublishedUiImageMaterial or PublishedDeferredDecalMaterial)
            return value;
        try
        {
            return ProjectMaterial(source);
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or ShaderCompilationException)
        {
            error.Data["BrowserCook.Material"] = source.Name;
            if (!error.Data.Contains("BrowserCook.Pass"))
                error.Data["BrowserCook.Pass"] = source.EngineSemantic.IsAuthoredLit()
                    ? source.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2 ? "forward-coverage" : "opaque-forward"
                    : source.RenderPass.ToString(System.Globalization.CultureInfo.InvariantCulture);
            XRShader? stage = source.Shaders.FirstOrDefault();
            string? sourcePath = stage?.Source?.FilePath ?? stage?.FilePath ?? source.FilePath;
            if (!string.IsNullOrWhiteSpace(sourcePath))
                error.Data["BrowserCook.SourcePath"] = sourcePath;
            throw;
        }
    }

    private object ProjectMaterial(XRMaterial source)
    {
        if (source.RenderPass == (int)EDefaultRenderPass.DeferredDecals ||
            IsDecalStageCandidate(source) || IsDecalSlotCandidate(source))
            return ProjectDeferredDecal(source);
        if (source.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition outline) && outline.Enabled)
            return ProjectOutlineSource(source, outline);
        if (source.EngineSemantic == EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1)
            return ProjectUiImage(source);
        if (source.EngineSemantic.IsAuthoredLit())
            return ProjectAuthored(source);
        if (source.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitTextureV1)
            return source;
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

    private XRMaterial ProjectUiImage(XRMaterial source)
    {
        if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
        if (source.GetType() != typeof(XRMaterial) || source.Parameters.Length != 1 ||
            source.Parameters[0] is not ShaderVector4 { Name: "MatColor" } ||
            source.Textures.Count != 1 || source.Textures[0] is not XRTexture2D image ||
            image.GetType() != typeof(XRTexture2D))
            throw new NotSupportedException($"BrowserCook.UiImageProjectionUnsupported: '{source.Name}' requires the exact built-in image material with MatColor and one XRTexture2D.");
        if (!UIMaterialComponent.TryGetWebGpuImageProfile(image, out string? reason))
            throw new NotSupportedException($"BrowserCook.UiImageProjectionUnsupported: '{source.Name}': {reason}.");
        _ = PublishedStandardLitTextureSettings.Capture(image);
        if (source.SurfaceTextureBindings.Length > 1 ||
            source.SurfaceTextureBindings.Length == 1 &&
            (source.SurfaceTextureBindings[0] is not { Semantic: EMaterialTextureSemantic.BaseColor, Texture: XRTexture2D boundImage } binding ||
             !EquivalentTexture(image, boundImage) || binding.TexCoordSet != 0 || binding.Channel != 0 ||
             binding.UvScaleOffset != new Vector4(1, 1, 0, 0) || binding.UvRotation != 0))
            throw new NotSupportedException($"BrowserCook.UiImageBindingUnsupported: '{source.Name}' requires one canonical BaseColor image binding when metadata is present.");
        if (source.Shaders.Count != 0) VerifyImageStage(source);

        // The bounded carrier adds the existing shared settings record without
        // rewriting the borrowed image or the general texture payload. Material
        // role encoding remains separate from the image's importer metadata.
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        XRMaterial copy = new PublishedUiImageMaterial();
        try
        {
            copy.AdoptPersistentID(source.ID);
            copy.Name = source.Name;
            copy.Parameters = source.Parameters;
            copy.Textures = [image];
            copy.SurfaceTextureBindings = [new(EMaterialTextureSemantic.BaseColor, image,
                // Distinct YAML occurrences may be joined only after EquivalentTexture
                // above proved identical persistent identity, pixels and all settings.
                IsSrgb: source.SurfaceTextureBindings.Length == 1 ? source.SurfaceTextureBindings[0].IsSrgb :
                    image.ImportedColorSpace == ETextureColorSpace.Srgb,
                // UI sampling is image-owned; authored metadata may predate a
                // later sampler edit and is recanonicalized to the actual image.
                WrapU: image.UWrap, WrapV: image.VWrap)];
            copy.RenderOptions = source.RenderOptions;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            copy.EngineSemantic = source.EngineSemantic;
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
            if (plan.UsesCoverage)
            {
                // Transparency setters synchronize cutoff parameters. Establish
                // detached state before borrowing the authored parameter objects.
                copy.AlphaCutoff = source.AlphaCutoff;
                copy.TransparencyMode = source.TransparencyMode;
                copy.TransparentTechniqueOverride = source.TransparentTechniqueOverride;
            }
            copy.Parameters = source.Parameters;
            if (textured)
            {
                copy.Textures = [.. textures];
                copy.SurfaceTextureBindings = bindings;
            }
            copy.RenderOptions = source.RenderOptions;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            copy.EngineSemantic = source.EngineSemantic;
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
        string file = material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2
            ? "StandardLitColorCoverageForward.fs" : "ColoredDeferred.fs";
        string path = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders", "Common", file));
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
        VerifySnippets(material, shader, canonical,
            allowCoverageGraph: material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2);
    }

    private void VerifyImageStage(XRMaterial material)
    {
        if (_engineRoot is null || material.Shaders.Count != 1)
            throw new NotSupportedException($"BrowserCook.UiImageStageUnsupported: '{material.Name}' has no canonical desktop image stage.");
        XRShader shader = material.Shaders[0];
        if (shader.Type != EShaderType.Fragment || shader.SourceLanguage != ShaderSourceLanguage.Glsl ||
            shader.EntryPoint != "main" || shader.IsGeneratedUberVariant || shader.GeneratedUberVariantHash != 0 ||
            !shader.SlangOptions.Defines.IsDefaultOrEmpty || !shader.SlangOptions.Includes.IsDefaultOrEmpty ||
            !shader.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty || !shader.SlangOptions.Resources.IsDefaultOrEmpty)
            throw new NotSupportedException($"BrowserCook.UiImageStageUnsupported: '{material.Name}' requires the canonical engine GLSL image fragment without extensions.");
        string path = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders", "Common", "UiTexturedForward.fs"));
        string? sourcePath = shader.Source?.FilePath ?? shader.FilePath;
        if (sourcePath is null || !string.Equals(Path.GetFullPath(sourcePath), path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.UiImageStageUnsupported: '{material.Name}' has a noncanonical desktop GLSL source path.");
        if (!_canonicalSources.TryGetValue(path, out string? canonical))
        {
            canonical = File.ReadAllText(path);
            _canonicalSources.Add(path, canonical);
        }
        if (!string.Equals(shader.Source?.Text, canonical, StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.UiImageStageUnsupported: '{material.Name}' desktop GLSL source differs from the engine image fragment.");
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

    private void VerifySnippets(XRMaterial material, XRShader shader, string canonical, bool allowCoverageGraph = false)
    {
        if (!shader.TryGetResolvedSource(out string resolved, annotateIncludes: true, logFailures: false))
            throw Unsupported(material);
        // The coverage stage has one explicitly reviewed transitive graph. Other
        // canonical programs retain the original flat-snippet requirement.
        if (Regex.IsMatch(canonical, @"(?m)^\s*#\s*include\b")) throw Unsupported(material);
        HashSet<string>? coverageSnippets = allowCoverageGraph ? new(StringComparer.OrdinalIgnoreCase)
        {
            "ShadowMomentEncoding", "NormalEncoding", "ForwardLighting", "AmbientOcclusionSampling",
            "LightStructs", "LightAttenuation", "ShadowSampling"
        } : null;
        Dictionary<string, string> canonicalSnippets = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> pending = new();
        EnqueueSnippets(canonical);
        while (pending.TryDequeue(out string? name))
        {
            if (canonicalSnippets.ContainsKey(name))
                continue;
            if (canonicalSnippets.Count >= 128 || coverageSnippets is not null && !coverageSnippets.Contains(name))
                throw Unsupported(material);
            string path = Path.Combine(_engineRoot!, "Shaders", "Snippets", name + ".glsl");
            if (!_canonicalSources.TryGetValue(path, out string? snippet))
            {
                snippet = File.ReadAllText(path);
                _canonicalSources.Add(path, snippet);
            }
            if (Regex.IsMatch(snippet, allowCoverageGraph ? @"(?m)^\s*#\s*include\b" :
                    @"(?m)^\s*#\s*(include|pragma\s+snippet)\b") ||
                !ShaderSnippets.TryGet(name, out string? active) || !string.Equals(active, snippet, StringComparison.Ordinal))
                throw new NotSupportedException($"BrowserCook.TexturedProjectionSnippetMismatch: '{material.Name}' uses a noncanonical '{name}' snippet or unresolved dependency.");
            canonicalSnippets.Add(name, snippet);
            if (allowCoverageGraph)
                EnqueueSnippets(snippet);
        }
        if (coverageSnippets is not null && canonicalSnippets.Count != coverageSnippets.Count)
            throw Unsupported(material);
        string expected = ShaderSnippets.ResolveCanonical(canonical, canonicalSnippets);
        if (!string.Equals(resolved, expected, StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.TexturedProjectionResolvedSourceMismatch: '{material.Name}' does not resolve to the complete canonical engine program.");

        void EnqueueSnippets(string source)
        {
            foreach (Match match in Regex.Matches(source, "(?m)^[ \\t]*#[ \\t]*pragma[ \\t]+snippet[ \\t]+\"(?<name>[A-Za-z][A-Za-z0-9_]*)\""))
                pending.Enqueue(match.Groups["name"].Value);
        }
    }

    private static NotSupportedException Unsupported(XRMaterial material)
        => new($"BrowserCook.TexturedProjectionStageMismatch: '{material.Name}' must retain its exact canonical engine fragment source, main entry point, and no extra stages or compiler extensions.");

    private static void Release(XRMaterial copy)
    {
        copy.Parameters = [];
        // Every projection owns its detached list; images remain borrowed.
        // Replacing it here would register an unowned empty EventList at disposal.
        copy.Textures.Clear();
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
        _outlineCanonicalSources.Clear();
        _disposed = true;
    }
}
