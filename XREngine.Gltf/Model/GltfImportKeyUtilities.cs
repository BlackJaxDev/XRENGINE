using System.Text.Json;

namespace XREngine.Gltf;

public static class GltfImportKeyUtilities
{
    public static bool IsGltfPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string extension = Path.GetExtension(path);
        return extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".glb", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetMaterialKeys(GltfRoot document)
    {
        List<string> baseNames = new(document.Materials.Count);
        for (int materialIndex = 0; materialIndex < document.Materials.Count; materialIndex++)
        {
            GltfMaterial material = document.Materials[materialIndex];
            string name = string.IsNullOrWhiteSpace(material.Name)
                ? $"Material {materialIndex}"
                : material.Name!;
            baseNames.Add(name);
        }

        return Deduplicate(baseNames);
    }

    public static string GetMaterialKey(GltfRoot document, int materialIndex)
    {
        IReadOnlyList<string> keys = GetMaterialKeys(document);
        return materialIndex >= 0 && materialIndex < keys.Count
            ? keys[materialIndex]
            : $"Material {materialIndex}";
    }

    public static IReadOnlyList<string> GetTextureKeys(GltfRoot document)
    {
        List<string> baseNames = new(document.Textures.Count);
        for (int textureIndex = 0; textureIndex < document.Textures.Count; textureIndex++)
            baseNames.Add(GetTextureDisplayName(document, textureIndex));

        return Deduplicate(baseNames);
    }

    public static string GetTextureKey(GltfRoot document, int textureIndex, string? purpose = null)
    {
        IReadOnlyList<string> keys = GetTextureKeys(document);
        string baseKey = textureIndex >= 0 && textureIndex < keys.Count
            ? keys[textureIndex]
            : $"Texture {textureIndex}";
        return string.IsNullOrWhiteSpace(purpose) ? baseKey : $"{baseKey} ({purpose})";
    }

    public static IEnumerable<string> EnumerateReferencedTextureKeys(GltfRoot document)
    {
        HashSet<string> emitted = new(StringComparer.Ordinal);

        foreach (GltfMaterial material in document.Materials)
        {
            if (material.PbrMetallicRoughness?.BaseColorTexture is { } baseColorTexture)
            {
                if (TryEmit(document, baseColorTexture.Index, null, emitted, out string key))
                    yield return key;
            }

            if (material.NormalTexture is { } normalTexture)
            {
                if (TryEmit(document, normalTexture.Index, null, emitted, out string key))
                    yield return key;
            }

            if (material.OcclusionTexture is { } occlusionTexture)
            {
                if (TryEmit(document, occlusionTexture.Index, null, emitted, out string key))
                    yield return key;
            }

            if (material.EmissiveTexture is { } emissiveTexture)
            {
                if (TryEmit(document, emissiveTexture.Index, null, emitted, out string key))
                    yield return key;
            }

            if (material.PbrMetallicRoughness?.MetallicRoughnessTexture is { } metallicRoughnessTexture)
            {
                if (TryEmit(document, metallicRoughnessTexture.Index, "metallic", emitted, out string metallicKey))
                    yield return metallicKey;
                if (TryEmit(document, metallicRoughnessTexture.Index, "roughness", emitted, out string roughnessKey))
                    yield return roughnessKey;
            }
        }
    }

    public static IReadOnlyList<string> GetMorphTargetNames(GltfMesh mesh)
    {
        if (mesh.Extras is not JsonElement extras || extras.ValueKind != JsonValueKind.Object)
            return [];

        if (!extras.TryGetProperty("targetNames", out JsonElement targetNames) || targetNames.ValueKind != JsonValueKind.Array)
            return [];

        List<string> names = [];
        foreach (JsonElement targetName in targetNames.EnumerateArray())
        {
            if (targetName.ValueKind == JsonValueKind.String)
                names.Add(targetName.GetString() ?? string.Empty);
        }

        return names;
    }

    private static bool TryEmit(GltfRoot document, int textureIndex, string? purpose, HashSet<string> emitted, out string key)
    {
        key = GetTextureKey(document, textureIndex, purpose);
        return emitted.Add(key);
    }

    private static string GetTextureDisplayName(GltfRoot document, int textureIndex)
    {
        if (textureIndex < 0 || textureIndex >= document.Textures.Count)
            return $"Texture {textureIndex}";

        GltfTexture texture = document.Textures[textureIndex];
        if (!string.IsNullOrWhiteSpace(texture.Name))
            return texture.Name!;

        if (texture.Source is int imageIndex && imageIndex >= 0 && imageIndex < document.Images.Count)
        {
            GltfImage image = document.Images[imageIndex];
            if (!string.IsNullOrWhiteSpace(image.Name))
                return image.Name!;

            if (!string.IsNullOrWhiteSpace(image.Uri))
            {
                string fileName = Path.GetFileName(image.Uri);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
        }

        return $"Texture {textureIndex}";
    }

    private static IReadOnlyList<string> Deduplicate(IReadOnlyList<string> names)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        for (int index = 0; index < names.Count; index++)
        {
            string key = names[index];
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        List<string> result = new(names.Count);
        for (int index = 0; index < names.Count; index++)
        {
            string key = names[index];
            if (counts[key] == 1)
            {
                result.Add(key);
                continue;
            }

            int occurrence = seen.TryGetValue(key, out int existingOccurrence) ? existingOccurrence + 1 : 1;
            seen[key] = occurrence;
            result.Add($"{key} [{occurrence}]");
        }

        return result;
    }
}