namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Canonical projection of model import settings that change imported semantic output.
/// </summary>
public static class ModelImportCanonicalSettings
{
    public static byte[] Serialize(ModelImportOptions options, string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        using ModelCacheCanonicalWriter writer = new();
        writer.WriteString(1, "xrengine.model-import-settings");
        writer.WriteUInt32(2, ModelBinaryCacheVersions.ImportSettingsProjection);
        writer.WriteUInt64(10, Convert.ToUInt64(options.ImportSteps));
        writer.WriteInt32(11, (int)options.FbxBackend);
        writer.WriteInt32(12, (int)options.GltfBackend);
        writer.WriteInt32(13, (int)options.FbxPivotPolicy);
        writer.WriteBoolean(14, options.CollapseGeneratedFbxHelperNodes);
        writer.WriteSingle(15, options.ScaleConversion);
        writer.WriteBoolean(16, options.ZUp);
        writer.WriteInt32(17, (int)options.DiffuseAlphaMode);
        writer.WriteInt32(18, (int)options.OpacityMapMode);
        writer.WriteBoolean(19, options.SplitSubmeshesIntoSeparateModelComponents);
        writer.WriteBoolean(20, options.GenerateSceneNodesPerSubmesh);
        writer.WriteBoolean(21, options.SeparateMeshIslands);
        writer.WriteInt32(22, options.SpatialPartitionMaxTriangles);
        writer.WriteString(23, NormalizeResolutionPath(options.SourceProjectRootOverride, sourceFilePath));
        writer.WriteBytes(24, SerializeSearchPaths(options.TextureLoadDirSearchPaths, sourceFilePath));

        // MultiThread, native worker count, ProcessMeshesAsynchronously,
        // GenerateMeshRenderersAsync, batch publication, and ProgressCallback affect only
        // execution. Texture/material remaps and their legacy values remain project authority.
        return writer.ToArray();
    }

    private static byte[] SerializeSearchPaths(
        IReadOnlyList<string>? searchPaths,
        string sourceFilePath)
    {
        searchPaths ??= [];
        using ModelCacheCanonicalWriter writer = new();
        writer.WriteInt32(1, searchPaths.Count);
        for (int index = 0; index < searchPaths.Count; index++)
            writer.WriteString((uint)(index + 2), NormalizeResolutionPath(searchPaths[index], sourceFilePath));
        return writer.ToArray();
    }

    private static string? NormalizeResolutionPath(string? path, string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string candidate = path.Trim();
        if (!Path.IsPathRooted(candidate))
        {
            string? sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
                candidate = Path.Combine(sourceDirectory, candidate);
        }

        return ModelImportPathNormalizer.NormalizeAbsolutePath(candidate);
    }
}
