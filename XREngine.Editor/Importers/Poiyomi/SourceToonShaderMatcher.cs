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

        if (SourceToonZekkSoftOutlineCatalog.Matches(input))
            return Accepted(SourceToonShaderMatchKind.ExactLockedSource,
                SourceToonZekkSoftOutlineCatalog.Version, isLocked: true);

        if (isProDowngradeSource)
        {
            string version = sourceVersion?.ToString() ?? "unknown";
            return new SourceToonShaderMatchResult
            {
                Kind = SourceToonShaderMatchKind.SourceToonFeatureLossSource,
                Version = sourceVersion,
                BehaviorVersion = SourceToonReleaseLineCatalog.GetBehaviorVersion(sourceVersion),
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

        SourceToonShaderMatchResult? releaseLineMatch = MatchReleaseLine(input, source, sourceVersion, isLockedName, hasSignature);
        if (releaseLineMatch is not null)
            return releaseLineMatch;

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
                $"Toon source shader version '{foundVersion}' has no verified identity and no admitted release line. Admitted release lines are {SourceToonReleaseLineCatalog.DescribeLines()}. Verified releases are 8.1.167, 9.2.66, 9.2.79, 9.3.64, and 10.0.25.");
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
            BehaviorVersion = version,
            SourceFamily = SourceToonShaderFamily.Toon,
            IsSourceToonFamily = true,
            IsAccepted = true,
            IsLocked = isLocked,
            Diagnostics = diagnostics,
        };

    /// <summary>
    /// Admits a release of a known release line that has no exact verified identity, or returns null.
    /// </summary>
    /// <remarks>
    /// An unlocked shader needs the main toon shader GUID of the line, or a version marker of the line and the main
    /// toon shader name. A locked shader needs the main toon shader GUID of the line as its original shader, or a
    /// version marker of the line, a locked name, and the property signature. Evidence that names two different
    /// lines, or an original shader that is not a main toon shader, is not admitted.
    /// </remarks>
    private static SourceToonShaderMatchResult? MatchReleaseLine(
        SourceToonShaderMatchInput input,
        string source,
        SourceToonShaderVersion? sourceVersion,
        bool isLockedName,
        bool hasSignature)
    {
        input.OverrideTags.TryGetValue(OriginalShaderGuidTag, out string? originalGuid);
        SourceToonReleaseLine? versionLine = SourceToonReleaseLineCatalog.FindByVersion(sourceVersion);
        SourceToonReleaseLine? guidLine = SourceToonReleaseLineCatalog.FindByGuid(input.ShaderGuid);
        SourceToonReleaseLine? originalLine = SourceToonReleaseLineCatalog.FindByGuid(originalGuid);
        SourceToonReleaseLine? line = versionLine ?? guidLine ?? originalLine;
        if (line is null)
            return null;

        // A version marker of one line with the GUID of another line is contradictory evidence.
        if (guidLine is not null && guidLine != line || originalLine is not null && originalLine != line)
            return null;

        // Locked variants of other shaders, such as two-pass or grab-pass shaders, keep the version marker
        // and the signature. Their original shader GUID is the only evidence that separates them.
        if (!string.IsNullOrWhiteSpace(originalGuid) && originalLine is null)
            return null;

        bool isLocked = isLockedName || originalLine is not null;
        bool admitted = isLocked
            ? originalLine is not null || versionLine is not null && hasSignature
            : guidLine is not null || versionLine is not null && MainToonShaderNameRegex().IsMatch(source);
        if (!admitted)
            return null;

        string versionText = sourceVersion?.ToString() ?? $"{line}.x";
        MaterialConversionDiagnostic diagnostic = new(
            MaterialConversionDiagnosticCodes.UnverifiedRelease,
            MaterialConversionDiagnosticSeverity.Warning,
            $"Toon source shader {versionText} has no exact verified identity. The converter admits it as a release of the {line} line and applies the conversion rules of verified {line.BehaviorVersion}. Compare the converted material with the source.");
        return new SourceToonShaderMatchResult
        {
            Kind = SourceToonShaderMatchKind.ReleaseLineSource,
            Version = sourceVersion,
            BehaviorVersion = line.BehaviorVersion,
            ReleaseLine = line,
            SourceFamily = SourceToonShaderFamily.Toon,
            IsSourceToonFamily = true,
            IsAccepted = true,
            IsLocked = isLocked,
            PropertyDefinitions = ReadReleaseLinePropertyDefinitions(line, source),
            Diagnostics = [diagnostic],
        };
    }

    /// <summary>
    /// Reads the property definitions that the conversion rules of the line need, or returns an empty set.
    /// </summary>
    /// <remarks>
    /// Only the 10.0 rules read definitions, to fill the defaults of properties that the material does not store.
    /// A source that cannot be read gives no defaults. The material values stay unchanged in that case.
    /// </remarks>
    private static IReadOnlyDictionary<string, SourceToonPropertyDefinition> ReadReleaseLinePropertyDefinitions(
        SourceToonReleaseLine line,
        string source)
    {
        if (line.BehaviorVersion != SourceToon10Catalog.Version || source.Length == 0)
            return new Dictionary<string, SourceToonPropertyDefinition>(StringComparer.Ordinal);

        try
        {
            return SourceToon10Catalog.ReadPropertyDefinitions(source);
        }
        catch (InvalidDataException)
        {
            return new Dictionary<string, SourceToonPropertyDefinition>(StringComparer.Ordinal);
        }
    }

    private static SourceToonShaderVersion? ExtractVersion(string source)
    {
        Match match = VersionMarkerRegex().Match(source);
        return match.Success && SourceToonShaderVersion.TryParse(match.Groups["version"].Value, out SourceToonShaderVersion version)
            ? version
            : null;
    }

    [GeneratedRegex(@"\bPoiyomi\s+(?<version>\d+\.\d+\.\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionMarkerRegex();

    /// <summary>Matches the name of the main toon shader in the current folder or in an older-version folder.</summary>
    [GeneratedRegex(@"\bShader\s+""\.poiyomi/(?:[^""/]+/)*Poiyomi Toon""", RegexOptions.CultureInvariant)]
    private static partial Regex MainToonShaderNameRegex();
}
