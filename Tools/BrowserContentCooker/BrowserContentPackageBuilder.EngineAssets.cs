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
        MembersOptional(recipe, ["schema", "format", "startupWorld", "assets"], ["startupSettings", "shaderArtifacts", "materialVariants", "pipelineArtifacts"]);
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
        Dictionary<string, int> heights = new(StringComparer.Ordinal);
        foreach (string path in byPath.Keys)
            ValidateGraph(path, byPath, dependencies, new HashSet<string>(StringComparer.Ordinal), heights, 0);

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
                string semantic = Choice(variant, "semantic", "StandardLitColor", "OpaqueShadowDepth",
                    "DebugPoint", "DebugLine", "DebugTriangle");
                int semanticVersion = Integer(variant.GetProperty("semanticVersion"), 1, 1);
                string target = Choice(variant, "target", "WebGPUWgsl");
                string pass = MaterialVariantSelector(variant.GetProperty("pass"));
                string vertexProfile = MaterialVariantSelector(variant.GetProperty("vertexProfile"));
                string outputProfile = MaterialVariantSelector(variant.GetProperty("outputProfile"));
                if (semantic == "OpaqueShadowDepth")
                    Require(pass == "depth" && vertexProfile == "static-position-v1" && outputProfile == "depth-normal-v1",
                        "Opaque shadow depth requires its exact pass and profiles.");
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
                Require(descriptor.TryGetProperty("materialVariant", out JsonElement declaration),
                    "Material variant is absent from its hash-owned shader descriptor.");
                Members(declaration, "semantic", "semanticVersion", "vertexProfile", "outputProfile");
                Require(declaration.GetProperty("semantic").GetString() == semantic
                    && Integer(declaration.GetProperty("semanticVersion"), 1, 1) == semanticVersion
                    && declaration.GetProperty("vertexProfile").GetString() == vertexProfile
                    && declaration.GetProperty("outputProfile").GetString() == outputProfile,
                    "Material variant differs from its hash-owned shader descriptor.");
                materialVariants.Add(new { semantic, semanticVersion, target, pass, vertexProfile, outputProfile, descriptorIdentity });
            }
        }
        List<object> pipelineArtifacts = [];
        if (recipe.TryGetProperty("pipelineArtifacts", out JsonElement pipelineValues))
        {
            Require(pipelineValues.ValueKind == JsonValueKind.Array && pipelineValues.GetArrayLength() <= 16,
                "Pipeline artifact catalog exceeds its limit.");
            HashSet<string> passes = new(StringComparer.Ordinal);
            foreach (JsonElement pipeline in pipelineValues.EnumerateArray())
            {
                Members(pipeline, "pass", "descriptorIdentity");
                string pass = Choice(pipeline, "pass", "tonemap");
                Require(passes.Add(pass), "Duplicate pipeline artifact pass.");
                JsonElement identityValue = pipeline.GetProperty("descriptorIdentity");
                Require(identityValue.ValueKind == JsonValueKind.String, "Pipeline artifact identity must be a string.");
                string descriptorIdentity = identityValue.GetString()!;
                Require(Regex.IsMatch(descriptorIdentity, "^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant),
                    "Pipeline artifact identity must be a lowercase SHA-256 descriptor hash.");
                if (!shaderDescriptors.TryGetValue(descriptorIdentity, out JsonElement descriptor))
                    throw new InvalidDataException("Pipeline artifact references an absent shader descriptor.");
                Require(descriptor.ValueKind == JsonValueKind.Object
                    && descriptor.TryGetProperty("pass", out JsonElement descriptorPass) && descriptorPass.ValueKind == JsonValueKind.String
                    && descriptorPass.GetString() == pass
                    && descriptor.TryGetProperty("target", out JsonElement descriptorTarget) && descriptorTarget.ValueKind == JsonValueKind.String
                    && descriptorTarget.GetString() == "WebGPUWgsl",
                    "Pipeline artifact pass or target differs from its shader descriptor.");
                pipelineArtifacts.Add(new { pass, descriptorIdentity });
            }
        }
        Dictionary<string, object?> manifestModel = new(StringComparer.Ordinal)
        {
            ["schema"] = 1, ["format"] = "xrengine-assets", ["startupWorld"] = startupWorld,
            ["startupSettings"] = startupSettings, ["shaderArtifacts"] = shaderArtifacts,
        };
        if (materialVariants.Count != 0)
            manifestModel.Add("materialVariants", materialVariants);
        if (pipelineArtifacts.Count != 0)
            manifestModel.Add("pipelineArtifacts", pipelineArtifacts);
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

    private static string MaterialVariantSelector(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "Material variant selector must be a string.");
        string selector = value.GetString()!;
        Require(Regex.IsMatch(selector, "^[a-z][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant),
            "Material variant selector must be a lowercase bounded identifier.");
        return selector;
    }
}
