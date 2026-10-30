using XREngine.Scene.Importers.SourceToon;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>Retains missing material inputs and proves when the selected shader does not use them.</summary>
internal static class SourceMaterialDependencyClassifier
{
    internal static void ClassifyMissingTextures(SourceProjectImportContext context, SourceDependencyGraph graph)
    {
        foreach (SourceDependencyEdge edge in graph.UnresolvedEdges)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (edge.Kind != SourceImportDependencyKind.RequiredVisual ||
                edge.ReferringMaterialTextureSlot is not { } slot ||
                edge.ReferringObjectFileId.HasValue)
                continue;

            try
            {
                string materialHash = SourceDependencyGraphBuilder.ComputeSha256(edge.SourcePath);
                SerializedMaterialDocument document = SerializedMaterialDocumentParser.ParseFile(edge.SourcePath);
                if (!document.Textures.TryGetValue(slot, out SerializedTexturePropertyDocument? texture) ||
                    texture.TextureReference.FileId != edge.TargetFileId ||
                    !string.Equals(texture.TextureReference.Guid, edge.TargetGuid, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? shaderPath = context.Resolver.Resolve(document.Shader.Guid);
                // The proof must depend on a shader already retained in the manifest.
                if (shaderPath is null || !graph.Nodes.ContainsKey(Path.GetFullPath(shaderPath)))
                    continue;

                string shaderHash = SourceDependencyGraphBuilder.ComputeSha256(shaderPath);
                string source = File.ReadAllText(shaderPath);
                SourceToonShaderMatchResult match = SourceToonShaderMatcher.Match(new SourceToonShaderMatchInput
                {
                    ShaderPath = shaderPath,
                    ShaderGuid = document.Shader.Guid,
                    ShaderSource = source,
                    PropertyNames = document.GetPropertyNames(),
                    OverrideTags = document.OverrideTags,
                });
                if (!SourceToonTextureUsageClassifier.IsInactiveLockedAo(document, slot, match, source))
                    continue;

                if (materialHash != SourceDependencyGraphBuilder.ComputeSha256(edge.SourcePath) ||
                    shaderHash != SourceDependencyGraphBuilder.ComputeSha256(shaderPath))
                    continue;

                edge.Kind = SourceImportDependencyKind.OptionalVisual;
                edge.InactiveMaterialSha256 = materialHash;
                edge.InactiveShaderPath = shaderPath;
                edge.InactiveShaderSha256 = shaderHash;
                edge.InactiveTextureReason = "The verified locked shader reduces all AO texture channels to one. " +
                    "All saved channel strengths are zero and are not animated. The reference remains in metadata; " +
                    "a material or shader change requires a new dependency check.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                context.AddDiagnostic("UNITYDEP0005", SourceImportDiagnosticSeverity.Warning,
                    SourceImportDiagnosticCategory.DependencyParsing,
                    $"Could not prove texture use: {ex.Message}. The missing input remains required.",
                    edge.SourcePath, edge.ReferringPropertyPath);
            }
        }
    }

    /// <summary>Rejects a proof if either source changes before material conversion reads it.</summary>
    internal static bool IsProofCurrent(SourceDependencyEdge edge)
    {
        if (edge.InactiveTextureReason is null || edge.InactiveShaderPath is null)
            return false;
        try
        {
            return edge.InactiveMaterialSha256 == SourceDependencyGraphBuilder.ComputeSha256(edge.SourcePath) &&
                edge.InactiveShaderSha256 == SourceDependencyGraphBuilder.ComputeSha256(edge.InactiveShaderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
