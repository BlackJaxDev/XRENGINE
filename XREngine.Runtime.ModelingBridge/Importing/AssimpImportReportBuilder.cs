using Assimp;
using System.Text;
using System.Text.Json;
using XREngine.Rendering.Models.Caching;
using AScene = Assimp.Scene;

namespace XREngine;

/// <summary>
/// Builds normalized metadata from the successful Assimp scene and format sidecars.
/// </summary>
internal static class AssimpImportReportBuilder
{
    private const string MaterialNameProperty = "?mat.name";
    private const string TextureFileProperty = "$tex.file";

    public static ModelImportProducerMetadata Build(string sourceFilePath, AScene scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(scene);

        List<ModelImportDependency> dependencies =
        [
            ModelImportDependency.FromFile(
                sourceFilePath,
                ModelImportDependencyKind.EntrySource,
                isRequired: true,
                producerKey: "assimp:entry"),
        ];
        List<ModelImportSourceEntity> sourceEntities = [];
        List<ModelImportReferenceKey> referenceKeys = [];
        List<string> diagnostics = [];

        AddObjMaterialLibraries(sourceFilePath, dependencies);
        AddGltfExternalDependencies(sourceFilePath, dependencies, diagnostics);
        AddNodeEntities(scene.RootNode, parentKey: "assimp", siblingIndex: 0, sourceEntities);

        for (int meshIndex = 0; meshIndex < scene.MeshCount; meshIndex++)
        {
            Mesh mesh = scene.Meshes[meshIndex];
            sourceEntities.Add(new ModelImportSourceEntity(
                $"assimp:mesh:{meshIndex}",
                ModelImportEntityKind.Mesh,
                mesh.Name,
                isStable: false));
        }

        for (int materialIndex = 0; materialIndex < scene.MaterialCount; materialIndex++)
        {
            Material material = scene.Materials[materialIndex];
            MaterialProperty[] properties = material.GetAllProperties();
            string materialKey = GetPropertyString(properties, MaterialNameProperty) ?? "DefaultMaterial";
            sourceEntities.Add(new ModelImportSourceEntity(
                $"assimp:material:{materialIndex}",
                ModelImportEntityKind.Material,
                materialKey,
                isStable: false));
            referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Material, materialKey));

