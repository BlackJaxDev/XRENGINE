namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Removes active Pro-only feature groups while retaining fields understood by the pinned Toon converter.
/// </summary>
public static class PoiyomiProDowngradeNormalizer
{
    private static readonly IReadOnlyDictionary<string, string[]> DiscardedFeaturePrefixes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Grab Pass"] = ["_GrabPass", "_Grabpass"],
            ["Refraction"] = ["_Refraction", "_Refract"],
            ["Blur"] = ["_Blur"],
            ["Touch Effects"] = ["_Touch"],
            ["Pro Integrations"] = ["_ProIntegration", "_ProLTCGI", "_ProAudio"],
            ["Pro Vertex Effects"] = ["_ProVertex"],
            ["Pro Authoring Metadata"] = ["_Pro", "pro_", "POI_PRO"],
        };

    public static UnityMaterialDocument Normalize(
        UnityMaterialDocument source,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var result = new UnityMaterialDocument
        {
            Name = source.Name,
            SourcePath = source.SourcePath,
            RawYaml = source.RawYaml,
            SerializedVersion = source.SerializedVersion,
            SavedPropertiesSerializedVersion = source.SavedPropertiesSerializedVersion,
            Shader = source.Shader,
            CustomRenderQueue = source.CustomRenderQueue,
        };

        CopySet(source.ValidKeywords, result.ValidKeywords);
        CopySet(source.InvalidKeywords, result.InvalidKeywords);
        CopySet(source.DisabledShaderPasses, result.DisabledShaderPasses);
        CopyDictionary(source.OverrideTags, result.OverrideTags);
        var reportedGroups = new HashSet<string>(StringComparer.Ordinal);
        CopyFiltered(source.Textures, result.Textures, source, diagnostics, reportedGroups);
        CopyFiltered(source.Floats, result.Floats, source, diagnostics, reportedGroups);
        CopyFiltered(source.Ints, result.Ints, source, diagnostics, reportedGroups);
        CopyFiltered(source.Vectors, result.Vectors, source, diagnostics, reportedGroups);
        CopyFiltered(source.Strings, result.Strings, source, diagnostics, reportedGroups);
        CopyDictionary(source.UnknownSerializedFields, result.UnknownSerializedFields);
        CopyDictionary(source.UnknownSavedProperties, result.UnknownSavedProperties);
        return result;
    }

    private static void CopyFiltered<T>(
        IReadOnlyDictionary<string, T> sourceValues,
        IDictionary<string, T> destination,
        UnityMaterialDocument sourceDocument,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ISet<string> reportedGroups)
    {
        foreach ((string name, T value) in sourceValues)
        {
            string? group = ResolveDiscardedGroup(name);
            if (group is null)
            {
                destination[name] = value;
                continue;
            }

            if (!IsActive(name, value, sourceDocument) || !reportedGroups.Add(group))
                continue;

            diagnostics.Add(new MaterialConversionDiagnostic(
                MaterialConversionDiagnosticCodes.ProFeatureDiscarded,
                MaterialConversionDiagnosticSeverity.Warning,
                $"Active Poiyomi Pro-only feature group '{group}' was discarded during Toon downgrade.",
                name));
        }
    }

    private static string? ResolveDiscardedGroup(string propertyName)
    {
        foreach ((string group, string[] prefixes) in DiscardedFeaturePrefixes)
        {
            if (prefixes.Any(prefix => propertyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return group;
        }

        return null;
    }

    private static bool IsActive<T>(string propertyName, T value, UnityMaterialDocument source)
    {
        if (source.Textures.TryGetValue(propertyName, out UnityTexturePropertyDocument? texture))
            return texture.TextureReference.FileId != 0 || texture.TextureReference.HasExternalGuid;
        if (value is float number)
            return MathF.Abs(number) > 0.0001f;
        if (value is int integer)
            return integer != 0;
        if (value is System.Numerics.Vector4 vector)
            return vector.LengthSquared() > 0.0000001f;
        if (value is string text)
            return !string.IsNullOrWhiteSpace(text);
        return value is not null;
    }

    private static void CopySet(IEnumerable<string> source, ISet<string> destination)
    {
        foreach (string value in source)
            destination.Add(value);
    }

    private static void CopyDictionary<T>(
        IReadOnlyDictionary<string, T> source,
        IDictionary<string, T> destination)
    {
        foreach ((string key, T value) in source)
            destination[key] = value;
    }
}
