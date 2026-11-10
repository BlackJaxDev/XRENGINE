using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Removes active Pro-only feature groups while retaining fields understood by the pinned Toon converter.
/// </summary>
public static class SourceToonFeatureLossNormalizer
{
    private static readonly Regex ExternalGrabPassToken = new(
        @"(?<![A-Za-z0-9_])(?:GrabPass\s*(?:\{|$)|UsePass\s+""|Fallback\s+(?:""|Off\b))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ShaderUiColorTag = new(
        @"<color=#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?>", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string[]> DiscardedFeaturePrefixes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Grab Pass"] = ["_GrabPass", "_Grabpass"],
            ["Refraction"] = ["_Refraction", "_Refract"],
            ["Blur"] = ["_Blur"],
            ["Touch Effects"] = ["_Touch"],
            ["Pro Integrations"] = ["_ProIntegration", "_ProLTCGI", "_ProAudio"],
            ["Pro Vertex Effects"] = ["_ProVertex"],
            ["Pro Authoring Metadata"] = ["pro_", "POI_PRO"],
        };

    public static SerializedMaterialDocument Normalize(
        SerializedMaterialDocument source,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        string? resolvedShaderPath = null,
        bool isLockedShader = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var result = new SerializedMaterialDocument
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
        bool grabPassAbsent = IsGrabPassAbsentFromLockedShader(source, resolvedShaderPath, isLockedShader);
        CopyFiltered(source.Textures, result.Textures, source, diagnostics, reportedGroups, grabPassAbsent);
        CopyFiltered(source.Floats, result.Floats, source, diagnostics, reportedGroups, grabPassAbsent);
        CopyFiltered(source.Ints, result.Ints, source, diagnostics, reportedGroups, grabPassAbsent);
        CopyFiltered(source.Vectors, result.Vectors, source, diagnostics, reportedGroups, grabPassAbsent);
        CopyFiltered(source.Strings, result.Strings, source, diagnostics, reportedGroups, grabPassAbsent);
        CopyDictionary(source.UnknownSerializedFields, result.UnknownSerializedFields);
        CopyDictionary(source.UnknownSavedProperties, result.UnknownSavedProperties);
        return result;
    }

    private static void CopyFiltered<T>(
        IReadOnlyDictionary<string, T> sourceValues,
        IDictionary<string, T> destination,
        SerializedMaterialDocument sourceDocument,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        ISet<string> reportedGroups,
        bool grabPassAbsent)
    {
        foreach ((string name, T value) in sourceValues)
        {
            string? group = ResolveDiscardedGroup(name);
            if (group is null)
            {
                destination[name] = value;
                continue;
            }

            if ((grabPassAbsent && group == "Grab Pass") ||
                IsExplicitlyDisabled(group, sourceDocument) ||
                !IsActive(name, value, sourceDocument) || !reportedGroups.Add(group))
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

    private static bool IsExplicitlyDisabled(string group, SerializedMaterialDocument source)
    {
        string? masterProperty = group switch
        {
            "Grab Pass" => "_EnableGrabpass",
            "Refraction" => "_RefractionEnabled",
            "Blur" => "_EnableBlur",
            _ => null,
        };
        return masterProperty is not null &&
            source.TryGetFloat(masterProperty, out float enabled) &&
            MathF.Abs(enabled) <= 0.0001f;
    }

    private static bool IsGrabPassAbsentFromLockedShader(
        SerializedMaterialDocument source, string? shaderPath, bool isLockedShader)
    {
        if (!isLockedShader || string.IsNullOrWhiteSpace(shaderPath) ||
            !shaderPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ||
            source.ValidKeywords.Contains("POI_GRABPASS"))
            return false;

        try
        {
            bool hasShaderDeclaration = false;
            foreach (string line in File.ReadLines(shaderPath))
            {
                string trimmed = line.TrimStart();
                if (line.Contains('#') && !trimmed.StartsWith('#') &&
                    ShaderUiColorTag.Replace(line, string.Empty).Contains('#'))
                    return false;
                if (trimmed.StartsWith("Shader \"", StringComparison.Ordinal))
                    hasShaderDeclaration = true;
                if (ExternalGrabPassToken.IsMatch(line) ||
                    line.Contains("applyGrabEffects", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!trimmed.StartsWith('#'))
                    continue;

                string directive = trimmed[1..].TrimStart();
                if (directive.TrimEnd().EndsWith('\\'))
                    return false;
                Match keywordMatch = Regex.Match(directive, @"^[A-Za-z_]+", RegexOptions.CultureInvariant);
                if (!keywordMatch.Success)
                    return false;

                string keyword = keywordMatch.Value;
                if (keyword == "include")
                {
                    if (directive is not ("include \"UnityCG.cginc\"" or "include \"AutoLight.cginc\""))
                        return false;
                }
                else if ((keyword is "define" or "pragma") &&
                    directive.Contains("POI_GRABPASS", StringComparison.Ordinal))
                    return false;
                else if (keyword is not ("define" or "pragma" or "if" or "ifdef" or "ifndef" or
                    "elif" or "else" or "endif" or "undef" or "line" or "error" or "warning"))
                    return false;
            }

            return hasShaderDeclaration;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsActive<T>(string propertyName, T value, SerializedMaterialDocument source)
    {
        if (source.Textures.TryGetValue(propertyName, out SerializedTexturePropertyDocument? texture))
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
