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
        ("UberShader.frag", "b5cd48ef5474f1752cddfcf620a9ef3a3388a1dbbdf683251b8f1a8313b64a0a"),
        ("UberShader.vert", "e7e8e992c5d53ceae3f0e5b6277da12541c78f0e7e045c3cf3ae2827230c1f7b"),
        ("uniforms.glsl", "370ce76029c98410f815e06b7007eba1ff3d89875cee31b8e2ae270bdbf73aab"),
        ("vertex_effects.glsl", "4d615ebe2ff7234d50f1fffa73c0e3fcf496d26b0fb8c6d59094be73743bdd87"),
        ("common.glsl", "98756dfd4a3370919006101de98c1adb99efabc6aac6c126a1476bd817281446"),
        ("dissolve.glsl", "aee16f86a38bad4dbe4b555b3b3c8ef3b670319521b8ee51082f0818580e8394"),
        ("surface_extensions.glsl", "fe454d4ef65050ca90e03e7573f2d8395407d82f98ad5850452ae830c5ae7c1b"),
        ("extended_effects.glsl", "08af279fa5313cb78bf2cdb6c7480a273a4818d8408c8bb7ffb5fb0b88720b01"),
        ("source_shading.glsl", "bf2dd18b6a72694132f340a4cfb7db443389bce475c3611187e7ff74224fb251"),
        ("vertex_manipulation.glsl", "673cddc426f27cb41600e77f582bc69cb597f21a222e19c126ac380b74cb9a55"),
        ("source_dissolve.glsl", "7cc289de892eb719495c1baa49c6e3cae73eb635f555e3422e58251223696cf8"),
        ("source_glitter.glsl", "59dc797e5259e83bc89714c1f3dd05293508434f7e8112a256c52a5c6b847799"),
        ("source_flipbook.glsl", "b283a308738fb555550db767daf4f9f5eb8b7457bf1122a35f7c4760d4438b06"),
        ("procedural_eye.glsl", "42992e818a6bd699bfed345bea76813310424b097525e7355aa7df3c823221cb"),
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
