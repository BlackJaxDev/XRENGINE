using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XREngine.Publishing;

public static partial class BrowserContentPackageBuilder
{
    /// <summary>Packages ordinary cooked engine assets without converting worlds into a browser scene model.</summary>
    private static void BuildEngineAssets(JsonElement recipe, string recipeDirectory, string outputDirectory,
        CancellationToken cancellationToken)
    {
        MembersOptional(recipe, ["schema", "format", "startupWorld", "assets"], ["startupSettings", "publishedMetadata", "defaultUiFont", "shaderArtifacts", "materialVariants", "pipelineArtifacts", "computeArtifacts", "essentialRoots", "streamedRoots"]);
        Require(Integer(recipe.GetProperty("schema"), 1, 1) == 1, "Unsupported engine asset schema.");
        Require(recipe.GetProperty("format").GetString() == "xrengine-assets", "Unsupported engine asset format.");
        string startupWorld = EngineAssetPath(recipe.GetProperty("startupWorld"));
        JsonElement[] assets = Array(recipe, "assets", 4096, 1);
        Dictionary<string, JsonElement> byPath = new(StringComparer.Ordinal);
        Dictionary<string, string[]> dependencies = new(StringComparer.Ordinal);
        HashSet<string> foldedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement asset in assets)
        {
            Members(asset, "path", "type", "encoding", "source", "dependencies");
            string path = EngineAssetPath(asset.GetProperty("path"));
            Require(byPath.TryAdd(path, asset) && foldedPaths.Add(path), $"Duplicate or case-colliding engine asset '{path}'.");
            string? type = asset.GetProperty("type").GetString();
            Require(!string.IsNullOrWhiteSpace(type) && type.Length <= 1024, $"Invalid type for '{path}'.");
            Choice(asset, "encoding", "cooked-binary", "yaml", "utf8-text");
            string[] paths = [.. Array(asset, "dependencies", 64).Select(EngineAssetPath)];
            Require(paths.Distinct(StringComparer.Ordinal).Count() == paths.Length, $"Duplicate dependencies for '{path}'.");
            dependencies.Add(path, paths);
        }
        string? startupSettings = recipe.TryGetProperty("startupSettings", out JsonElement settings) ? EngineAssetPath(settings) : null;
        Require(startupSettings is null || byPath.ContainsKey(startupSettings), "Startup settings are absent from the asset catalog.");
        Require(byPath.ContainsKey(startupWorld), "Startup world is absent from the engine asset catalog.");
        string? publishedMetadata = recipe.TryGetProperty("publishedMetadata", out JsonElement metadataValue)
            ? EngineAssetPath(metadataValue) : null;
        if (publishedMetadata is not null)
        {
            Require(publishedMetadata == "/engine/Metadata/AotRuntimeMetadata.bin" && byPath.ContainsKey(publishedMetadata),
                "Published runtime metadata is absent from the engine catalog.");
            JsonElement metadataAsset = byPath[publishedMetadata];
            Require(metadataAsset.GetProperty("encoding").GetString() == "cooked-binary"
                && (metadataAsset.GetProperty("type").GetString() == "XREngine.AotRuntimeMetadata, XREngine.Data"
                    || metadataAsset.GetProperty("type").GetString()?.StartsWith(
                        "XREngine.AotRuntimeMetadata, XREngine.Data,", StringComparison.Ordinal) == true)
                && dependencies[publishedMetadata].Length == 0,
                "Published runtime metadata must be a standalone AotRuntimeMetadata payload.");
        }
        string? defaultUiFont = recipe.TryGetProperty("defaultUiFont", out JsonElement fontValue) ? EngineAssetPath(fontValue) : null;
        if (defaultUiFont is not null)
        {
            Require(defaultUiFont.StartsWith("/engine/Fonts/", StringComparison.Ordinal)
                && byPath.ContainsKey(defaultUiFont),
                "Default UI font is absent from the engine font catalog.");
            JsonElement fontAsset = byPath[defaultUiFont];
            Require(fontAsset.GetProperty("encoding").GetString() == "cooked-binary"
                && fontAsset.GetProperty("type").GetString()?.StartsWith(
                    "XREngine.Rendering.FontGlyphSet, XREngine.Runtime.Rendering,", StringComparison.Ordinal) == true
                && dependencies[defaultUiFont].Length == 0,
                "Default UI font must be a standalone cooked FontGlyphSet.");
        }
        Dictionary<string, int> heights = new(StringComparer.Ordinal);
        foreach (string path in byPath.Keys)
            ValidateGraph(path, byPath, dependencies, new HashSet<string>(StringComparer.Ordinal), heights, 0);