            int textureOrdinal = 0;
            foreach (MaterialProperty property in properties)
            {
                if (!string.Equals(property.Name, TextureFileProperty, StringComparison.Ordinal))
                    continue;

                string? rawPath = property.GetStringValue();
                if (string.IsNullOrWhiteSpace(rawPath))
                    continue;

                referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Texture, rawPath));
                string? dependencyPath = ModelImportPathNormalizer.ResolveLocalReference(sourceFilePath, rawPath);
                if (dependencyPath is not null)
                {
                    dependencies.Add(ModelImportDependency.FromFile(
                        dependencyPath,
                        ModelImportDependencyKind.ReferencedTexture,
                        isRequired: false,
                        producerKey: $"assimp:material:{materialIndex}:texture:{textureOrdinal}"));
                }

                textureOrdinal++;
            }
        }

        for (int animationIndex = 0; animationIndex < scene.AnimationCount; animationIndex++)
        {
            string animationKey = string.IsNullOrWhiteSpace(scene.Animations[animationIndex].Name)
                ? $"Animation_{animationIndex}"
                : scene.Animations[animationIndex].Name;
            sourceEntities.Add(new ModelImportSourceEntity(
                $"assimp:animation:{animationIndex}",
                ModelImportEntityKind.Animation,
                animationKey,
                isStable: false));
            referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Animation, animationKey));
        }

        return new ModelImportProducerMetadata(
            dependencies,
            sourceEntities,
            referenceKeys,
            diagnostics);
    }

    private static void AddNodeEntities(
        Node? node,
        string parentKey,
        int siblingIndex,
        ICollection<ModelImportSourceEntity> entities)
    {
        if (node is null)
            return;

        string nodeName = string.IsNullOrWhiteSpace(node.Name) ? "Node" : node.Name;
        string key = $"{parentKey}/{EscapeKeySegment(nodeName)}[{siblingIndex}]";
        entities.Add(new ModelImportSourceEntity(
            key,
            ModelImportEntityKind.Node,
            nodeName,
            isStable: false));

        for (int childIndex = 0; childIndex < node.ChildCount; childIndex++)
            AddNodeEntities(node.Children[childIndex], key, childIndex, entities);
    }

    private static string EscapeKeySegment(string value)
        => value.Normalize(NormalizationForm.FormC)
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("/", "%2F", StringComparison.Ordinal);

    private static string? GetPropertyString(
        IEnumerable<MaterialProperty> properties,
        string propertyName)
    {
        foreach (MaterialProperty property in properties)
            if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                return property.GetStringValue();

        return null;
    }

    private static void AddObjMaterialLibraries(
        string sourceFilePath,
        ICollection<ModelImportDependency> dependencies)
    {
        if (!string.Equals(Path.GetExtension(sourceFilePath), ".obj", StringComparison.OrdinalIgnoreCase))
            return;

        int ordinal = 0;
        foreach (string line in File.ReadLines(sourceFilePath))
        {
            ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
            const string directive = "mtllib";
            if (!trimmed.StartsWith(directive, StringComparison.OrdinalIgnoreCase)
                || trimmed.Length == directive.Length
                || !char.IsWhiteSpace(trimmed[directive.Length]))
                continue;

            string libraryReference = trimmed[directive.Length..].Trim().ToString().Trim('"');
            string? dependencyPath = ModelImportPathNormalizer.ResolveLocalReference(
                sourceFilePath,
                libraryReference);
            if (dependencyPath is null)
                continue;

            dependencies.Add(ModelImportDependency.FromFile(
                dependencyPath,
                ModelImportDependencyKind.Structural,
                isRequired: true,
                producerKey: $"obj:mtllib:{ordinal}"));
            ordinal++;
        }
    }

    private static void AddGltfExternalDependencies(
        string sourceFilePath,
        ICollection<ModelImportDependency> dependencies,
        ICollection<string> diagnostics)
    {
        if (!string.Equals(Path.GetExtension(sourceFilePath), ".gltf", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using FileStream stream = File.OpenRead(sourceFilePath);
            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            AddGltfUriArray(
                sourceFilePath,
                document.RootElement,
                "buffers",
                ModelImportDependencyKind.Structural,
                isRequired: true,
                "assimp:gltf:buffer",
                dependencies);
            AddGltfUriArray(
                sourceFilePath,
                document.RootElement,
                "images",
                ModelImportDependencyKind.ReferencedTexture,
                isRequired: false,
                "assimp:gltf:image",
                dependencies);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            diagnostics.Add(
                $"Assimp imported the glTF document, but its dependency metadata could not be inspected: {ex.Message}");
        }
    }

    private static void AddGltfUriArray(
        string sourceFilePath,
        JsonElement root,
        string propertyName,
        ModelImportDependencyKind kind,
        bool isRequired,
        string producerKeyPrefix,
        ICollection<ModelImportDependency> dependencies)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement values)
            || values.ValueKind != JsonValueKind.Array)
            return;

        int index = 0;
        foreach (JsonElement value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("uri", out JsonElement uriValue)
                && uriValue.ValueKind == JsonValueKind.String)
            {
                string? dependencyPath = ModelImportPathNormalizer.ResolveLocalReference(
                    sourceFilePath,
                    uriValue.GetString());
                if (dependencyPath is not null)
                {
                    dependencies.Add(ModelImportDependency.FromFile(
                        dependencyPath,
                        kind,
                        isRequired,
                        producerKey: $"{producerKeyPrefix}:{index}"));
                }
            }

            index++;
        }
    }
}
