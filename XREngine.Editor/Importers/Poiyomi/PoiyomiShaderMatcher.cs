using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Matches unlocked and Thry optimizer-generated shaders against the pinned Poiyomi target.
/// </summary>
public static partial class PoiyomiShaderMatcher
{
    private const string OriginalShaderGuidTag = "OriginalShaderGUID";

    private static readonly string[] RequiredSignature =
    [
        "shader_master_label",
        "shader_is_using_thry_editor",
        "_ShaderOptimizerEnabled",
        "_MainTex",
        "_ShadingEnabled",
    ];

    public static PoiyomiShaderMatchResult Match(PoiyomiShaderMatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        string normalizedPath = input.ShaderPath?.Replace('\\', '/') ?? string.Empty;
        string source = input.ShaderSource ?? string.Empty;
        bool hasCanonicalName = source.Contains("Shader \".poiyomi/Poiyomi Toon\"", StringComparison.Ordinal);
        bool hasPoiyomiPath = normalizedPath.Contains("Poiyomi", StringComparison.OrdinalIgnoreCase);
        bool hasCanonical93Path =
            normalizedPath.Contains("/_PoiyomiShaders/Shaders/9.3/Toon/", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(normalizedPath).StartsWith("Poiyomi Toon", StringComparison.OrdinalIgnoreCase);
        bool hasSignature = RequiredSignature.All(input.PropertyNames.Contains);
        bool isLockedName =
            source.Contains("Shader \"Hidden/Locked/", StringComparison.Ordinal) ||
            normalizedPath.Contains("/OptimizedShaders/", StringComparison.OrdinalIgnoreCase);
        bool hasOptimizerMarker =
            source.Contains("OPTIMIZER_ENABLED", StringComparison.Ordinal) ||
            input.PropertyNames.Contains("_ShaderOptimizerEnabled");
        bool exactGuid = string.Equals(input.ShaderGuid, PoiyomiToon93Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase);
        bool originalGuidMatch =
            input.OverrideTags.TryGetValue(OriginalShaderGuidTag, out string? originalGuid) &&
            string.Equals(originalGuid, PoiyomiToon93Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase);

        PoiyomiShaderVersion? sourceVersion = ExtractVersion(source);
        bool exactVersion = sourceVersion == PoiyomiToon93Catalog.Version;

        if (exactGuid)
            return Accepted(PoiyomiShaderMatchKind.ExactGuid, PoiyomiToon93Catalog.Version, isLocked: false);

        if (originalGuidMatch)
            return Accepted(PoiyomiShaderMatchKind.ExactLockedSource, PoiyomiToon93Catalog.Version, isLocked: true);

        if (exactVersion && hasCanonicalName && !isLockedName)
            return Accepted(PoiyomiShaderMatchKind.ExactUnlockedSource, sourceVersion, isLocked: false);

        if (exactVersion && (isLockedName || hasOptimizerMarker) && (hasCanonicalName || hasSignature))
            return Accepted(PoiyomiShaderMatchKind.ExactLockedSource, sourceVersion, isLocked: true);

        bool familyEvidence = hasCanonicalName || hasPoiyomiPath || hasCanonical93Path || hasSignature;
        if (hasSignature && hasOptimizerMarker && sourceVersion is null)
        {
            MaterialConversionDiagnostic diagnostic = new(
                MaterialConversionDiagnosticCodes.AmbiguousLockedSignature,
                MaterialConversionDiagnosticSeverity.Warning,
                "The generated shader matches the Poiyomi 9.3.64 property signature but has no original GUID or exact version marker. Conversion will preserve this ambiguity in its report.");
            return Accepted(
                PoiyomiShaderMatchKind.LockedPropertySignature,
                PoiyomiToon93Catalog.Version,
                isLocked: true,
                diagnostic);
        }

        if (familyEvidence)
        {
            string foundVersion = sourceVersion?.ToString() ?? "unavailable";
            MaterialConversionDiagnostic diagnostic = new(
                MaterialConversionDiagnosticCodes.UnknownVersion,
                MaterialConversionDiagnosticSeverity.Warning,
                $"Poiyomi Toon version '{foundVersion}' is not the pinned 9.3.64 target. Poiyomi-specific conversion was rejected; pin or add a version catalog before converting it.");
            return new PoiyomiShaderMatchResult
            {
                Kind = PoiyomiShaderMatchKind.UnsupportedVersion,
                Version = sourceVersion,
                IsPoiyomiFamily = true,
                IsAccepted = false,
                IsLocked = isLockedName,
                Diagnostics = [diagnostic],
            };
        }

        return new PoiyomiShaderMatchResult
        {
            Kind = PoiyomiShaderMatchKind.NotPoiyomi,
            IsPoiyomiFamily = false,
            IsAccepted = false,
            IsLocked = false,
        };
    }

    private static PoiyomiShaderMatchResult Accepted(
        PoiyomiShaderMatchKind kind,
        PoiyomiShaderVersion? version,
        bool isLocked,
        params MaterialConversionDiagnostic[] diagnostics)
        => new()
        {
            Kind = kind,
            Version = version,
            IsPoiyomiFamily = true,
            IsAccepted = true,
            IsLocked = isLocked,
            Diagnostics = diagnostics,
        };

    private static PoiyomiShaderVersion? ExtractVersion(string source)
    {
        Match match = VersionMarkerRegex().Match(source);
        return match.Success && PoiyomiShaderVersion.TryParse(match.Groups["version"].Value, out PoiyomiShaderVersion version)
            ? version
            : null;
    }

    [GeneratedRegex(@"\bPoiyomi\s+(?<version>\d+\.\d+\.\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionMarkerRegex();
}
