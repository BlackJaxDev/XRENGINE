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
        MembersOptional(recipe, ["schema", "format", "startupWorld", "assets"], ["startupSettings", "shaderArtifacts"]);
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
                shaderArtifacts.Add(new { identity, descriptor, source });
            }
        }
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, format = "xrengine-assets", startupWorld, startupSettings, shaderArtifacts, assets = cookedAssets
        }, OutputOptions);
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
}
