using System.Buffers;
using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Maps normalized source and semantic variant identities into bounded cache paths.
/// </summary>
public static class ModelCachePathResolver
{
    public const int MaximumPreferredPathLength = 240;
    public const int MaximumSourceRelativeLength = 160;
    public const int MaximumDisplaySegmentLength = 64;

    private static readonly SearchValues<char> InvalidDisplayCharacters =
        SearchValues.Create("<>:\"/\\|?*\0");

    public static ModelCachePathResolution Resolve(
        string cacheRoot,
        ModelCacheSourceIdentity sourceIdentity,
        ModelImportBackendResolution backendResolution,
        ModelCacheVariantFingerprint variantFingerprint,
        string assetExtension = "asset")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(backendResolution);
        ArgumentNullException.ThrowIfNull(variantFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetExtension);

        if (!Path.IsPathFullyQualified(cacheRoot))
            throw new ArgumentException("The cache root must be fully qualified.", nameof(cacheRoot));
        if (!IsLowerHex(variantFingerprint.Value, expectedLength: 32))
            throw new ArgumentException("The model variant must be 32 lowercase hexadecimal characters.", nameof(variantFingerprint));
        if (!IsLowerHex(backendResolution.CandidateListHash, expectedLength: 64))
            throw new ArgumentException("The backend candidate hash must be lowercase SHA-256.", nameof(backendResolution));

        string resolverKey =
            $"p{ModelBinaryCacheVersions.CachePathPolicy}" +
            $"_r{backendResolution.ResolverPolicyVersion}" +
            $"_{backendResolution.RequestedPolicy.ToString().ToLowerInvariant()}" +
            $"_{backendResolution.CandidateListHash[..16]}";
        string originSegment = sourceIdentity.Origin.ToString();
        string variantRoot = Path.Combine(
            Path.GetFullPath(cacheRoot),
            "Models",
            $"v{ModelBinaryCacheVersions.Schema}",
            $"policy_{resolverKey}",
            $"opts_{variantFingerprint.Value}",
            originSegment);

        string normalizedExtension = assetExtension.Trim().TrimStart('.');
        if (normalizedExtension.Length == 0)
            throw new ArgumentException("The cache asset extension cannot be empty.", nameof(assetExtension));

        bool forceHash = sourceIdentity.Origin == ModelCacheSourceOrigin.External
            || string.IsNullOrWhiteSpace(sourceIdentity.RootRelativePath);
        string candidatePath = string.Empty;
        if (!forceHash)
        {
            candidatePath = BuildDisplayPath(
                variantRoot,
                sourceIdentity.RootRelativePath!,
                normalizedExtension,
                out bool hadUnsafeSourceSegment);
            forceHash = hadUnsafeSourceSegment
                || sourceIdentity.RootRelativePath!.Length > MaximumSourceRelativeLength
                || candidatePath.Length > MaximumPreferredPathLength;
        }

        string cachePath = forceHash
            ? BuildHashedPath(variantRoot, sourceIdentity, normalizedExtension)
            : candidatePath;
        return new ModelCachePathResolution(
            cachePath,
            forceHash,
            sourceIdentity,
            resolverKey,
            variantFingerprint);
    }

    private static string BuildDisplayPath(
        string variantRoot,
        string rootRelativePath,
        string assetExtension,
        out bool hadUnsafeSourceSegment)
    {
        string[] sourceSegments = rootRelativePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (sourceSegments.Length == 0)
        {
            hadUnsafeSourceSegment = true;
            return string.Empty;
        }

        hadUnsafeSourceSegment = false;
        string current = variantRoot;
        for (int index = 0; index < sourceSegments.Length - 1; index++)
        {
            string sanitized = SanitizeDisplaySegment(sourceSegments[index], out bool changed);
            hadUnsafeSourceSegment |= changed;
            current = Path.Combine(current, sanitized);
        }

        string sourceFileName = SanitizeDisplaySegment(sourceSegments[^1], out bool fileNameChanged);
        hadUnsafeSourceSegment |= fileNameChanged;
        return Path.Combine(current, $"{sourceFileName}.{assetExtension}");
    }

    private static string BuildHashedPath(
        string variantRoot,
        ModelCacheSourceIdentity sourceIdentity,
        string assetExtension)
    {
        string displayName = Path.GetFileNameWithoutExtension(
            sourceIdentity.CanonicalAbsolutePath.Replace('/', Path.DirectorySeparatorChar));
        string safeDisplayName = SanitizeDisplaySegment(displayName, out _);
        if (safeDisplayName.Length > 32)
            safeDisplayName = safeDisplayName[..32];
        if (string.IsNullOrWhiteSpace(safeDisplayName))
            safeDisplayName = "model";

        string hash = sourceIdentity.IdentityHash;
        return Path.Combine(
            variantRoot,
            "hashed",
            hash[..2],
            hash[2..4],
            $"{safeDisplayName}_{hash[..16]}.{assetExtension}");
    }

    private static string SanitizeDisplaySegment(string value, out bool changed)
    {
        string normalized = value.Normalize(NormalizationForm.FormC);
        StringBuilder builder = new(Math.Min(normalized.Length, MaximumDisplaySegmentLength));
        changed = false;

        foreach (char character in normalized)
        {
            bool invalid = character < ' '
                || InvalidDisplayCharacters.Contains(character)
                || Path.GetInvalidFileNameChars().Contains(character);
            if (invalid)
            {
                builder.Append('_');
                changed = true;
            }
            else
            {
                builder.Append(character);
            }
        }

        string result = builder.ToString().TrimEnd(' ', '.');
        changed |= result.Length != normalized.Length;
        if (result.Length > MaximumDisplaySegmentLength)
        {
            result = result[..MaximumDisplaySegmentLength];
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(result))
        {
            changed = true;
            return "_";
        }

        string deviceName = result.Split('.', 2)[0];
        if (IsReservedWindowsDeviceName(deviceName))
        {
            changed = true;
            result = $"_{result}";
        }

        return result;
    }

    private static bool IsReservedWindowsDeviceName(string value)
    {
        if (value.Equals("con", StringComparison.OrdinalIgnoreCase)
            || value.Equals("prn", StringComparison.OrdinalIgnoreCase)
            || value.Equals("aux", StringComparison.OrdinalIgnoreCase)
            || value.Equals("nul", StringComparison.OrdinalIgnoreCase))
            return true;

        return value.Length == 4
            && (value.StartsWith("com", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("lpt", StringComparison.OrdinalIgnoreCase))
            && value[3] is >= '1' and <= '9';
    }

    private static bool IsLowerHex(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
            return false;

        for (int index = 0; index < value.Length; index++)
            if (value[index] is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;

        return true;
    }
}
