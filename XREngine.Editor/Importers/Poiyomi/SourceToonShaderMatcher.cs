using System.Text.RegularExpressions;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Matches unlocked and Thry optimizer-generated shaders against verified Poiyomi sources.
/// </summary>
public static partial class SourceToonShaderMatcher
{
    private const string OriginalShaderGuidTag = "OriginalShaderGUID";
    private const string OriginalShaderTag = "OriginalShader";

    private static readonly string[] RequiredSignature =
    [
        "shader_master_label",
        "shader_is_using_thry_editor",
        "_ShaderOptimizerEnabled",
        "_MainTex",
        "_ShadingEnabled",
    ];

    private static readonly string[] ProPropertyEvidence =
    [
        "_GrabPass",
        "_Refraction",
        "_Blur",
        "_Touch",
        "_ProIntegration",
        "_ProLTCGI",
        "_ProAudio",
        "_ProVertex",
    ];

    public static SourceToonShaderMatchResult Match(SourceToonShaderMatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        string normalizedPath = input.ShaderPath?.Replace('\\', '/') ?? string.Empty;
        string source = input.ShaderSource ?? string.Empty;
        bool hasCanonicalName = source.Contains("Shader \".poiyomi/Poiyomi Toon\"", StringComparison.Ordinal);
        bool hasSourceToonPath = normalizedPath.Contains("Poiyomi", StringComparison.OrdinalIgnoreCase);
        bool hasCanonicalToonPath =
            (normalizedPath.Contains("/_PoiyomiShaders/Shaders/9.3/Toon/", StringComparison.OrdinalIgnoreCase) ||
             normalizedPath.Contains(SourceToon92Catalog.ShaderPathSegment, StringComparison.OrdinalIgnoreCase) ||
             normalizedPath.Contains(SourceToon10Catalog.ShaderPathSegment, StringComparison.OrdinalIgnoreCase)) &&
            Path.GetFileName(normalizedPath).StartsWith("Poiyomi Toon", StringComparison.OrdinalIgnoreCase);
        bool hasSignature = RequiredSignature.All(input.PropertyNames.Contains);
        bool isLockedName =
            source.Contains("Shader \"Hidden/Locked/", StringComparison.Ordinal) ||
            normalizedPath.Contains("/OptimizedShaders/", StringComparison.OrdinalIgnoreCase);
        bool hasOptimizerMarker =
            source.Contains("OPTIMIZER_ENABLED", StringComparison.Ordinal) ||
            input.PropertyNames.Contains("_ShaderOptimizerEnabled");
        SourceToonShaderVersion? sourceVersion = ExtractVersion(source);
        bool supported92 = SourceToon92Catalog.Supports(sourceVersion);
        bool exact93 = sourceVersion == SourceToon93Catalog.Version;
        bool exactGuid = sourceVersion is null &&
            string.Equals(input.ShaderGuid, SourceToon93Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase);
        bool originalGuidMatch =
            sourceVersion is null &&
            input.OverrideTags.TryGetValue(OriginalShaderGuidTag, out string? originalGuid) &&
            string.Equals(originalGuid, SourceToon93Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase);
        bool sourceGuidMatches92 =
            string.Equals(input.ShaderGuid, SourceToon92Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase) ||
            input.OverrideTags.TryGetValue(OriginalShaderGuidTag, out string? original92Guid) &&
            string.Equals(original92Guid, SourceToon92Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase);
        bool hasProName =
            source.Contains("Poiyomi Pro", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("Poiyomi Pro", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/Pro/", StringComparison.OrdinalIgnoreCase);
        bool hasOriginalProName =
            input.OverrideTags.TryGetValue(OriginalShaderTag, out string? originalShaderName) &&
            originalShaderName.Contains("Poiyomi Pro", StringComparison.OrdinalIgnoreCase);
        bool hasProProperties = input.PropertyNames.Any(name =>
            ProPropertyEvidence.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        bool hasNonToonOriginalGuid =
            input.OverrideTags.TryGetValue(OriginalShaderGuidTag, out string? originalShaderGuid) &&
            !string.IsNullOrWhiteSpace(originalShaderGuid) &&
            !string.Equals(originalShaderGuid, SourceToon93Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(originalShaderGuid, SourceToon92Catalog.ShaderGuid, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(originalShaderGuid, SourceToon10Catalog.OriginalShaderGuid, StringComparison.OrdinalIgnoreCase);
        bool isProDowngradeSource =
            hasProName || hasOriginalProName ||
            (hasProProperties && (hasSourceToonPath || hasSignature || isLockedName)) ||
            (hasNonToonOriginalGuid && hasProProperties && hasOptimizerMarker);

        if (isProDowngradeSource)
        {
            string version = sourceVersion?.ToString() ?? "unknown";
            return new SourceToonShaderMatchResult
            {
                Kind = SourceToonShaderMatchKind.SourceToonFeatureLossSource,
                Version = sourceVersion,
                SourceFamily = SourceToonShaderFamily.Pro,
                IsSourceToonFamily = true,
                IsAccepted = false,
                IsDowngradeSource = true,
                IsLocked = isLockedName,
                Diagnostics =
                [
                    new MaterialConversionDiagnostic(
                        MaterialConversionDiagnosticCodes.ProLossyDowngrade,
                        MaterialConversionDiagnosticSeverity.Warning,
                        $"Poiyomi Pro {version} input is being converted to the native Uber shader. Common Toon-compatible modules are preserved; only active Pro-exclusive feature groups are omitted and reported separately."),
                ],
            };
        }

        if (exactGuid)
            return Accepted(SourceToonShaderMatchKind.ExactGuid, SourceToon93Catalog.Version, isLocked: false);

        if (originalGuidMatch)
            return Accepted(SourceToonShaderMatchKind.ExactLockedSource, SourceToon93Catalog.Version, isLocked: true);

        if (exact93 && hasCanonicalName && !isLockedName)
            return Accepted(SourceToonShaderMatchKind.ExactUnlockedSource, sourceVersion, isLocked: false);

        if (exact93 && (isLockedName || hasOptimizerMarker) && (hasCanonicalName || hasSignature))
            return Accepted(SourceToonShaderMatchKind.ExactLockedSource, sourceVersion, isLocked: true);

        if (supported92 && sourceGuidMatches92 && hasCanonicalName && !isLockedName)
            return Accepted(SourceToonShaderMatchKind.ExactUnlockedSource, sourceVersion, isLocked: false);

        if (supported92 && sourceGuidMatches92 && isLockedName && hasSignature)
            return Accepted(SourceToonShaderMatchKind.ExactLockedSource, sourceVersion, isLocked: true);

        if (SourceToon81Catalog.Matches(input, sourceVersion) && isLockedName && hasSignature)
            return Accepted(SourceToonShaderMatchKind.ExactLockedSource, sourceVersion, isLocked: true);

        if (SourceToon10Catalog.Matches(input, sourceVersion, isLockedName) &&
            (isLockedName ? hasSignature : hasCanonicalName && hasCanonicalToonPath))
            return Accepted(
                isLockedName ? SourceToonShaderMatchKind.ExactLockedSource : SourceToonShaderMatchKind.ExactUnlockedSource,
                sourceVersion,
                isLockedName) with
            {
                PropertyDefinitions = SourceToon10Catalog.ReadPropertyDefinitions(source),
            };

        bool familyEvidence = hasCanonicalName || hasSourceToonPath || hasCanonicalToonPath || hasSignature || sourceGuidMatches92;
        if (hasSignature && hasOptimizerMarker && sourceVersion is null && !sourceGuidMatches92)
        {
            MaterialConversionDiagnostic diagnostic = new(
                MaterialConversionDiagnosticCodes.AmbiguousLockedSignature,
                MaterialConversionDiagnosticSeverity.Warning,
                "The generated shader matches the Poiyomi 9.3.64 property signature but has no original GUID or exact version marker. Conversion will preserve this ambiguity in its report.");
            return Accepted(
                SourceToonShaderMatchKind.LockedPropertySignature,
                SourceToon93Catalog.Version,
                isLocked: true,
                diagnostic);
        }

        if (familyEvidence)
        {
            string foundVersion = sourceVersion?.ToString() ?? "unavailable";
            MaterialConversionDiagnostic diagnostic = new(
                MaterialConversionDiagnosticCodes.UnknownVersion,
                MaterialConversionDiagnosticSeverity.Warning,
                $"Poiyomi Toon version '{foundVersion}' has no verified source identity for conversion. Supported sources are verified 8.1.167, 9.2.66, 9.2.79, 9.3.64, and 10.0.25 shaders.");
            return new SourceToonShaderMatchResult
            {
                Kind = SourceToonShaderMatchKind.UnsupportedVersion,
                Version = sourceVersion,
                SourceFamily = SourceToonShaderFamily.Toon,
                IsSourceToonFamily = true,
                IsAccepted = false,
                IsLocked = isLockedName,
                Diagnostics = [diagnostic],
            };
        }

        return new SourceToonShaderMatchResult
        {
            Kind = SourceToonShaderMatchKind.NotSourceToon,
            SourceFamily = SourceToonShaderFamily.None,
            IsSourceToonFamily = false,
            IsAccepted = false,
            IsLocked = false,
        };
    }

    private static SourceToonShaderMatchResult Accepted(
        SourceToonShaderMatchKind kind,
        SourceToonShaderVersion? version,
        bool isLocked,
        params MaterialConversionDiagnostic[] diagnostics)
        => new()
        {
            Kind = kind,
            Version = version,
            SourceFamily = SourceToonShaderFamily.Toon,
            IsSourceToonFamily = true,
            IsAccepted = true,
            IsLocked = isLocked,
            Diagnostics = diagnostics,
        };

    private static SourceToonShaderVersion? ExtractVersion(string source)
    {
        Match match = VersionMarkerRegex().Match(source);
        return match.Success && SourceToonShaderVersion.TryParse(match.Groups["version"].Value, out SourceToonShaderVersion version)
            ? version
            : null;
    }

    [GeneratedRegex(@"\bPoiyomi\s+(?<version>\d+\.\d+\.\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionMarkerRegex();
}
