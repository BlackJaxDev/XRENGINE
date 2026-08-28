using System.Security.Cryptography;
using System.Text.RegularExpressions;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Builds a deterministic dependency closure by streaming only reached serialized files.
/// </summary>
public static partial class SourceDependencyGraphBuilder
{
    private static readonly HashSet<string> SerializedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".prefab",
        ".unity",
        ".mat",
        ".controller",
        ".overridecontroller",
        ".anim",
        ".asset",
        ".meta",
        ".shader",
        ".compute",
    };

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fbx", ".obj", ".gltf", ".glb", ".dae", ".3ds", ".blend", ".ply", ".stl", ".x",
    };

    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".exr", ".gif", ".psd", ".hdr", ".tga", ".tif", ".tiff",
    };

    public static SourceDependencyGraph Build(SourceProjectImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var graph = new SourceDependencyGraph();
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(context.EntrySourcePath, SourceImportDependencyKind.RequiredVisual, context, graph, active);

        foreach (SourceDependencyEdge missing in graph.UnresolvedEdges)
        {
            SourceImportDiagnosticSeverity severity = missing.Kind == SourceImportDependencyKind.RequiredVisual
                ? SourceImportDiagnosticSeverity.Error
                : SourceImportDiagnosticSeverity.Warning;
            context.AddDiagnostic(
                "UNITYDEP0001",
                severity,
                SourceImportDiagnosticCategory.GuidResolution,
                $"Unity GUID '{missing.TargetGuid}' referenced by '{missing.ReferringProperty}' could not be resolved.",
                missing.SourcePath,
                missing.ReferringProperty,
                new SourceAssetIdentity
                {
                    AssetGuid = missing.TargetGuid,
                    LocalFileId = missing.TargetFileId,
                    ObjectKind = InferObjectKind(missing.ReferringProperty),
                });
        }

        context.DependencyGraph = graph;
        return graph;
    }

    public static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Visit(
        string sourcePath,
        SourceImportDependencyKind kind,
        SourceProjectImportContext context,
        SourceDependencyGraph graph,
        HashSet<string> active)
    {
        string fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
            return;

        string? guid = context.GuidIndex.TryGetGuid(fullPath, out string? resolvedGuid)
            ? resolvedGuid
            : null;
        SourceDependencyNode node = graph.GetOrAdd(
            fullPath,
            () => new SourceDependencyNode
            {
                SourcePath = fullPath,
                SourceGuid = guid,
                PortablePath = context.GuidIndex.NormalizePortablePath(fullPath),
                Kind = kind,
            },
            kind);

        if (!active.Add(fullPath))
            return;

        try
        {
            string extension = GetCompleteExtension(fullPath);
            if (SerializedExtensions.Contains(extension))
                StreamReferences(node, fullPath, context, graph, active);

            string metaPath = fullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath + ".meta";
            if (!string.Equals(metaPath, fullPath, StringComparison.OrdinalIgnoreCase) && File.Exists(metaPath))
            {
                SourceDependencyNode metaNode = graph.GetOrAdd(
                    metaPath,
                    () => new SourceDependencyNode
                    {
                        SourcePath = metaPath,
                        SourceGuid = guid,
                        PortablePath = context.GuidIndex.NormalizePortablePath(fullPath) + ".meta",
                        Kind = kind,
                    },
                    kind);
                StreamReferences(metaNode, metaPath, context, graph, active);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            node.Outcome = SourceImportConversionOutcome.Failed;
            context.AddDiagnostic(
                "UNITYDEP0002",
                kind == SourceImportDependencyKind.RequiredVisual
                    ? SourceImportDiagnosticSeverity.Error
                    : SourceImportDiagnosticSeverity.Warning,
                SourceImportDiagnosticCategory.DependencyParsing,
                $"Could not inspect reached Unity dependency '{fullPath}': {ex.Message}",
                fullPath);
        }
        finally
        {
            active.Remove(fullPath);
        }
    }

    private static void StreamReferences(
        SourceDependencyNode sourceNode,
        string sourcePath,
        SourceProjectImportContext context,
        SourceDependencyGraph graph,
        HashSet<string> active)
    {
        string currentProperty = string.Empty;
        long? modificationTargetFileId = null;
        string? modificationPropertyPath = null;
        foreach (string line in File.ReadLines(sourcePath))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("guid:", StringComparison.Ordinal))
                continue;

            Match modificationTargetMatch = PrefabModificationTargetRegex().Match(line);
            if (modificationTargetMatch.Success)
            {
                modificationTargetFileId = long.Parse(
                    modificationTargetMatch.Groups["fileId"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                modificationPropertyPath = null;
            }

            Match modificationPropertyMatch = PrefabModificationPropertyRegex().Match(line);
            if (modificationPropertyMatch.Success)
                modificationPropertyPath = modificationPropertyMatch.Groups["propertyPath"].Value.Trim();

            Match propertyMatch = PropertyRegex().Match(trimmed);
            if (propertyMatch.Success)
                currentProperty = propertyMatch.Groups["property"].Value;

            MatchCollection matches = SourceReferenceRegex().Matches(line);
            foreach (Match match in matches)
            {
                string guid = match.Groups["guid"].Value;
                if (IsSourceBuiltInGuid(guid))
                    continue;

                long.TryParse(
                    match.Groups["fileId"].Value,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long localFileId);
                string serializedProperty = propertyMatch.Success
                    ? propertyMatch.Groups["property"].Value
                    : currentProperty;
                if (IsPrefabCorrespondenceProperty(serializedProperty))
                    continue;

                bool isPrefabModificationReference =
                    modificationTargetFileId.HasValue &&
                    string.Equals(serializedProperty, "objectReference", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(modificationPropertyPath);
                string property = isPrefabModificationReference
                    ? modificationPropertyPath!
                    : serializedProperty;
                string? targetPath = context.GuidIndex.ResolvePath(guid);
                SourceImportDependencyKind kind = ClassifyDependency(property, targetPath);
                bool cycle = targetPath is not null && active.Contains(targetPath);
                var edge = new SourceDependencyEdge
                {
                    SourcePath = sourcePath,
                    TargetGuid = guid,
                    TargetFileId = localFileId,
                    TargetPath = targetPath,
                    ReferringObjectFileId = isPrefabModificationReference
                        ? modificationTargetFileId
                        : null,
                    ReferringProperty = property,
                    Kind = kind,
                    IsCycle = cycle,
                };
                sourceNode.OutgoingEdges.Add(edge);

                if (targetPath is null)
                {
                    graph.UnresolvedEdges.Add(edge);
                    continue;
                }

                if (cycle)
                {
                    context.AddDiagnostic(
                        "UNITYDEP0003",
                        SourceImportDiagnosticSeverity.Warning,
                        SourceImportDiagnosticCategory.DependencyParsing,
                        $"Dependency cycle detected from '{sourcePath}' to '{targetPath}'. The edge was retained without recursively reopening the active document.",
                        sourcePath,
                        property);
                    continue;
                }

                Visit(targetPath, kind, context, graph, active);
            }
        }
    }

    private static SourceImportDependencyKind ClassifyDependency(string property, string? targetPath)
    {
        if (property.Contains("script", StringComparison.OrdinalIgnoreCase))
            return SourceImportDependencyKind.AvatarBehavior;
        if (property.Contains("probe", StringComparison.OrdinalIgnoreCase))
            return SourceImportDependencyKind.OptionalVisual;
        if (property.Contains("expression", StringComparison.OrdinalIgnoreCase) ||
            property.Contains("menu", StringComparison.OrdinalIgnoreCase) ||
            property.Contains("parameter", StringComparison.OrdinalIgnoreCase) ||
            property.Contains("icon", StringComparison.OrdinalIgnoreCase))
        {
            return SourceImportDependencyKind.AvatarBehavior;
        }
        if (property.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
            property.Contains("animation", StringComparison.OrdinalIgnoreCase) ||
            property.Contains("avatar", StringComparison.OrdinalIgnoreCase))
        {
            return SourceImportDependencyKind.Animation;
        }

        if (targetPath is null)
        {
            return property.Contains("material", StringComparison.OrdinalIgnoreCase) ||
                   property.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
                   property.Contains("mesh", StringComparison.OrdinalIgnoreCase) ||
                   property.Contains("sourcePrefab", StringComparison.OrdinalIgnoreCase) ||
                   property.Contains("shader", StringComparison.OrdinalIgnoreCase)
                ? SourceImportDependencyKind.RequiredVisual
                : SourceImportDependencyKind.AvatarBehavior;
        }

        string extension = GetCompleteExtension(targetPath);
        if (ModelExtensions.Contains(extension) ||
            TextureExtensions.Contains(extension) ||
            extension is ".mat" or ".shader" or ".prefab" or ".unity")
        {
            return SourceImportDependencyKind.RequiredVisual;
        }

        if (extension is ".controller" or ".overridecontroller" or ".anim")
            return SourceImportDependencyKind.Animation;
        if (extension is ".cs" or ".dll" or ".asmdef")
            return SourceImportDependencyKind.EditorOnly;

        return SourceImportDependencyKind.AvatarBehavior;
    }

    private static SourceAssetObjectKind InferObjectKind(string property)
    {
        if (property.Contains("material", StringComparison.OrdinalIgnoreCase))
            return SourceAssetObjectKind.Material;
        if (property.Contains("texture", StringComparison.OrdinalIgnoreCase))
            return SourceAssetObjectKind.Texture;
        if (property.Contains("mesh", StringComparison.OrdinalIgnoreCase))
            return SourceAssetObjectKind.Mesh;
        if (property.Contains("script", StringComparison.OrdinalIgnoreCase))
            return SourceAssetObjectKind.MonoBehaviour;
        return SourceAssetObjectKind.Asset;
    }

    private static string GetCompleteExtension(string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".overrideController", StringComparison.OrdinalIgnoreCase))
            return ".overridecontroller";
        return Path.GetExtension(path).ToLowerInvariant();
    }

    private static bool IsSourceBuiltInGuid(string guid)
        => guid.StartsWith("0000000000000000", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrefabCorrespondenceProperty(string property)
        => property is "target" or "m_CorrespondingSourceObject" or "targetCorrespondingSourceObject";

    // Anchor the property name so inline sequence mappings such as
    // "- {fileID: ..., guid: ...}" inherit their owning property (for example
    // m_Materials) instead of being reclassified under the nested "fileID"
    // token.
    [GeneratedRegex(@"^(?<property>[A-Za-z0-9_.$\[\]-]+)\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(
        @"\{[^\r\n{}]*?fileID:\s*(?<fileId>-?\d+)[^\r\n{}]*?guid:\s*(?<guid>[0-9a-fA-F]{32})[^\r\n{}]*?\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex SourceReferenceRegex();

    [GeneratedRegex(
        @"^\s*-\s*target:\s*\{[^\r\n{}]*?fileID:\s*(?<fileId>-?\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrefabModificationTargetRegex();

    [GeneratedRegex(
        @"^\s*propertyPath:\s*(?<propertyPath>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrefabModificationPropertyRegex();
}
