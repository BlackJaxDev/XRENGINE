using System.Text.Json;

namespace XREngine.Publishing;

public static partial class BrowserContentPackageBuilder
{
    /// <summary>Validates additive delivery membership without changing authored asset serialization.</summary>
    private static JsonElement? ValidateEngineShaderDelivery(JsonElement recipe, HashSet<string>? essential,
        string[]? streamedRoots, IEnumerable<string> shaderIdentities, Dictionary<string, JsonElement> assets,
        Dictionary<string, string[]> dependencies)
    {
        if (!recipe.TryGetProperty("shaderDelivery", out JsonElement delivery))
            return null;
        Require(essential is not null && streamedRoots is not null,
            "Shader delivery requires explicit essential and streamed roots.");
        Members(delivery, "schema", "startup", "scenes");
        Require(Integer(delivery.GetProperty("schema"), 1, 1) == 1, "Unsupported shader delivery schema.");
        HashSet<string> declared = new(shaderIdentities, StringComparer.Ordinal);
        HashSet<string> startup = ReadIdentities(delivery.GetProperty("startup"));
        foreach (string member in new[] { "materialVariants", "pipelineArtifacts", "computeArtifacts" })
            if (recipe.TryGetProperty(member, out JsonElement bindings))
                foreach (JsonElement binding in bindings.EnumerateArray())
                    Require(startup.Contains(binding.GetProperty("descriptorIdentity").GetString()!),
                        "Globally declared shader bindings must remain essential.");
        HashSet<string> covered = new(startup, StringComparer.Ordinal);
        HashSet<string> scenes = new(StringComparer.Ordinal);
        JsonElement[] sceneEntries = Array(delivery, "scenes", 256);
        Require(sceneEntries.Length == streamedRoots!.Length, "Shader delivery must declare each streamed scene exactly once.");
        foreach (JsonElement scene in sceneEntries)
        {
            Members(scene, "path", "identities");
            string path = EngineAssetPath(scene.GetProperty("path"));
            Require(streamedRoots.Contains(path, StringComparer.Ordinal) && scenes.Add(path),
                "Shader delivery contains a missing or repeated streamed scene.");
            covered.UnionWith(ReadIdentities(scene.GetProperty("identities")));
        }
        Require(covered.SetEquals(declared), "Shader delivery must cover every declared shader identity.");
        if (recipe.TryGetProperty("shaderArtifacts", out JsonElement shaders))
            foreach (JsonElement shader in shaders.EnumerateArray())
            {
                bool required = startup.Contains(shader.GetProperty("identity").GetString()!);
                string descriptor = shader.GetProperty("descriptor").GetString()!;
                string source = shader.GetProperty("source").GetString()!;
                Require(required == essential!.Contains(descriptor) && required == essential.Contains(source),
                    "Startup shader membership must match essential descriptor and source delivery.");
                Require(assets[descriptor].GetProperty("encoding").GetString() == "utf8-text"
                    && assets[source].GetProperty("encoding").GetString() == "utf8-text"
                    && dependencies[descriptor].Length == 0 && dependencies[source].Length == 0,
                    "Shader delivery payloads must be standalone text assets.");
            }
        return delivery.Clone();

        HashSet<string> ReadIdentities(JsonElement identities)
        {
            Require(identities.ValueKind == JsonValueKind.Array && identities.GetArrayLength() <= declared.Count,
                "Shader delivery identities exceed the declared shader catalog.");
            HashSet<string> values = new(StringComparer.Ordinal);
            foreach (JsonElement identity in identities.EnumerateArray())
                Require(identity.ValueKind == JsonValueKind.String && identity.GetString() is { } text
                    && declared.Contains(text) && values.Add(text),
                    "Shader delivery contains a missing or repeated descriptor identity.");
            return values;
        }
    }
}