        bool hasEssentialRoots = recipe.TryGetProperty("essentialRoots", out _);
        bool hasStreamedRoots = recipe.TryGetProperty("streamedRoots", out _);
        Require(hasEssentialRoots == hasStreamedRoots, "Engine delivery roots must declare both essentialRoots and streamedRoots.");
        string[]? essentialRoots = hasEssentialRoots ? EngineAssetRoots(recipe, "essentialRoots", byPath, 1) : null;
        string[]? streamedRoots = hasStreamedRoots ? EngineAssetRoots(recipe, "streamedRoots", byPath, 0) : null;
        HashSet<string>? essentialClosure = null;
        if (essentialRoots is not null)
        {
            HashSet<string> essential = EngineAssetClosure(essentialRoots, dependencies);
            essentialClosure = essential;
            HashSet<string> covered = EngineAssetClosure(streamedRoots!, dependencies);
            covered.UnionWith(essential);
            Require(essential.Contains(startupWorld)
                && (startupSettings is null || essential.Contains(startupSettings))
                && (publishedMetadata is null || essential.Contains(publishedMetadata))
                && (defaultUiFont is null || essential.Contains(defaultUiFont)),
                "Engine startup world, settings, metadata, and default font must be essential.");
            Require(streamedRoots!.All(path => !essential.Contains(path)
                && byPath[path].GetProperty("encoding").GetString() == "cooked-binary"
                && path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)),
                "A streamed scene root must be a separate cooked asset outside the essential closure.");
            Require(covered.SetEquals(byPath.Keys), "Engine delivery roots must cover every declared asset.");
        }

        Dictionary<string, byte[]> payloads = new(StringComparer.Ordinal);
        List<object> cookedAssets = [];
        long totalBytes = 0;
        foreach ((string path, JsonElement asset) in byPath.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = SourcePath(recipeDirectory, asset.GetProperty("source").GetString()!);
            byte[] bytes = ReadBounded(source, RawLimit);
            Require(bytes.Length != 0, $"Engine asset '{path}' is empty.");
            totalBytes += bytes.Length;
            Require(totalBytes <= AggregateLimit, "Engine content exceeds 64 MiB; split optional content into streamed catalogs.");
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            payloads.TryAdd(hash, bytes);
            cookedAssets.Add(new
            {
                path,
                type = asset.GetProperty("type").GetString(),
                encoding = asset.GetProperty("encoding").GetString(),
                bytes = bytes.Length,
                hash,
                url = $"payload/{hash}.bin",
                dependencies = dependencies[path]
            });
        }
        List<object> shaderArtifacts = [];
        Dictionary<string, JsonElement> shaderDescriptors = new(StringComparer.Ordinal);
        if (recipe.TryGetProperty("shaderArtifacts", out JsonElement shaderValues))
        {
            Require(shaderValues.ValueKind == JsonValueKind.Array && shaderValues.GetArrayLength() <= 256, "Shader artifact catalog exceeds its limit.");
            HashSet<string> identities = new(StringComparer.Ordinal);
            foreach (JsonElement shader in shaderValues.EnumerateArray())
            {
                Members(shader, "identity", "descriptor", "source");
                string? identity = shader.GetProperty("identity").GetString();
                Require(identity is not null && Regex.IsMatch(identity, "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)
                    && identities.Add(identity), "Invalid or duplicate shader artifact identity.");
                string descriptor = EngineAssetPath(shader.GetProperty("descriptor"));
                string source = EngineAssetPath(shader.GetProperty("source"));
                Require(byPath.ContainsKey(descriptor) && byPath.ContainsKey(source), "Shader artifact payload is absent from the catalog.");
                Require(essentialClosure is null || essentialClosure.Contains(descriptor) && essentialClosure.Contains(source),
                    "Shader descriptor and source assets must be essential.");
                byte[] descriptorBytes = ReadBounded(SourcePath(recipeDirectory, byPath[descriptor].GetProperty("source").GetString()!), JsonLimit);
                Require(Convert.ToHexStringLower(SHA256.HashData(descriptorBytes)) == identity, "Shader descriptor identity mismatch.");
                using JsonDocument descriptorDocument = ReadJson(descriptorBytes);
                shaderDescriptors.Add(identity!, descriptorDocument.RootElement.Clone());
                shaderArtifacts.Add(new { identity, descriptor, source });
            }
        }
        List<object> materialVariants = [];
        if (recipe.TryGetProperty("materialVariants", out JsonElement variantValues))
        {
            Require(variantValues.ValueKind == JsonValueKind.Array && variantValues.GetArrayLength() <= 256,
                "Material variant catalog exceeds its limit.");
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonElement variant in variantValues.EnumerateArray())
            {
                Members(variant, "semantic", "semanticVersion", "target", "pass", "vertexProfile", "outputProfile", "descriptorIdentity");
                string semantic = Choice(variant, "semantic", "StandardLitColor", "StandardLitTexture", "OpaqueShadowDepth",
                    "DebugPoint", "DebugLine", "DebugTriangle", "UIQuadBatched", "UIQuadBatchedTexture", "UITextBatchedBitmap", "UICanvasSurface", "OpaquePointShadowDepth", "OpaqueSpotShadowDepth",
                    "SkyboxGradient", "SkyboxEquirectangular", "SkyboxOctahedral", "SkyboxCubemap", "SkyboxDynamicProcedural");
                int maximumSemanticVersion = semantic is "StandardLitColor" or "UIQuadBatched" or "UIQuadBatchedTexture" or "UITextBatchedBitmap" ? 2 : 1;
                int semanticVersion = Integer(variant.GetProperty("semanticVersion"), 1, maximumSemanticVersion);
                string target = Choice(variant, "target", "WebGPUWgsl");
                string pass = MaterialVariantSelector(variant.GetProperty("pass"));
                string vertexProfile = MaterialVariantSelector(variant.GetProperty("vertexProfile"));
                string outputProfile = MaterialVariantSelector(variant.GetProperty("outputProfile"));
                if (semantic == "StandardLitTexture")
                    Require(vertexProfile is "position-normal-uv-v1" or "position-normal-tangent-uv-v1" &&
                        (pass == "opaque-forward" && outputProfile is "linear-hdr-v1" or "linear-hdr-directional-shadow-v1" or "linear-hdr-local-shadows-v1" ||
                         pass == "depth-normal" && vertexProfile == "position-normal-tangent-uv-v1" && outputProfile == "normal-rgba16f-v1"),
                        "Lit-texture surfaces require their exact opaque color or mapped-normal profile.");
                if (semantic == "StandardLitColor" && semanticVersion == 2)
                    Require(pass == "forward-coverage" && vertexProfile == "static-position-normal-v1" &&
                            outputProfile is "linear-hdr-v1" or "linear-hdr-directional-shadow-v1" or "linear-hdr-local-shadows-v1" ||
                        pass == "depth-normal" && vertexProfile == "static-position-normal-v1" && outputProfile == "normal-rgba16f-v1" ||
                        pass == "depth" && vertexProfile == "static-position-v1" && outputProfile == "depth-normal-v1" ||
                        pass == "point-shadow-depth" && vertexProfile == "static-position-v1" && outputProfile == "radial-r16f-v1" ||
                        pass == "spot-shadow-depth" && vertexProfile == "static-position-v1" && outputProfile == "projected-r16f-v1",
                        "Lit-color coverage requires its exact color, normal, or depth profile.");
                if (semantic == "OpaqueShadowDepth")
                    Require(pass == "depth" && vertexProfile == "static-position-v1" && outputProfile == "depth-normal-v1",
                        "Opaque shadow depth requires its exact pass and profiles.");
                if (semantic == "OpaquePointShadowDepth")
                    Require(pass == "point-shadow-depth" && vertexProfile == "static-position-v1" && outputProfile == "radial-r16f-v1",
                        "Point shadow depth requires its exact radial color pass and profiles.");
                if (semantic == "OpaqueSpotShadowDepth")
                    Require(pass == "spot-shadow-depth" && vertexProfile == "static-position-v1" && outputProfile == "projected-r16f-v1",
                        "Spot shadow depth requires its exact projected color pass and profiles.");
                if (semantic is "SkyboxGradient" or "SkyboxEquirectangular" or "SkyboxOctahedral" or
                    "SkyboxCubemap" or "SkyboxDynamicProcedural")
                    Require(pass == "background" && vertexProfile == "fullscreen-sky-v1" && outputProfile == "linear-hdr-v1",
                        "Skybox requires its exact HDR background pass and profiles.");
                string? debugProfile = semantic switch
                {
                    "DebugPoint" => "instanced-debug-point-v1",
                    "DebugLine" => "instanced-debug-line-v1",
                    "DebugTriangle" => "instanced-debug-triangle-v1",
                    _ => null,
                };
                if (debugProfile is not null)
                    Require(pass == "debug-overlay" && vertexProfile == debugProfile && outputProfile == "display-rgba-v1",
                        "Debug primitives require their exact overlay pass and profiles.");
                string? uiProfile = semantic switch
                {
                    "UIQuadBatched" => "instanced-ui-quad-v1",
                    "UIQuadBatchedTexture" => "instanced-ui-quad-texture-v1",
                    "UITextBatchedBitmap" => "instanced-ui-bitmap-text-v1",
                    _ => null,
                };
                if (uiProfile is not null)
                    Require(pass == "screen-ui" && vertexProfile == uiProfile &&
                        outputProfile == (semanticVersion == 2 ? "canvas-rgba-v2" : "display-rgba-v1"),
                        "Screen UI requires its exact versioned output profile and batched pass.");
                if (semantic == "UICanvasSurface")
                    Require(pass == "canvas-composite" && vertexProfile == "position-uv-v1" && outputProfile == "linear-hdr-premultiplied-rgba-v1",
                        "Canvas surfaces require their exact composite pass and profiles.");
                string? descriptorIdentity = variant.GetProperty("descriptorIdentity").GetString();
                Require(descriptorIdentity is not null && Regex.IsMatch(descriptorIdentity, "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant),
                    "Material variant references an absent shader descriptor.");
                if (!shaderDescriptors.TryGetValue(descriptorIdentity!, out JsonElement descriptor))
                    throw new InvalidDataException("Material variant references an absent shader descriptor.");
                string key = string.Join('\u001f', semantic, semanticVersion, target, pass, vertexProfile, outputProfile);
                Require(keys.Add(key), "Duplicate material variant key.");
                Require(descriptor.GetProperty("pass").GetString() == pass && descriptor.GetProperty("target").GetString() == target,
                    "Material variant pass or target differs from its shader descriptor.");
                if (semantic == "OpaqueShadowDepth")
                {
                    JsonElement entries = descriptor.GetProperty("entryPoints");
                    Members(entries, "vertex");
                    Require(entries.GetProperty("vertex").GetString() == "depthVertex",
                        "Opaque shadow depth requires a vertex-only depth entry point.");
                }
                if (semantic == "UICanvasSurface")
                {
                    JsonElement entries = descriptor.GetProperty("entryPoints");
                    Members(entries, "vertex", "fragment");
                    Require(entries.GetProperty("vertex").GetString() == "canvasSurfaceVertex" &&
                        entries.GetProperty("fragment").GetString() == "canvasSurfaceFragment",
                        "Canvas surfaces require their exact vertex and fragment entry points.");
                }
                if (semantic is "OpaquePointShadowDepth" or "OpaqueSpotShadowDepth" or "SkyboxGradient" or "SkyboxEquirectangular" or
                    "SkyboxOctahedral" or "SkyboxCubemap" or "SkyboxDynamicProcedural")
                {
                    JsonElement entries = descriptor.GetProperty("entryPoints");
                    Members(entries, "vertex", "fragment");
                    Require(entries.TryGetProperty("vertex", out _) && entries.TryGetProperty("fragment", out _),
                        "Sky and local shadow variants require a vertex and fragment stage.");
                }
                Require(descriptor.TryGetProperty("materialVariant", out JsonElement declaration),
                    "Material variant is absent from its hash-owned shader descriptor.");
                Members(declaration, "semantic", "semanticVersion", "vertexProfile", "outputProfile");
                Require(declaration.GetProperty("semantic").GetString() == semantic
                    && Integer(declaration.GetProperty("semanticVersion"), 1, maximumSemanticVersion) == semanticVersion
                    && declaration.GetProperty("vertexProfile").GetString() == vertexProfile
                    && declaration.GetProperty("outputProfile").GetString() == outputProfile,
                    "Material variant differs from its hash-owned shader descriptor.");
                materialVariants.Add(new { semantic, semanticVersion, target, pass, vertexProfile, outputProfile, descriptorIdentity });
            }
        }
        List<object> pipelineArtifacts = [];
        if (recipe.TryGetProperty("pipelineArtifacts", out JsonElement pipelineValues))
        {
            Require(pipelineValues.ValueKind == JsonValueKind.Array && pipelineValues.GetArrayLength() <= 256,
                "Pipeline artifact catalog exceeds its limit.");
            HashSet<string> bindings = new(StringComparer.Ordinal);
            foreach (JsonElement pipeline in pipelineValues.EnumerateArray())
            {
                MembersOptional(pipeline, ["pass", "descriptorIdentity"], ["scope"]);
                JsonElement passValue = pipeline.GetProperty("pass");
                Require(passValue.ValueKind == JsonValueKind.String, "Pipeline artifact pass must be a string.");
                string pass = passValue.GetString()!;
                Require(Regex.IsMatch(pass, "^[a-z][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant),
                    "Pipeline artifact pass must be a bounded lowercase identifier.");
                string? scope = null;
                if (pipeline.TryGetProperty("scope", out JsonElement scopeValue))
                {
                    Require(scopeValue.ValueKind == JsonValueKind.String, "Pipeline artifact scope must be a string.");
                    scope = scopeValue.GetString()!;
                    Require(Regex.IsMatch(scope, "^[a-z][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant),
                        "Pipeline artifact scope must be a bounded lowercase identifier.");
                }
                string bindingKey = scope is null ? pass : scope + "::" + pass;
                Require(bindings.Add(bindingKey), "Duplicate pipeline artifact binding.");
                JsonElement identityValue = pipeline.GetProperty("descriptorIdentity");
                Require(identityValue.ValueKind == JsonValueKind.String, "Pipeline artifact identity must be a string.");
                string descriptorIdentity = identityValue.GetString()!;
                Require(Regex.IsMatch(descriptorIdentity, "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant),
                    "Pipeline artifact identity must be a lowercase SHA-256 descriptor hash.");
                if (!shaderDescriptors.TryGetValue(descriptorIdentity, out JsonElement descriptor))
                    throw new InvalidDataException("Pipeline artifact references an absent shader descriptor.");
                bool descriptorObject = descriptor.ValueKind == JsonValueKind.Object;
                JsonElement entries = default;
                bool hasEntries = descriptorObject && descriptor.TryGetProperty("entryPoints", out entries) &&
                    entries.ValueKind == JsonValueKind.Object;
                bool raster = hasEntries && entries.EnumerateObject().Count() == 2
                    && entries.TryGetProperty("vertex", out JsonElement vertex) && vertex.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(vertex.GetString())
                    && entries.TryGetProperty("fragment", out JsonElement fragment) && fragment.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(fragment.GetString())
                    && !descriptor.TryGetProperty("workgroupSize", out _);
                bool compute = hasEntries && entries.EnumerateObject().Count() == 1
                    && entries.TryGetProperty("compute", out JsonElement computeEntry) && computeEntry.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(computeEntry.GetString())
                    && descriptor.TryGetProperty("workgroupSize", out JsonElement workgroupSize)
                    && ValidPipelineWorkgroupSize(workgroupSize)
                    && descriptor.TryGetProperty("layout", out JsonElement layout) && layout.ValueKind == JsonValueKind.Object
                    && layout.TryGetProperty("vertexBuffers", out JsonElement vertexBuffers) && vertexBuffers.ValueKind == JsonValueKind.Array
                    && vertexBuffers.GetArrayLength() == 0;
                Require(descriptorObject
                    && descriptor.TryGetProperty("pass", out JsonElement descriptorPass) && descriptorPass.ValueKind == JsonValueKind.String
                    && descriptorPass.GetString() == pass
                    && descriptor.TryGetProperty("target", out JsonElement descriptorTarget) && descriptorTarget.ValueKind == JsonValueKind.String
                    && descriptorTarget.GetString() == "WebGPUWgsl"
                    && (raster ^ compute)
                    && !descriptor.TryGetProperty("materialVariant", out _),
                    "Pipeline artifact descriptor must be a complete matching WebGPU raster or compute program.");
                if (scope is null)
                    pipelineArtifacts.Add(new { pass, descriptorIdentity });
                else
                    pipelineArtifacts.Add(new { scope, pass, descriptorIdentity });
            }
        }
        List<object> computeArtifacts = [];
        HashSet<string> computeKernels = new(StringComparer.Ordinal);
        if (recipe.TryGetProperty("computeArtifacts", out JsonElement computeValues))
        {
            Require(computeValues.ValueKind == JsonValueKind.Array && computeValues.GetArrayLength() <= 4,
                "Compute artifact catalog exceeds four kernels.");
            foreach (JsonElement compute in computeValues.EnumerateArray())
            {
                Members(compute, "kernel", "descriptorIdentity");
                string kernel = Choice(compute, "kernel", "packed-skinning", "luminance-reduction",
                    "luminance-reduction-2d", "luminance-mipmap");
                Require(computeKernels.Add(kernel), "Compute artifact kernels must be unique.");
                JsonElement identityValue = compute.GetProperty("descriptorIdentity");
                Require(identityValue.ValueKind == JsonValueKind.String, "Compute artifact identity must be a string.");
                string identity = identityValue.GetString()!;
                Require(Regex.IsMatch(identity, "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant),
                    "Compute artifact identity must be a lowercase SHA-256 descriptor hash.");
                if (!shaderDescriptors.TryGetValue(identity, out JsonElement descriptor))
                    throw new InvalidDataException("Compute artifact references an absent shader descriptor.");
                bool skinning = kernel == "packed-skinning";
                bool mipmap = kernel == "luminance-mipmap";
                Require(descriptor.ValueKind == JsonValueKind.Object
                    && descriptor.GetProperty("pass").GetString() == (skinning ? "skinning" : kernel)
                    && descriptor.GetProperty("target").GetString() == "WebGPUWgsl"
                    && descriptor.GetProperty("semanticSchemaIdentity").GetString() == "xrengine.engine.compute.v1"
                    && descriptor.TryGetProperty("entryPoints", out JsonElement entries) && entries.ValueKind == JsonValueKind.Object
                    && entries.EnumerateObject().Count() == 1 && entries.GetProperty("compute").GetString() == (skinning ? "skin" : mipmap ? "generate" : "reduce")
                    && descriptor.TryGetProperty("workgroupSize", out JsonElement workgroup) && workgroup.ValueKind == JsonValueKind.Array
                    && workgroup.GetArrayLength() == 3 && workgroup[0].GetInt32() == (skinning ? 64 : mipmap ? 16 : 256)
                    && workgroup[1].GetInt32() == (mipmap ? 16 : 1) && workgroup[2].GetInt32() == 1
                    && !descriptor.TryGetProperty("materialVariant", out _),
                    "Compute artifact descriptor must match the selected engine WebGPU kernel.");
                computeArtifacts.Add(new { kernel, descriptorIdentity = identity });
            }
        }
        Dictionary<string, object?> manifestModel = new(StringComparer.Ordinal)
        {
            ["schema"] = 1, ["format"] = "xrengine-assets", ["startupWorld"] = startupWorld,
            ["startupSettings"] = startupSettings, ["shaderArtifacts"] = shaderArtifacts,
        };
        if (materialVariants.Count != 0)
            manifestModel.Add("materialVariants", materialVariants);
        if (defaultUiFont is not null)
            manifestModel.Add("defaultUiFont", defaultUiFont);
        if (publishedMetadata is not null)
            manifestModel.Add("publishedMetadata", publishedMetadata);
        if (pipelineArtifacts.Count != 0)
            manifestModel.Add("pipelineArtifacts", pipelineArtifacts);
        if (computeArtifacts.Count != 0)
            manifestModel.Add("computeArtifacts", computeArtifacts);
        if (essentialRoots is not null)
        {
            manifestModel.Add("essentialRoots", essentialRoots);
            manifestModel.Add("streamedRoots", streamedRoots);
        }
        manifestModel.Add("assets", cookedAssets);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(manifestModel, OutputOptions);
        Require(manifest.Length <= JsonLimit, "Engine asset manifest exceeds 1 MiB.");
        RejectLinks(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string payloadDirectory = Path.Combine(outputDirectory, "payload");
        RejectLinks(payloadDirectory);
        Directory.CreateDirectory(payloadDirectory);
        foreach ((string hash, byte[] bytes) in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(payloadDirectory, hash + ".bin");
            RejectLinks(destination);
            if (File.Exists(destination))
                Require(ReadBounded(destination, RawLimit).AsSpan().SequenceEqual(bytes), "Existing engine payload hash has different bytes.");
            else
                AtomicWrite(destination, bytes, false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        AtomicWrite(Path.Combine(outputDirectory, "manifest.json"), manifest, true);
    }

    private static string EngineAssetPath(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "Engine asset path must be a string.");
        string path = value.GetString()!;
        Require(path.Length <= 1024 && Regex.IsMatch(path, "^/(engine|game)/[A-Za-z0-9_. /-]+\\z", RegexOptions.CultureInvariant)
            && path.Split('/').Skip(1).All(part => part.Length != 0 && part is not "." and not ".."),
            $"Invalid engine asset path '{path}'.");
        return path;
    }

    private static string[] EngineAssetRoots(JsonElement recipe, string member,
        Dictionary<string, JsonElement> byPath, int minimum)
    {
        string[] roots = [.. Array(recipe, member, 4096, minimum).Select(EngineAssetPath)];
        Require(roots.Distinct(StringComparer.Ordinal).Count() == roots.Length,
            $"Engine {member} contains duplicate roots.");
        Require(roots.All(byPath.ContainsKey), $"Engine {member} references an absent asset.");
        return roots;
    }

    private static HashSet<string> EngineAssetClosure(IEnumerable<string> roots,
        IReadOnlyDictionary<string, string[]> dependencies)
    {
        HashSet<string> closure = new(StringComparer.Ordinal);
        Stack<string> pending = new(roots);
        while (pending.TryPop(out string? path))
        {
            if (!closure.Add(path))
                continue;
            foreach (string dependency in dependencies[path])
                pending.Push(dependency);
        }
        return closure;
    }

    private static string MaterialVariantSelector(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "Material variant selector must be a string.");
        string selector = value.GetString()!;
        Require(Regex.IsMatch(selector, "^[a-z][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant),
            "Material variant selector must be a lowercase bounded identifier.");
        return selector;
    }

    private static bool ValidPipelineWorkgroupSize(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            return false;
        long invocations = 1;
        foreach (JsonElement dimension in value.EnumerateArray())
        {
            if (!dimension.TryGetInt32(out int size) || size is < 1 or > 1024)
                return false;
            invocations *= size;
        }
        return invocations <= 1024;
    }
}
