using System.Reflection;
using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;
using YamlDotNet.Serialization;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private XRMaterial ProjectUberBase(XRMaterial source)
    {
        if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
        UberBaseMaterialProfile prepared = UberBaseSourcePreparation.Prepare(source);
        VerifyCanonicalUberBase(source, prepared);
        if (_shaderSource is null || !_shaderSource.TryResolveUberBase(source.ID, prepared, EngineUberBaseShaderContract.Pass, out ShaderProgramArtifact? receiver))
            throw new NotSupportedException($"BrowserCook.UberBaseArtifactMissing: material '{source.Name}', pass '{EngineUberBaseShaderContract.Pass}', canonical source '{prepared.SourceIdentity}', variant '{prepared.Variant.VariantHash:x16}' requires exact offline cook '{EngineUberBaseShaderContract.CookName(source.ID, prepared.Variant.VariantHash, EngineUberBaseShaderContract.Pass)}'.");
        List<UberBasePassArtifact> passes = [];
        foreach (string pass in new[] { "depth-normal", "depth", "point-shadow-depth", "spot-shadow-depth", EngineUberBaseShaderContract.OrderPass })
        {
            if (!_shaderSource.TryResolveUberBase(source.ID, prepared, pass, out ShaderProgramArtifact? artifact))
                throw new NotSupportedException($"BrowserCook.UberBaseAuxiliaryMissing: material '{source.Name}', prepared variant '{prepared.Variant.VariantHash:x16}', pass '{pass}' requires its exact canonical coverage companion.");
            passes.Add(new() { Pass = pass, ArtifactIdentity = artifact.Identity });
        }
        (XRTexture?[] textures, MaterialSurfaceTextureBinding[] surfaceBindings) = CanonicalizeTextureAliases(source);
        List<UberBaseTextureBinding> complete = [];
        for (int slot = 0; slot < textures.Length; slot++)
        {
            if (textures[slot] is null) continue;
            if (textures[slot]?.GetType() != typeof(XRTexture2D))
                throw new NotSupportedException($"BrowserCook.UberBaseTextureUnsupported: material '{source.Name}' source slot {slot} requires a static XRTexture2D.");
            XRTexture2D texture = (XRTexture2D)textures[slot]!;
            complete.Add(new() { SourceTextureSlot = slot, SamplerName = texture.ResolveSamplerName(slot), Texture = texture,
                Settings = PublishedStandardLitTextureSettings.Capture(texture) });
        }
        UberBaseTextureBinding?[] roles = new UberBaseTextureBinding?[UberBaseMaterialProfile.RoleCount];
        for (int role = 0; role < roles.Length; role++)
        {
            if (!UberBaseMaterialProfile.IsRoleActive(role, prepared.Features)) continue;
            string name = UberBaseMaterialProfile.SamplerName(role);
            foreach (UberBaseTextureBinding binding in complete)
                if (binding.SamplerName == name)
                {
                    if (roles[role] is not null) throw new NotSupportedException($"BrowserCook.UberBaseSamplerAmbiguous: '{source.Name}' has multiple original slots for '{name}'.");
                    roles[role] = binding;
                }
            if (roles[role] is null) throw new NotSupportedException($"BrowserCook.UberBaseSamplerMissing: '{source.Name}' has no exact original image for '{name}'.");
        }
        UberBaseMaterialProfile profile = prepared with
        {
            CookedArtifactIdentity = receiver.Identity, PassArtifacts = [.. passes],
            TextureBindings = roles, SourceTextureBindings = [.. complete],
        };
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        PublishedUberBaseMaterial copy = new();
        try
        {
            CopyUberBasePersistentState(source, copy);
            copy.AdoptPersistentID(source.ID);
            copy.RenderOptions = source.RenderOptions;
            copy.Parameters = source.Parameters;
            copy.Textures = [.. textures];
            copy.SurfaceTextureBindings = surfaceBindings;
            copy.UberAuthoredState = source.UberAuthoredState;
            copy.SetRequestedUberVariant(profile.Variant);
            copy.SetActiveUberVariant(null);
            copy.Shaders = [new XRShader(EShaderType.Vertex) { CookedArtifactIdentity = receiver.Identity },
                new XRShader(EShaderType.Fragment) { CookedArtifactIdentity = receiver.Identity }];
            copy.EngineSemantic = EngineMaterialSemanticIdentity.UberBaseV1;
            copy.PublishedUberBaseProfile = profile;
            if (!EngineUberBaseMaterialAdmission.TryAdmit(copy, receiver, out _, out string? reason))
                throw new InvalidDataException($"BrowserCook.UberBaseProjectionMismatch: '{source.Name}': {reason}");
            _copies.Add(source, copy);
            return copy;
        }
        catch
        {
            Release(copy);
            throw;
        }
    }

    private static void CopyUberBasePersistentState(XRMaterial source, PublishedUberBaseMaterial copy)
    {
        // Mirror the existing reflection persistence membership. Scalars are set
        // before references, and source parameters/images/stages are attached by
        // the caller last so synchronizing setters cannot edit borrowed objects.
        PropertyInfo[] properties = typeof(XRMaterial).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int referencePass = 0; referencePass < 2; referencePass++)
            foreach (PropertyInfo property in properties)
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 ||
                    property.GetCustomAttribute<YamlIgnoreAttribute>() is not null ||
                    property.GetCustomAttribute<RuntimeOnlyAttribute>() is not null ||
                    property.PropertyType.GetCustomAttribute<RuntimeOnlyAttribute>(inherit: true) is not null ||
                    property.GetCustomAttribute<MemoryPackIgnoreAttribute>() is not null ||
                    property.Name is nameof(XRMaterial.Parameters) or nameof(XRMaterial.Textures) or nameof(XRMaterial.Shaders) or
                        nameof(XRMaterial.SurfaceTextureBindings) or nameof(XRMaterial.RenderOptions) or nameof(XRMaterial.UberAuthoredState) or
                        nameof(XRMaterial.RequestedUberVariant) or nameof(XRMaterial.ActiveUberVariant) or nameof(XRMaterial.EngineSemantic)) continue;
                bool reference = !property.PropertyType.IsValueType && property.PropertyType != typeof(string);
                if (reference != (referencePass == 1)) continue;
                property.SetValue(copy, property.GetValue(source));
            }
    }

    private void VerifyCanonicalUberBase(XRMaterial source, UberBaseMaterialProfile profile)
    {
        if (_engineRoot is null) throw new NotSupportedException("BrowserCook.UberBaseSourceRootMissing: the owning canonical engine shader root is required.");
        string root = Path.Combine(_engineRoot, "Shaders");
        foreach (EngineLitMaterialShaderSource canonical in EngineUberBaseShaderContract.RequiredDesktopSources)
        {
            string path = Path.Combine(root, canonical.Path);
            string text = File.ReadAllText(path);
            if (EngineUberBaseShaderContract.NormalizedHash(text) != canonical.Sha256)
                throw new NotSupportedException($"BrowserCook.UberBaseSourceChanged: '{source.Name}' canonical source '{canonical.Path}' differs from its qualified counterpart.");
            if (canonical.Path.StartsWith("Snippets/", StringComparison.Ordinal) &&
                ShaderSnippets.TryGet(Path.GetFileNameWithoutExtension(path), out string? active) && active?.ReplaceLineEndings("\n") != text.ReplaceLineEndings("\n"))
                throw new NotSupportedException($"BrowserCook.UberBaseSnippetChanged: '{source.Name}' resolves a modified '{canonical.Path}' snippet.");
        }
        foreach (XRShader shader in profile.SourceShaders)
        {
            string relative = shader.Type == EShaderType.Vertex ? "Uber/UberShader.vert" : "Uber/UberShader.frag";
            string expected = Path.GetFullPath(Path.Combine(root, relative));
            string? path = shader.Source?.FilePath ?? shader.FilePath;
            if (path is null || !string.Equals(Path.GetFullPath(path), expected,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                shader.SourceLanguage != ShaderSourceLanguage.Glsl || shader.EntryPoint != "main" ||
                !shader.SlangOptions.Defines.IsDefaultOrEmpty || !shader.SlangOptions.Includes.IsDefaultOrEmpty ||
                !shader.SlangOptions.Resources.IsDefaultOrEmpty || !shader.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty)
                throw new NotSupportedException($"BrowserCook.UberBaseStageUnsupported: '{source.Name}' requires the unchanged explicit canonical mono stage '{relative}'.");
        }
    }
}
