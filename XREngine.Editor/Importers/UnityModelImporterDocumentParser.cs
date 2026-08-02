using System.Globalization;
using System.Numerics;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Parses Unity ModelImporter metadata without reading the model payload as YAML.
/// </summary>
public static class UnityModelImporterDocumentParser
{
    public static UnityModelImporterDocument ParseForModel(string modelPath)
        => ParseFile(modelPath + ".meta");

    public static UnityModelImporterDocument ParseFile(string metaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metaPath);
        string normalized = Path.GetFullPath(metaPath);

        var yaml = new YamlStream();
        using var reader = new StreamReader(normalized);
        yaml.Load(reader);
        if (yaml.Documents.Count == 0 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root ||
            GetNode(root, "ModelImporter") is not YamlMappingNode importer)
        {
            throw new InvalidDataException($"'{normalized}' is not a Unity ModelImporter .meta document.");
        }

        YamlMappingNode materials = GetNode(importer, "materials") as YamlMappingNode ?? [];
        YamlMappingNode meshes = GetNode(importer, "meshes") as YamlMappingNode ?? [];
        YamlMappingNode animations = GetNode(importer, "animations") as YamlMappingNode ?? [];
        return new UnityModelImporterDocument
        {
            SourceMetaPath = normalized,
            FileIdsGeneration = GetInt(meshes, "fileIdsGeneration") ?? GetInt(importer, "fileIdsGeneration") ?? 2,
            ImportBlendShapes = (GetInt(meshes, "importBlendShapes") ?? GetInt(importer, "importBlendShapes") ?? 1) != 0,
            ImportAnimation = (GetInt(animations, "importAnimation") ?? GetInt(importer, "importAnimation") ?? 1) != 0,
            AnimationType = GetInt(importer, "animationType") ?? 0,
            GlobalScale = GetFloat(importer, "globalScale") ?? 1.0f,
            UseFileScale = (GetInt(importer, "useFileScale") ?? 1) != 0,
            UseFileUnits = (GetInt(meshes, "useFileUnits") ?? GetInt(importer, "useFileUnits") ?? 1) != 0,
            BakeAxisConversion = (GetInt(importer, "bakeAxisConversion") ?? 0) != 0,
            PreserveHierarchy = (GetInt(importer, "preserveHierarchy") ?? 0) != 0,
            SortHierarchyByName = (GetInt(importer, "sortHierarchyByName") ?? 0) != 0,
            MaterialImportMode = GetInt(materials, "materialImportMode") ?? 0,
            MaterialName = GetInt(materials, "materialName") ?? 0,
            MaterialSearch = GetInt(materials, "materialSearch") ?? 0,
            MaterialLocation = GetInt(materials, "materialLocation") ?? 0,
            SkeletonTransforms = ParseSkeletonTransforms(GetNode(importer, "humanDescription")),
            ExternalMaterialRemaps = ParseExternalObjects(GetNode(importer, "externalObjects")),
        };
    }

    private static IReadOnlyList<UnityModelSkeletonTransform> ParseSkeletonTransforms(YamlNode? node)
    {
        if (node is not YamlMappingNode humanDescription ||
            GetNode(humanDescription, "skeleton") is not YamlSequenceNode sequence)
        {
            return [];
        }

        var transforms = new List<UnityModelSkeletonTransform>(sequence.Children.Count);
        foreach (YamlMappingNode entry in sequence.Children.OfType<YamlMappingNode>())
        {
            string name = GetString(entry, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            transforms.Add(new UnityModelSkeletonTransform
            {
                Name = name,
                ParentName = GetString(entry, "parentName") ?? string.Empty,
                Position = GetVector3(entry, "position", Vector3.Zero),
                Rotation = GetQuaternion(entry, "rotation", Quaternion.Identity),
                Scale = GetVector3(entry, "scale", Vector3.One),
            });
        }

        return transforms;
    }

    private static IReadOnlyList<UnityExternalMaterialRemap> ParseExternalObjects(YamlNode? node)
    {
        if (node is not YamlSequenceNode sequence)
            return [];

        var remaps = new List<UnityExternalMaterialRemap>(sequence.Children.Count);
        foreach (YamlMappingNode entry in sequence.Children.OfType<YamlMappingNode>())
        {
            if (GetNode(entry, "first") is not YamlMappingNode source ||
                GetNode(entry, "second") is not YamlMappingNode target)
            {
                continue;
            }

            string name = GetString(source, "name") ?? string.Empty;
            UnityAssetReference reference = ParseReference(target);
            if (string.IsNullOrWhiteSpace(name) || !reference.HasExternalGuid)
                continue;

            remaps.Add(new UnityExternalMaterialRemap
            {
                SourceMaterialName = name,
                TargetMaterial = reference,
            });
        }

        return remaps;
    }

    private static UnityAssetReference ParseReference(YamlMappingNode mapping)
    {
        long.TryParse(GetString(mapping, "fileID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long fileId);
        int? type = int.TryParse(GetString(mapping, "type"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedType)
            ? parsedType
            : null;
        return new UnityAssetReference(fileId, GetString(mapping, "guid"), type);
    }

    private static YamlNode? GetNode(YamlMappingNode mapping, string key)
    {
        foreach ((YamlNode yamlKey, YamlNode value) in mapping.Children)
        {
            if (string.Equals((yamlKey as YamlScalarNode)?.Value, key, StringComparison.Ordinal))
                return value;
        }

        return null;
    }

    private static string? GetString(YamlMappingNode mapping, string key)
        => (GetNode(mapping, key) as YamlScalarNode)?.Value;

    private static int? GetInt(YamlMappingNode mapping, string key)
        => int.TryParse(GetString(mapping, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private static float? GetFloat(YamlMappingNode mapping, string key)
        => float.TryParse(GetString(mapping, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    private static Vector3 GetVector3(YamlMappingNode mapping, string key, Vector3 fallback)
    {
        if (GetNode(mapping, key) is not YamlMappingNode vector)
            return fallback;

        return new Vector3(
            GetFloat(vector, "x") ?? fallback.X,
            GetFloat(vector, "y") ?? fallback.Y,
            GetFloat(vector, "z") ?? fallback.Z);
    }

    private static Quaternion GetQuaternion(YamlMappingNode mapping, string key, Quaternion fallback)
    {
        if (GetNode(mapping, key) is not YamlMappingNode quaternion)
            return fallback;

        var value = new Quaternion(
            GetFloat(quaternion, "x") ?? fallback.X,
            GetFloat(quaternion, "y") ?? fallback.Y,
            GetFloat(quaternion, "z") ?? fallback.Z,
            GetFloat(quaternion, "w") ?? fallback.W);
        return value.LengthSquared() > 0.000001f
            ? Quaternion.Normalize(value)
            : Quaternion.Identity;
    }
}
