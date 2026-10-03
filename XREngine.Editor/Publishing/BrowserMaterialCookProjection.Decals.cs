using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private XRMaterial ProjectDeferredDecal(XRMaterial source)
    {
        if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
        if (source.GetType() != typeof(XRMaterial))
            throw new NotSupportedException($"BrowserCook.DeferredDecalTypeUnsupported: '{source.Name}' requires the exact engine XRMaterial type.");
        if (source.GetEffectiveTransparencyMode() == ETransparencyMode.WeightedBlendedOit)
            throw new NotSupportedException($"BrowserCook.DeferredDecalOitUnsupported: '{source.Name}' uses forward weighted OIT rather than the default deferred decal shader.");
        if (!DeferredDecalMaterialContract.TryReadShape(source, out XRTexture2D? image, out string reason) || image is null)
            throw new NotSupportedException($"BrowserCook.DeferredDecalShapeUnsupported: '{source.Name}': {reason}");
        VerifyDecalStage(source);

        // Keep the author's material, shader and image untouched. The detached
        // carrier writes one image and restores its extra settings on decode.
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        XRMaterial copy = new PublishedDeferredDecalMaterial();
        try
        {
            copy.AdoptPersistentID(source.ID);
            copy.Name = source.Name;
            copy.Textures = [null, null, null, null, image];
            copy.RenderOptions = source.RenderOptions;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            if (!DeferredDecalMaterialContract.TryRead(copy, out _, out reason))
                throw new InvalidDataException($"BrowserCook.DeferredDecalProjectionMismatch: '{source.Name}': {reason}");
            _copies.Add(source, copy);
            return copy;
        }
        catch
        {
            Release(copy);
            throw;
        }
    }

    private void VerifyDecalStage(XRMaterial material)
    {
        if (_engineRoot is null || material.Shaders.Count != 1)
            throw new NotSupportedException($"BrowserCook.DeferredDecalStageUnsupported: '{material.Name}' requires one canonical desktop deferred decal fragment.");
        XRShader shader = material.Shaders[0];
        if (shader.Type != EShaderType.Fragment || shader.SourceLanguage != ShaderSourceLanguage.Glsl ||
            shader.EntryPoint != "main" || shader.CookedArtifactIdentity is not null ||
            shader.IsGeneratedUberVariant || shader.GeneratedUberVariantHash != 0 ||
            !shader.SlangOptions.Defines.IsDefaultOrEmpty || !shader.SlangOptions.Includes.IsDefaultOrEmpty ||
            !shader.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty || !shader.SlangOptions.Resources.IsDefaultOrEmpty)
            throw new NotSupportedException($"BrowserCook.DeferredDecalStageUnsupported: '{material.Name}' has a custom shader stage or compiler extension.");
        string canonicalPath = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders", "Scene3D", "DeferredDecal.fs"));
        string? sourcePath = shader.Source?.FilePath ?? shader.FilePath;
        if (sourcePath is null || !string.Equals(Path.GetFullPath(sourcePath), canonicalPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.DeferredDecalStageUnsupported: '{material.Name}' has a noncanonical desktop decal source path.");
        if (!_canonicalSources.TryGetValue(canonicalPath, out string? canonical))
        {
            canonical = File.ReadAllText(canonicalPath);
            _canonicalSources.Add(canonicalPath, canonical);
        }
        if (!string.Equals(shader.Source?.Text, canonical, StringComparison.Ordinal))
            throw new NotSupportedException($"BrowserCook.DeferredDecalStageUnsupported: '{material.Name}' desktop GLSL differs from the engine deferred decal fragment.");
        VerifySnippets(material, shader, canonical);
    }

    private static bool IsDecalStageCandidate(XRMaterial material)
    {
        foreach (XRShader shader in material.Shaders)
        {
            string? path = shader.Source?.FilePath ?? shader.FilePath;
            if (path is null) continue;
            string file = Path.GetFileName(path);
            if (string.Equals(file, "DeferredDecal.fs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file, "DeferredDecalForwardWeightedOit.fs", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsDecalSlotCandidate(XRMaterial material)
        => material.Textures.Count == 5 && material.Textures[0] is null &&
            material.Textures[1] is null && material.Textures[2] is null &&
            material.Textures[3] is null && material.Textures[4] is XRTexture2D;
}
