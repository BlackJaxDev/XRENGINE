using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private readonly Dictionary<string, string> _outlineCanonicalSources = new(StringComparer.Ordinal);
    private static readonly (string File, string Hash)[] OutlineSources =
    [
        ("UberShader.frag", "ac53058b54e0f906861f24cdd48440b6d2a4d6222c8188b0ccb8d0d1eb807967"),
        ("UberShader.vert", "0a304c2b877f9011cd976081f51ae8bfaae03f325609889c1fbac14749af8ff8"),
        ("uniforms.glsl", "6a40c01dac8ad25b4a3f446e94980ed494dc84f63d9d4cd4ec0d560c438cde96"),
        ("vertex_effects.glsl", "c35bed02889627b905c7d7328f455c7960268632bfb28b7f268c0548777a2a76"),
        ("common.glsl", "98756dfd4a3370919006101de98c1adb99efabc6aac6c126a1476bd817281446"),
        ("dissolve.glsl", "dae8e92050de22e2be55e2b19b50000eb92fb8e259c0e4230bfbe9fd9d9b5d65"),
        ("surface_extensions.glsl", "f200b1689b41197f7d8648c9a9eb0691e60691da9cfe025f5807565773ac8e52"),
        ("extended_effects.glsl", "0abe0079db05c10c38a83b99cf991b1b0a673e73ed2205b3b1c9c97ea92307b4"),
    ];

    private XRMaterial ProjectOutlineSource(XRMaterial source, MaterialPassDefinition outline)
    {
        try
        {
            if (_copies.TryGetValue(source, out XRMaterial? existing)) return existing;
            if (source.GetType() != typeof(XRMaterial))
                throw OutlineUnsupported("Type", "outlined sources require the ordinary reflection material contract");
            if (!UberOutlineSurfaceBinding.TryAdmitAuthoredSource(source, out uint features, out bool renderTime, out string? reason))
                throw OutlineUnsupported("Source", reason ?? "source behavior is not modeled");
            ShaderProgramArtifact baseArtifact = ResolveOutlineBaseCompanion(source);
            if (_shaderSource is null || !_shaderSource.TryResolveMaterialVariant(UberOutlineProgramContract.Key(features), out ShaderProgramArtifact? artifact))
                throw OutlineUnsupported("CompanionMissing", "the selected manifest requires the exact canonical outline coverage recipe");
            UberOutlineProgramContract.Validate(artifact, features);
            if (source.CookedOutlineProfile is { } cooked)
            {
                if (outline.ShaderBehavior != EngineMaterialSemanticIdentity.UberOutlineV1 ||
                    outline.CookedArtifactIdentity != artifact.Identity ||
                    !UberOutlineSurfaceBinding.TryCreate(source, cooked, out _, out reason))
                    throw OutlineUnsupported("ProfileMismatch", reason ?? "the retained pass descriptor or modeled source differs from its cook");
                return source;
            }
            VerifyCanonicalOutlineSource(source);
            if (source.Textures.Count > UberOutlineMaterialProfile.MaximumSourceTextures ||
                source.Textures.Any(static texture => texture is not null && texture.GetType() != typeof(XRTexture2D)))
                throw OutlineUnsupported("TextureType", "the original source table requires at most 32 static XRTexture2D entries");
            (XRTexture?[] textures, MaterialSurfaceTextureBinding[] surfaceBindings) = CanonicalizeTextureAliases(source);
            UberOutlineTextureBinding[] complete = textures.Select((texture, slot) => (texture, slot))
                .Where(static item => item.texture is not null)
                .Select(static item => item.texture is XRTexture2D texture
                    ? new UberOutlineTextureBinding(item.slot, texture.ResolveSamplerName(item.slot), texture,
                        PublishedStandardLitTextureSettings.Capture(texture))
                    : throw OutlineUnsupported("TextureType", "all source image slots require static XRTexture2D payloads"))
                .ToArray();
            int count = 3 + ((features & UberOutlineMaterialProfile.AlphaMasks) != 0 ? 1 : 0) +
                ((features & UberOutlineMaterialProfile.Dissolve) != 0 ? 5 : 0);
            UberOutlineTextureBinding[] required = new UberOutlineTextureBinding[count];
            for (int index = 0; index < count; index++)
            {
                string name = UberOutlineMaterialProfile.RequiredSamplerName(features, index);
                UberOutlineTextureBinding? match = null;
                foreach (UberOutlineTextureBinding binding in complete)
                    if (binding.SamplerName == name)
                    {
                        if (match is not null) throw OutlineUnsupported("SamplerAmbiguous", $"sampler '{name}' occurs in multiple source slots");
                        match = binding;
                    }
                required[index] = match ?? throw OutlineUnsupported("SamplerMissing", $"sampler '{name}' has no exact authored image");
            }
            UberOutlineMaterialProfile profile = new()
            {
                Features = features, RenderTimeEnabled = renderTime,
                TextureBindings = required, SourceTextureBindings = complete,
            };
            MaterialPassDefinition[] passes = source.PassSet.Passes.Select(pass => pass.Identity == EMaterialPassIdentity.Outline
                ? pass with { ShaderBehavior = EngineMaterialSemanticIdentity.UberOutlineV1, CookedArtifactIdentity = artifact.Identity }
                : pass).ToArray();
            using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
            using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
            using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
            XRShader[] stages = source.Shaders.Select(shader => new XRShader(shader.Type) { CookedArtifactIdentity = baseArtifact.Identity }).ToArray();
            XRMaterial copy = new(textures, stages);
            try
            {
                copy.AdoptPersistentID(source.ID);
                copy.Name = source.Name;
                // Establish setters that synchronize parameter/state values before
                // borrowing any authored objects from the surrounding graph.
                copy.AlphaCutoff = source.AlphaCutoff;
                copy.TransparencyMode = source.TransparencyMode;
                copy.TransparentTechniqueOverride = source.TransparentTechniqueOverride;
                copy.BillboardMode = source.BillboardMode;
                copy.TransparentSortPriority = source.TransparentSortPriority;
                copy.RenderPass = source.RenderPass;
                copy.RenderOptions = source.RenderOptions;
                copy.Parameters = source.Parameters;
                copy.SurfaceTextureBindings = surfaceBindings;
                copy.UberAuthoredState = source.UberAuthoredState;
                copy.PassSet = source.PassSet with { Passes = passes };
                copy.CookedOutlineProfile = profile;
                copy.EngineSemantic = source.EngineSemantic;
                if (!UberOutlineSurfaceBinding.TryCreate(copy, profile, out _, out reason))
                    throw OutlineUnsupported("ProjectionMismatch", reason ?? "the detached source does not retain the admitted bindings");
                _copies.Add(source, copy);
                return copy;
            }
            catch
            {
                Release(copy);
                throw;
            }
        }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException)
        {
            error.Data["BrowserCook.Pass"] = "outline";
            throw;
        }
    }

    private ShaderProgramArtifact ResolveOutlineBaseCompanion(XRMaterial source)
    {
        if (_shaderSource is null || source.Shaders.Count is < 1 or > 2 ||
            source.Shaders.Count(shader => shader.Type == EShaderType.Fragment) != 1)
            throw OutlineUnsupported("BaseCompanionMissing", "the original base program requires its independently cooked vertex/fragment companion");
        ShaderProgramArtifact? program = null;
        foreach (XRShader stage in source.Shaders)
        {
            if (stage.Type is not (EShaderType.Vertex or EShaderType.Fragment) ||
                !stage.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, _shaderSource, out ShaderProgramArtifact? artifact) ||
                program is not null && program.Identity != artifact.Identity ||
                !WebPipelineArtifactCatalog.IsCompleteRasterProgram(artifact))
                throw OutlineUnsupported("BaseCompanionMissing", "all retained base stages must identify the same exact complete authored raster program");
            program = artifact;
        }
        return program!;
    }

    private void VerifyCanonicalOutlineSource(XRMaterial source)
    {
        if (_engineRoot is null || !source.TryGetUberMaterialState(out XRShader? canonical, out _) || canonical is null ||
            canonical.SourceLanguage != ShaderSourceLanguage.Glsl || canonical.EntryPoint != "main" ||
            !canonical.SlangOptions.Defines.IsDefaultOrEmpty || !canonical.SlangOptions.Includes.IsDefaultOrEmpty ||
            !canonical.SlangOptions.RequiredCapabilities.IsDefaultOrEmpty || !canonical.SlangOptions.Resources.IsDefaultOrEmpty)
            throw OutlineUnsupported("ShaderSource", "only the canonical modeled Uber outline source can receive this engine companion");
        string shaderRoot = Path.GetFullPath(Path.Combine(_engineRoot, "Shaders"));
        string expectedPath = Path.Combine(shaderRoot, "Uber", "UberShader.frag");
        string? sourcePath = canonical.Source?.FilePath ?? canonical.FilePath;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (sourcePath is null || !string.Equals(Path.GetFullPath(sourcePath), expectedPath, comparison))
            throw OutlineUnsupported("ShaderSource", "the canonical fragment must come from the owning engine shader root");
        foreach ((string file, string hash) in OutlineSources)
        {
            string path = Path.Combine(shaderRoot, "Uber", file);
            string text = ReadCanonicalOutlineText(path);
            if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() != hash)
                throw OutlineUnsupported("SourceVersion", $"'{file}' differs from the versioned modeled outline source");
        }
        if (canonical.Source?.Text?.ReplaceLineEndings("\n") != ReadCanonicalOutlineText(expectedPath) ||
            !canonical.TryGetResolvedShaderSource(out ResolvedShaderSource resolved, logFailures: false))
            throw OutlineUnsupported("ShaderSource", "the authored fragment does not resolve from the canonical source image");
        HashSet<string> inspected = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> dependencies = new();
        dependencies.Enqueue(expectedPath);
        foreach (string path in resolved.ResolvedPaths) dependencies.Enqueue(path);
        foreach (ShaderSourceFileDependency dependency in resolved.FileDependencies) dependencies.Enqueue(dependency.Path);
        while (dependencies.TryDequeue(out string? path))
        {
            path = Path.GetFullPath(path);
            if (!path.StartsWith(shaderRoot + Path.DirectorySeparatorChar, comparison))
                throw OutlineUnsupported("SourceDependency", "the resolved outline graph includes a source outside its canonical engine root");
            if (!inspected.Add(path)) continue;
            string text = ReadCanonicalOutlineText(path);
            foreach (Match match in Regex.Matches(text, "(?m)^[ \\t]*#[ \\t]*pragma[ \\t]+snippet[ \\t]+\"(?<name>[A-Za-z][A-Za-z0-9_]*)\""))
            {
                string name = match.Groups["name"].Value;
                string snippetPath = Path.Combine(shaderRoot, "Snippets", name + ".glsl");
                string snippet = ReadCanonicalOutlineText(snippetPath);
                if (ShaderSnippets.TryGet(name, out string? active) && active?.ReplaceLineEndings("\n") != snippet)
                    throw OutlineUnsupported("SourceDependency", $"snippet '{name}' differs from the owning canonical engine source");
                dependencies.Enqueue(snippetPath);
            }
        }
    }

    private string ReadCanonicalOutlineText(string path)
    {
        if (!_outlineCanonicalSources.TryGetValue(path, out string? source))
        {
            source = File.ReadAllText(path).ReplaceLineEndings("\n");
            _outlineCanonicalSources.Add(path, source);
        }
        return source;
    }

    private static NotSupportedException OutlineUnsupported(string operation, string reason)
        => new($"BrowserCook.UberOutline{operation}: {reason}.");
}
