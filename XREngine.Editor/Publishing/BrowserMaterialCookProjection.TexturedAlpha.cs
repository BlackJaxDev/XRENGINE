using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private XRMaterial ProjectTexturedAlpha(XRMaterial source)
    {
        if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
        if (source.GetType() != typeof(AuthoredTexturedAlphaMaterial))
            throw new NotSupportedException($"BrowserCook.TexturedAlphaTypeUnsupported: '{source.Name}' requires the explicit authored textured-alpha material schema.");
        if (!TexturedAlphaSurfaceBinding.TryReadForCook(source, EquivalentTexture, out _, out string? reason))
            throw new NotSupportedException($"BrowserCook.TexturedAlphaSurfaceUnsupported: '{source.Name}': {reason}");
        VerifyTexturedAlphaStage(source);
        if (!EngineLitMaterialShaderGenerator.TryPlanForCook(source, ShaderCompileTarget.WebGPUWgsl,
            EquivalentTexture, out EngineLitMaterialShaderPlan plan, out reason))
            throw new NotSupportedException($"BrowserCook.TexturedAlphaSurfaceUnsupported: '{source.Name}': {reason}");
        if (_shaderSource is null || !_shaderSource.TryResolveAuthoredLit(source, plan, out ShaderProgramArtifact? artifact))
            throw new NotSupportedException($"BrowserCook.TexturedAlphaCookMissing: '{source.Name}' needs exact MaterialRecipe '{EngineLitMaterialShaderGenerator.CookName(source.ID)}' for '{plan.SemanticSchemaIdentity}' in BrowserShaderArtifactManifestPath.");
        if (source.Shaders[0].CookedArtifactIdentity is { } previous && previous != artifact.Identity)
            throw new NotSupportedException($"BrowserCook.TexturedAlphaCompanionStale: '{source.Name}' retains a different cooked descriptor identity.");
        (XRTexture?[] textures, MaterialSurfaceTextureBinding[] bindings) = CanonicalizeTextureAliases(source);
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        XRMaterial copy = new PublishedTexturedAlphaMaterial();
        try
        {
            copy.AdoptPersistentID(source.ID);
            copy.Name = source.Name;
            copy.AlphaCutoff = source.AlphaCutoff;
            copy.TransparencyMode = source.TransparencyMode;
            copy.Parameters = source.Parameters;
            copy.Textures = [.. textures];
            copy.SurfaceTextureBindings = bindings;
            copy.RenderOptions = source.RenderOptions;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            copy.Shaders = [new XRShader(EShaderType.Fragment) { CookedArtifactIdentity = artifact.Identity }];
            copy.EngineSemantic = source.EngineSemantic;
            if (!EngineTexturedAlphaMaterialAdmission.TryAdmit(copy, artifact, out _, out reason))
                throw new InvalidDataException($"BrowserCook.TexturedAlphaProjectionMismatch: '{source.Name}': {reason}");
            _copies.Add(source, copy);
            return copy;
        }
        catch
        {
            Release(copy);
            throw;
        }
    }

    private void VerifyTexturedAlphaStage(XRMaterial material)
    {
        if (_engineRoot is null || material.Shaders.Count != 1)
            throw Unsupported(material);
        XRShader shader = material.Shaders[0];
        if (shader.Type != EShaderType.Fragment || shader.SourceLanguage != ShaderSourceLanguage.Glsl ||
            shader.EntryPoint != "main" || shader.IsGeneratedUberVariant || shader.GeneratedUberVariantHash != 0 ||
            !shader.SlangOptions.Defines.IsDefaultOrEmpty || !shader.SlangOptions.Includes.IsDefaultOrEmpty ||
            !shader.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty || !shader.SlangOptions.Resources.IsDefaultOrEmpty)
            throw Unsupported(material);
        string root = Path.Combine(_engineRoot, "Shaders");
        string canonicalPath = Path.GetFullPath(Path.Combine(root, "Common", "LitTexturedAlphaForward.fs"));
        string? path = shader.Source?.FilePath ?? shader.FilePath;
        if (path is null || !string.Equals(Path.GetFullPath(path), canonicalPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Unsupported(material);
        string canonical = ReadCanonical("Common/LitTexturedAlphaForward.fs")!;
        if (!string.Equals(shader.Source?.Text, canonical, StringComparison.Ordinal) ||
            !EngineTexturedAlphaShaderGenerator.TryValidateDesktopSources(ReadCanonical, out string reason))
            throw Unsupported(material);
        Dictionary<string, string> snippets = new(StringComparer.OrdinalIgnoreCase);
        foreach (EngineLitMaterialShaderSource dependency in EngineTexturedAlphaShaderGenerator.RequiredDesktopSources)
        {
            if (!dependency.Path.StartsWith("Snippets/", StringComparison.Ordinal)) continue;
            string name = Path.GetFileNameWithoutExtension(dependency.Path);
            string expected = ReadCanonical(dependency.Path)!;
            if (!ShaderSnippets.TryGet(name, out string? active) || active != expected)
                throw new NotSupportedException($"BrowserCook.TexturedAlphaSnippetMismatch: '{material.Name}' uses a noncanonical '{name}' snippet.");
            snippets.Add(name, expected);
        }
        if (!shader.TryGetResolvedSource(out string resolved, annotateIncludes: true, logFailures: false) ||
            resolved != ShaderSnippets.ResolveCanonical(canonical, snippets))
            throw new NotSupportedException($"BrowserCook.TexturedAlphaResolvedSourceMismatch: '{material.Name}' does not resolve to the complete canonical shader graph.");

        string? ReadCanonical(string relativePath)
        {
            string fullPath = Path.Combine(root, relativePath);
            if (!_canonicalSources.TryGetValue(fullPath, out string? value))
            {
                if (!File.Exists(fullPath)) return null;
                value = File.ReadAllText(fullPath);
                _canonicalSources.Add(fullPath, value);
            }
            return value;
        }
    }
}
