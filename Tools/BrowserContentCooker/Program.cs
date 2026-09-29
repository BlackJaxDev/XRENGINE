using System.Security.Cryptography;
using System.Text.Json;

namespace XREngine.Tools.BrowserContentCooker;

/// <summary>Packages preconverted browser content into bounded, immutable payloads.</summary>
internal static partial class Program
{
    private const int JsonLimit = 1024 * 1024;
    private const int RawLimit = 4 * 1024 * 1024;
    private const long AggregateLimit = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: BrowserContentCooker <recipe.json> <output-directory>");
            return 2;
        }
        try
        {
            Cook(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or OverflowException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine($"Content cook failed: {error.Message}");
            return 1;
        }
    }

    private static void Cook(string recipePath, string outputDirectory)
    {
        RejectLinks(recipePath);
        using JsonDocument recipeDocument = ReadJson(ReadBounded(recipePath, JsonLimit));
        JsonElement recipe = recipeDocument.RootElement;
        Members(recipe, "schema", "entrypoints", "streamed", "assets");
        Require(recipe.GetProperty("schema").GetInt32() == 1, "Unsupported recipe schema.");
        JsonElement[] assets = Array(recipe, "assets", 4096, 1);
        Dictionary<string, JsonElement> byId = new(StringComparer.Ordinal);
        Dictionary<string, string[]> dependencies = new(StringComparer.Ordinal);
        foreach (JsonElement asset in assets)
        {
            Members(asset, "id", "kind", "dependencies", "variants");
            string id = Identifier(asset.GetProperty("id"));
            Require(byId.TryAdd(id, asset), $"Duplicate asset ID '{id}'.");
            Choice(asset, "kind", "mesh", "texture", "material", "scene");
            dependencies.Add(id, Ids(asset, "dependencies", 64));
        }
        string[] entrypoints = Ids(recipe, "entrypoints", 4096);
        string[] streamed = Ids(recipe, "streamed", 4096);
        Require(entrypoints.Length > 0, "At least one entrypoint is required.");
        foreach (string id in entrypoints.Concat(streamed))
            Require(byId.TryGetValue(id, out JsonElement root) && root.GetProperty("kind").GetString() == "scene", $"Root '{id}' must identify a scene asset.");
        Dictionary<string, int> heights = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        foreach (string id in byId.Keys)
            ValidateGraph(id, byId, dependencies, visiting, heights, 0);
        Require(assets.Count(asset => asset.GetProperty("kind").GetString() == "material") <= 256, "Package exceeds 256 materials.");

        List<object> cookedAssets = [];
        Dictionary<string, byte[]> payloads = new(StringComparer.Ordinal);
        long maximumSelectedBytes = 0;
        int instanceCount = 0;
        float[]? packageCamera = null;
        string recipeDirectory = Path.GetDirectoryName(recipePath)!;
        foreach (JsonElement asset in assets)
        {
            string id = Identifier(asset.GetProperty("id"));
            string kind = asset.GetProperty("kind").GetString()!;
            if (kind == "texture") Require(dependencies[id].Length == 0, "Texture assets cannot declare dependencies.");
            JsonElement[] variants = Array(asset, "variants", 8, 1);
            if (kind != "texture") Require(variants.Length == 1, $"'{id}' requires exactly one JSON variant.");
            List<Dictionary<string, object?>> cookedVariants = [];
            HashSet<string> variantHashes = new(StringComparer.Ordinal);
            int largestVariant = 0;
            foreach (JsonElement variant in variants)
            {
                ValidateVariant(variant, kind);
                string source = SourcePath(recipeDirectory, variant.GetProperty("source").GetString()!);
                byte[] payload = ReadBounded(source, kind == "texture" ? RawLimit : JsonLimit);
                Require(payload.Length > 0, $"Empty payload '{id}'.");
                if (kind == "texture") ValidateTextureBytes(variant, payload.Length);
                else
                {
                    (int instances, float[]? camera) = ValidatePayload(kind, payload, dependencies[id], byId);
                    instanceCount += instances;
                    Require(instanceCount <= 2048, "Package exceeds 2048 scene instances.");
                    if (camera is not null)
                    {
                        Require(packageCamera is null || packageCamera.AsSpan().SequenceEqual(camera), "Scene chunks must share the same camera matrices.");
                        packageCamera ??= camera;
                    }
                }
                string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
                Require(variantHashes.Add(hash), "Variants within one asset must have distinct payload hashes.");
                payloads.TryAdd(hash, payload);
                largestVariant = Math.Max(largestVariant, payload.Length);
                Dictionary<string, object?> cooked = new(StringComparer.Ordinal)
                {
                    ["hash"] = hash, ["url"] = $"payloads/{hash}.bin", ["bytes"] = payload.Length
                };
                foreach (JsonProperty property in variant.EnumerateObject())
                    if (property.Name != "source") cooked.Add(property.Name, property.Value.Clone());
                cookedVariants.Add(cooked);
            }
            if (kind == "texture") ValidateTextureFallback(variants);
            maximumSelectedBytes += largestVariant;
            Require(maximumSelectedBytes <= AggregateLimit, "Selected asset payload budget exceeds 64 MiB.");
            cookedAssets.Add(new { id, kind, dependencies = dependencies[id], variants = cookedVariants });
        }
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, profile = "browser-forward-v1", toolchain = "xrengine-browser-content-1",
            entrypoints, streamed, assets = cookedAssets
        }, OutputOptions);
        Require(manifest.Length <= JsonLimit, "Manifest exceeds 1 MiB.");

        // The mutable manifest is published last. Previously referenced content remains valid.
        RejectLinks(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string payloadDirectory = Path.Combine(outputDirectory, "payloads");
        RejectLinks(payloadDirectory);
        Directory.CreateDirectory(payloadDirectory);
        foreach ((string hash, byte[] bytes) in payloads)
        {
            string destination = Path.Combine(payloadDirectory, hash + ".bin");
            RejectLinks(destination);
            if (File.Exists(destination))
                Require(ReadBounded(destination, RawLimit).AsSpan().SequenceEqual(bytes), "Existing hashed payload has different contents.");
            else AtomicWrite(destination, bytes, false);
        }
        AtomicWrite(Path.Combine(outputDirectory, "manifest.json"), manifest, true);
        Console.WriteLine($"Published {assets.Length} assets and {payloads.Count} immutable payloads to manifest.json.");
    }

    private static void AtomicWrite(string destination, byte[] bytes, bool overwrite)
    {
        RejectLinks(destination);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
