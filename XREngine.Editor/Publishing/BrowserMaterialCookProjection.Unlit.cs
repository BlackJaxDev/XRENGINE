using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private static readonly (string File, EngineMaterialSemanticIdentity Semantic, string Sha256)[] UnlitDesktopSources =
    [
        ("UnlitColoredForward.fs", EngineMaterialSemanticIdentity.UnlitColorV1, "88f89e4058a1231b6271409848cb06b262cf408dae55364bd5a384ab91d0b840"),
        ("UnlitTexturedForward.fs", EngineMaterialSemanticIdentity.UnlitTextureV2, "ae78f19c6bf5c1c7061e043f0d5ac97bed0f72062ffb925c943432139713ff3a"),
        ("UnlitTexturedOpaqueForward.fs", EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3, "21f2c269072769ce711316071134e6de5e886746dd23a41b6d0da222fdf55964"),
        ("UnlitAlphaTexturedForward.fs", EngineMaterialSemanticIdentity.UnlitAlphaTextureV4, "203b656fdd47bb24e67c36a01c6c6c70e4da2d9597be32ce889c5429b98a0f1c"),
        ("UnlitTexturedArraySliceForward.fs", EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5, "19b9a48c3ea2df7443b30192e7b4c28c987b636f6f1966376360fced003c24de"),
    ];

    private bool IsUnlitStageCandidate(XRMaterial material)
    {
        if (_engineRoot is null || material.Shaders.Count != 1 || material.Shaders[0].Type != EShaderType.Fragment)
            return false;
        string? source = material.Shaders[0].Source?.FilePath ?? material.Shaders[0].FilePath;
        if (source is null) return false;
        string path = Path.GetFullPath(source);
        foreach (var (file, _, _) in UnlitDesktopSources)
        {
            string expected = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders", "Common", file));
            if (string.Equals(path, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private XRMaterial ProjectUnlit(XRMaterial source)
    {
        if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
        EngineMaterialSemanticIdentity semantic = source.EngineSemantic.IsUnlit()
            ? source.EngineSemantic : IdentifyUnlitDesktopStage(source);
        if (source.Shaders.Count != 0) VerifyUnlitDesktopStage(source, semantic);
        else if (!semantic.IsUnlit()) throw new NotSupportedException($"BrowserCook.UnlitStageMissing: '{source.Name}' has no canonical unlit source.");
        if (!EngineUnlitSurfaceBinding.TryReadForCook(source, semantic, EquivalentTexture,
                out EngineUnlitSurface surface, out string? reason))
            throw new NotSupportedException($"BrowserCook.UnlitSurfaceUnsupported: '{source.Name}': {reason}");
        string coverage = surface.TransparencyMode switch
        {
            ETransparencyMode.Opaque => "opaque",
            ETransparencyMode.Masked => "masked",
            ETransparencyMode.AlphaBlend => "alpha-blend",
            ETransparencyMode.PremultipliedAlpha => "premultiplied-alpha",
            ETransparencyMode.Additive => "additive",
            _ => throw new NotSupportedException($"BrowserCook.UnlitCoverageUnsupported: '{source.Name}' requests an unsupported transparency technique."),
        };
        bool builtIn = source.Shaders.Count == 0;
        ShaderProgramArtifact? artifact;
        EngineMaterialVariantKey builtInKey = default;
        if (builtIn)
        {
            builtInKey = EngineUnlitMaterialShaderGenerator.BuiltInKey(semantic);
            if (_shaderSource?.TryResolveMaterialVariant(builtInKey, out artifact) != true || artifact is null ||
                !EngineUnlitShaderProvenance.TryValidateBuiltIn(artifact, builtInKey, out reason))
                throw new NotSupportedException($"BrowserCook.UnlitBuiltInMissing: '{source.Name}' needs exact '{builtInKey}': {reason}");
        }
        else
        {
            EngineUnlitMaterialShaderPlan plan = EngineUnlitMaterialShaderGenerator.Plan(
                EngineLitMaterialShaderGenerator.CookName(source.ID), semantic, coverage, ShaderCompileTarget.WebGPUWgsl);
            if (_shaderSource is null || !_shaderSource.TryResolveUnlit(source, plan, out artifact))
                throw new NotSupportedException($"BrowserCook.UnlitCookMissing: '{source.Name}' needs exact MaterialRecipe '{plan.Name}' for '{plan.SemanticSchemaIdentity}' in BrowserShaderArtifactManifestPath.");
        }
        if (!builtIn && source.Shaders.Count == 1 && source.Shaders[0].CookedArtifactIdentity is { } previous && previous != artifact.Identity)
            throw new NotSupportedException($"BrowserCook.UnlitCompanionStale: '{source.Name}' retains a different cooked descriptor identity.");
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        PublishedUnlitMaterial copy = new();
        try
        {
            copy.AdoptPersistentID(source.ID);
            copy.Name = source.Name;
            copy.AlphaCutoff = source.AlphaCutoff;
            copy.TransparencyMode = source.TransparencyMode;
            if (surface.Texture is not null)
            {
                copy.Textures = [surface.Texture];
                MaterialSurfaceTextureBinding? binding = surface.TextureBinding;
                XRTexture2D? image = surface.Texture as XRTexture2D;
                XRTexture2D? sampledLayer = image ?? (surface.Texture as XRTexture2DArray)?.Textures[0];
                bool srgb = binding?.IsSrgb ?? (sampledLayer?.SizedInternalFormat is ESizedInternalFormat.Srgb8 or ESizedInternalFormat.Srgb8Alpha8);
                copy.SurfaceTextureBindings = [new(EMaterialTextureSemantic.BaseColor, surface.Texture,
                    IsSrgb: srgb,
                    WrapU: binding?.WrapU ?? sampledLayer?.UWrap ?? ETexWrapMode.Repeat,
                    WrapV: binding?.WrapV ?? sampledLayer?.VWrap ?? ETexWrapMode.Repeat)];
            }
            copy.RenderOptions = source.RenderOptions;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            if (!builtIn)
                copy.Shaders = [new XRShader(EShaderType.Fragment) { CookedArtifactIdentity = artifact.Identity }];
            copy.EngineSemantic = semantic;
            // Stage assignment can synchronize AlphaCutoff. Attach borrowed
            // parameters after stage and state setup to retain their raw values.
            copy.Parameters = source.Parameters;
            copy.PublishedUnlitTextureProfile = UnlitPublishedTextureProfile.Capture(surface.Texture);
            if (builtIn
                ? !EngineUnlitMaterialAdmission.TryAdmitBuiltIn(copy, artifact, builtInKey, out _, out reason)
                : !EngineUnlitMaterialAdmission.TryAdmit(copy, artifact, out _, out reason))
                throw new InvalidDataException($"BrowserCook.UnlitProjectionMismatch: '{source.Name}': {reason}");
            _copies.Add(source, copy);
            return copy;
        }
        catch
        {
            Release(copy);
            throw;
        }
    }

    private EngineMaterialSemanticIdentity IdentifyUnlitDesktopStage(XRMaterial source)
    {
        string? sourcePath = source.Shaders.Count == 1 ? source.Shaders[0].Source?.FilePath ?? source.Shaders[0].FilePath : null;
        string? name = sourcePath is null ? null : Path.GetFileName(sourcePath);
        foreach (var (file, semantic, _) in UnlitDesktopSources)
            if (file == name) return semantic;
        throw new NotSupportedException($"BrowserCook.UnlitStageUnsupported: '{source.Name}' has no canonical unlit fragment.");
    }

    private void VerifyUnlitDesktopStage(XRMaterial material, EngineMaterialSemanticIdentity semantic)
    {
        if (_engineRoot is null || material.Shaders.Count != 1)
            throw new NotSupportedException($"BrowserCook.UnlitStageUnsupported: '{material.Name}' requires one canonical GLSL fragment stage.");
        XRShader shader = material.Shaders[0];
        if (shader.Type != EShaderType.Fragment || shader.SourceLanguage != ShaderSourceLanguage.Glsl ||
            shader.EntryPoint != "main" || shader.IsGeneratedUberVariant || shader.GeneratedUberVariantHash != 0 ||
            !shader.SlangOptions.Defines.IsDefaultOrEmpty || !shader.SlangOptions.Includes.IsDefaultOrEmpty ||
            !shader.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty || !shader.SlangOptions.Resources.IsDefaultOrEmpty)
            throw new NotSupportedException($"BrowserCook.UnlitStageUnsupported: '{material.Name}' has a shader override.");
        foreach ((string file, EngineMaterialSemanticIdentity expectedSemantic, string expectedHash) in UnlitDesktopSources)
        {
            if (semantic != expectedSemantic) continue;
            string canonicalPath = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders", "Common", file));
            string? path = shader.Source?.FilePath ?? shader.FilePath;
            if (path is null || !string.Equals(Path.GetFullPath(path), canonicalPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
            if (!_canonicalSources.TryGetValue(canonicalPath, out string? canonical))
            {
                canonical = File.ReadAllText(canonicalPath);
                _canonicalSources.Add(canonicalPath, canonical);
            }
            if (EngineTexturedAlphaShaderGenerator.NormalizedHash(canonical) != expectedHash ||
                !string.Equals(shader.Source?.Text, canonical, StringComparison.Ordinal))
                break;
            // Desktop resolution writes each source line, including an
            // unterminated final line. The source bytes above still have to
            // match the pinned canonical file exactly.
            VerifySnippets(material, shader, NormalizeResolvedLines(canonical));
            return;
        }
        throw new NotSupportedException($"BrowserCook.UnlitStageUnsupported: '{material.Name}' differs from its canonical engine unlit fragment or uses an authored override.");
    }
}
