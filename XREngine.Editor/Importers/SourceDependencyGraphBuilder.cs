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
        var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(context.EntrySourcePath, SourceImportDependencyKind.RequiredVisual, context, graph, active, scannedFiles);

        SourceMaterialDependencyClassifier.ClassifyMissingTextures(context, graph);
        ResolveOverriddenPrefabMaterials(context, graph);
        DeferRecoverableShaders(context, graph);

        foreach (SourceDependencyEdge missing in graph.UnresolvedEdges)
        {
            if (!missing.DeferredPrefabMaterialValidation && !missing.DeferredShaderConversionValidation)
                AddMissingDiagnostic(context, missing);
        }

        context.DependencyGraph = graph;
        return graph;
    }

    internal static void AddMissingDiagnostic(SourceProjectImportContext context, SourceDependencyEdge missing)
    {
        SourceImportDiagnosticSeverity severity = missing.Kind == SourceImportDependencyKind.RequiredVisual && !context.AllowPartialImport
            ? SourceImportDiagnosticSeverity.Error
            : SourceImportDiagnosticSeverity.Warning;
        context.AddDiagnostic(
            missing.InactiveTextureReason is null ? "UNITYDEP0001" : "UNITYDEP0004",
            missing.InactiveTextureReason is null ? severity : SourceImportDiagnosticSeverity.Info,
            SourceImportDiagnosticCategory.GuidResolution,
            $"Unity GUID '{missing.TargetGuid}' with file ID '{missing.TargetFileId}' referenced by '{Path.GetFileName(missing.SourcePath)}' at '{missing.ReferringPropertyPath}' could not be resolved." +
                (missing.InactiveTextureReason is null ? string.Empty : $" {missing.InactiveTextureReason}"),
            missing.SourcePath,
            missing.ReferringPropertyPath,
            new SourceAssetIdentity
            {
                AssetGuid = missing.TargetGuid,
                LocalFileId = missing.TargetFileId,
                ObjectKind = missing.ReferringMaterialTextureSlot is null
                    ? InferObjectKind(missing.ReferringProperty)
                    : SourceAssetObjectKind.Texture,
            });
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
        HashSet<string> active,
        HashSet<string> scannedFiles)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
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
                StreamReferences(node, fullPath, context, graph, active, scannedFiles);

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
                StreamReferences(metaNode, metaPath, context, graph, active, scannedFiles);
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
        HashSet<string> active,
        HashSet<string> scannedFiles)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!scannedFiles.Add(sourcePath))
            return;

        string currentProperty = string.Empty;
        bool isMaterial = sourcePath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
        int texturePropertiesIndent = -1;
        string? materialTextureSlot = null;
        long? modificationTargetFileId = null;
        string? modificationPropertyPath = null;
        string? modificationTargetGuid = null;
        long? prefabInstanceFileId = null;
        long? documentFileId = null;
        int? documentClassId = null;
        int nextMaterialIndex = -1;
        string? prefabSourceGuid = null;
        var prefabDocumentEdges = new List<SourceDependencyEdge>();
        var seenMaterialModifications = new HashSet<string>(StringComparer.Ordinal);
        var repeatedMaterialModifications = new HashSet<string>(StringComparer.Ordinal);

        static string MaterialModificationKey(long targetFileId, string targetGuid, string property)
            => $"{targetFileId}:{targetGuid.ToLowerInvariant()}:{property}";

        void CompletePrefabDocument()
        {
            foreach (SourceDependencyEdge edge in prefabDocumentEdges)
            {
                if (edge.ReferringObjectFileId is long targetFileId &&
                    edge.ReferringTargetGuid is string targetGuid &&
                    repeatedMaterialModifications.Contains(MaterialModificationKey(
                        targetFileId, targetGuid, edge.ReferringProperty)))
                    edge.AmbiguousPrefabModification = true;
            }
            seenMaterialModifications.Clear();
            repeatedMaterialModifications.Clear();

            if (!prefabInstanceFileId.HasValue || string.IsNullOrWhiteSpace(prefabSourceGuid))
            {
                prefabDocumentEdges.Clear();
                return;
            }

            string? modelPath = context.GuidIndex.ResolvePath(prefabSourceGuid);
            if (modelPath is null || !ModelExtensions.Contains(GetCompleteExtension(modelPath)) ||
                !File.Exists(modelPath + ".meta") ||
                SerializedModelImporterDocumentParser.ParseFile(modelPath + ".meta").FileIdsGeneration != 2)
            {
                prefabDocumentEdges.Clear();
                return;
            }

            foreach (SourceDependencyEdge edge in prefabDocumentEdges)
            {
                if (string.Equals(edge.ReferringTargetGuid, prefabSourceGuid, StringComparison.OrdinalIgnoreCase) &&
                    edge.ReferringProperty.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                    edge.DeferredPrefabMaterialValidation = true;
            }
            prefabDocumentEdges.Clear();
        }

        foreach (string line in File.ReadLines(sourcePath))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string trimmed = line.Trim();
            Match documentHeader = PrefabInstanceDocumentHeaderRegex().Match(line);
            if (documentHeader.Success)
            {
                CompletePrefabDocument();
                documentFileId = long.Parse(documentHeader.Groups["fileId"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                documentClassId = int.Parse(documentHeader.Groups["classId"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                prefabInstanceFileId = documentClassId == 1001
                    ? documentFileId
                    : null;
                prefabSourceGuid = null;
                modificationTargetFileId = null;
                modificationTargetGuid = null;
                modificationPropertyPath = null;
                currentProperty = string.Empty;
                nextMaterialIndex = -1;
            }
            if (trimmed.StartsWith("guid:", StringComparison.Ordinal))
                continue;

            if (isMaterial && trimmed.Length > 0)
            {
                int indent = line.Length - line.TrimStart().Length;
                if (texturePropertiesIndent >= 0 && indent < texturePropertiesIndent)
                {
                    texturePropertiesIndent = -1;
                    materialTextureSlot = null;
                }
                else if (texturePropertiesIndent >= 0 && indent == texturePropertiesIndent &&
                    !trimmed.StartsWith("- ", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("m_TexEnvs:", StringComparison.Ordinal))
                {
                    texturePropertiesIndent = -1;
                    materialTextureSlot = null;
                }

                if (trimmed.StartsWith("m_TexEnvs:", StringComparison.Ordinal))
                {
                    texturePropertiesIndent = indent;
                    materialTextureSlot = null;
                }
                else if (texturePropertiesIndent >= 0 && indent == texturePropertiesIndent)
                {
                    Match textureSlotMatch = MaterialTextureSlotRegex().Match(trimmed);
                    materialTextureSlot = textureSlotMatch.Success
                        ? textureSlotMatch.Groups["slot"].Value
                        : null;
                }
            }

            if (prefabInstanceFileId.HasValue && PrefabModificationTargetPrefixRegex().IsMatch(line))
            {
                modificationTargetFileId = null;
                modificationTargetGuid = null;
                modificationPropertyPath = null;
                Match modificationTargetMatch = PrefabModificationTargetRegex().Match(line);
                if (modificationTargetMatch.Success)
                {
                    modificationTargetFileId = long.Parse(
                        modificationTargetMatch.Groups["fileId"].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    Match targetGuidMatch = PrefabModificationTargetGuidRegex().Match(line);
                    if (targetGuidMatch.Success)
                        modificationTargetGuid = targetGuidMatch.Groups["guid"].Value;
                }
            }

            Match modificationPropertyMatch = PrefabModificationPropertyRegex().Match(line);
            if (prefabInstanceFileId.HasValue && modificationPropertyMatch.Success)
            {
                modificationPropertyPath = modificationPropertyMatch.Groups["propertyPath"].Value.Trim();
                if (modificationTargetFileId is long targetFileId &&
                    modificationTargetGuid is string targetGuid &&
                    modificationPropertyPath.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                {
                    string key = MaterialModificationKey(targetFileId, targetGuid, modificationPropertyPath);
                    if (!seenMaterialModifications.Add(key))
                        repeatedMaterialModifications.Add(key);
                }
            }

            Match propertyMatch = PropertyRegex().Match(trimmed);
            if (propertyMatch.Success)
            {
                currentProperty = propertyMatch.Groups["property"].Value;
                nextMaterialIndex = currentProperty == "m_Materials" ? 0 : -1;
            }

            int? materialIndex = null;
            if (nextMaterialIndex >= 0 && currentProperty == "m_Materials" &&
                trimmed.StartsWith("- {fileID:", StringComparison.Ordinal))
                materialIndex = nextMaterialIndex++;

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
                    : isMaterial && materialTextureSlot is not null &&
                        trimmed.Contains("m_Texture:", StringComparison.Ordinal)
                        ? "m_Texture"
                        : currentProperty;
                if (IsPrefabCorrespondenceProperty(serializedProperty))
                    continue;

                if (prefabInstanceFileId.HasValue && serializedProperty == "m_SourcePrefab")
                    prefabSourceGuid = guid;

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
                    ReferringDocumentFileId = documentFileId,
                    ReferringDocumentClassId = documentClassId,
                    ReferringMaterialIndex = materialIndex,
                    ReferringObjectFileId = isPrefabModificationReference
                        ? modificationTargetFileId
                        : null,
                    ReferringPrefabInstanceFileId = isPrefabModificationReference
                        ? prefabInstanceFileId
                        : null,
                    ReferringTargetGuid = isPrefabModificationReference
                        ? modificationTargetGuid
                        : null,
                    ReferringProperty = property,
                    ReferringMaterialTextureSlot = isMaterial &&
                        string.Equals(serializedProperty, "m_Texture", StringComparison.Ordinal)
                        ? materialTextureSlot
                        : null,
                    Kind = kind,
                    IsCycle = cycle,
                };
                sourceNode.OutgoingEdges.Add(edge);
                if (isPrefabModificationReference)
                    prefabDocumentEdges.Add(edge);

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

                Visit(targetPath, kind, context, graph, active, scannedFiles);
            }
        }
        CompletePrefabDocument();
    }

    private static void ResolveOverriddenPrefabMaterials(SourceProjectImportContext context, SourceDependencyGraph graph)
    {
        if (!graph.Nodes.TryGetValue(context.EntrySourcePath, out SourceDependencyNode? entryNode) ||
            !context.EntrySourcePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (SourceDependencyEdge missing in graph.UnresolvedEdges.ToArray())
        {
            if (missing.ReferringDocumentClassId != 199 ||
                missing.ReferringMaterialIndex is not int slot ||
                missing.ReferringDocumentFileId is not long rendererId ||
                missing.ReferringProperty != "m_Materials" ||
                !missing.SourcePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                !graph.Nodes.TryGetValue(missing.SourcePath, out SourceDependencyNode? prefabNode) ||
                string.IsNullOrWhiteSpace(prefabNode.SourceGuid))
                continue;

            string property = $"m_Materials.Array.data[{slot}]";
            SourceDependencyEdge[] instances = entryNode.OutgoingEdges.Where(edge =>
                string.Equals(edge.TargetPath, missing.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                edge.ReferringProperty == "m_SourcePrefab" &&
                string.Equals(edge.TargetGuid, prefabNode.SourceGuid, StringComparison.OrdinalIgnoreCase) &&
                edge.ReferringDocumentFileId.HasValue).ToArray();
            if (instances.Length == 0)
                continue;

            int incomingInstances = graph.Nodes.Values.Sum(node => node.OutgoingEdges.Count(edge =>
                string.Equals(edge.TargetPath, missing.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                edge.ReferringProperty == "m_SourcePrefab"));
            if (incomingInstances != instances.Length)
                continue;

            bool allReplaced = true;
            foreach (SourceDependencyEdge instance in instances)
            {
                SourceDependencyEdge? replacement = entryNode.OutgoingEdges.LastOrDefault(edge =>
                    edge.ReferringPrefabInstanceFileId == instance.ReferringDocumentFileId &&
                    edge.ReferringObjectFileId == rendererId &&
                    string.Equals(edge.ReferringTargetGuid, prefabNode.SourceGuid, StringComparison.OrdinalIgnoreCase) &&
                    edge.ReferringProperty == property);
                if (replacement?.TargetPath is null || replacement.AmbiguousPrefabModification ||
                    replacement.TargetFileId != 2100000 ||
                    !File.Exists(replacement.TargetPath) ||
                    !replacement.TargetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    allReplaced = false;
                    break;
                }
            }

            if (!allReplaced)
                continue;

            prefabNode.OutgoingEdges.Remove(missing);
            graph.UnresolvedEdges.Remove(missing);
            context.ReportUnsupportedVisual(
                "UNITYDEP0005",
                $"Missing particle material '{missing.TargetGuid}/{missing.TargetFileId}' in '{Path.GetFileName(missing.SourcePath)}' is replaced at '{property}' in every reached scene instance. ParticleSystemRenderer is not imported, so this particle effect is omitted.",
                missing.SourcePath,
                property);
        }
    }

    private static void DeferRecoverableShaders(SourceProjectImportContext context, SourceDependencyGraph graph)
    {
        foreach (SourceDependencyEdge missing in graph.UnresolvedEdges)
        {
            if (missing.ReferringProperty != "m_Shader" || missing.TargetFileId != 4800000 ||
                !missing.SourcePath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
                !context.Resolver.TryFindOriginalShaderName(missing.TargetGuid, out _, out _))
                continue;

            missing.DeferredShaderConversionValidation = true;
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

    [GeneratedRegex(@"^\s*-\s*target\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex PrefabModificationTargetPrefixRegex();

    [GeneratedRegex(@"\bguid:\s*(?<guid>[0-9a-fA-F]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex PrefabModificationTargetGuidRegex();

    [GeneratedRegex(@"^---\s*!u!(?<classId>-?\d+)\s*&(?<fileId>-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PrefabInstanceDocumentHeaderRegex();

    [GeneratedRegex(
        @"^\s*propertyPath:\s*(?<propertyPath>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrefabModificationPropertyRegex();

    [GeneratedRegex(@"^-\s+(?<slot>[A-Za-z0-9_.$-]+):", RegexOptions.CultureInvariant)]
    private static partial Regex MaterialTextureSlotRegex();
}
