using System.Collections.ObjectModel;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable identity, ordering, and capability metadata for a model import backend.
/// </summary>
public sealed class ModelImportBackendDescriptor
{
    private readonly ReadOnlyCollection<string> _supportedExtensions;

    public ModelImportBackendDescriptor(
        string stableId,
        uint implementationVersion,
        IEnumerable<string> supportedExtensions,
        int priority,
        ModelImportBackendCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentNullException.ThrowIfNull(supportedExtensions);

        ValidateStableId(stableId);
        if (implementationVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(implementationVersion), "Backend implementation versions start at one.");

        string[] normalizedExtensions = supportedExtensions
            .Select(NormalizeExtension)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static extension => extension, StringComparer.Ordinal)
            .ToArray();

        if (normalizedExtensions.Length == 0
            && (capabilities & ModelImportBackendCapabilities.GeneralPurposeFallback) == 0)
            throw new ArgumentException("A backend must declare at least one supported extension.", nameof(supportedExtensions));

        StableId = stableId;
        ImplementationVersion = implementationVersion;
        Priority = priority;
        Capabilities = capabilities;
        _supportedExtensions = Array.AsReadOnly(normalizedExtensions);
    }

    /// <summary>
    /// Gets the culture-invariant persistent backend identity.
    /// </summary>
    public string StableId { get; }

    /// <summary>
    /// Gets the monotonic version of output-affecting backend behavior.
    /// </summary>
    public uint ImplementationVersion { get; }

    /// <summary>
    /// Gets the deterministic resolver priority. Higher values run first.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Gets the backend capability flags.
    /// </summary>
    public ModelImportBackendCapabilities Capabilities { get; }

    /// <summary>
    /// Gets normalized, ordinally sorted file extensions supported directly by the backend.
    /// </summary>
    public IReadOnlyList<string> SupportedExtensions => _supportedExtensions;

    /// <summary>
    /// Returns whether the backend is eligible for the supplied source extension.
    /// General-purpose fallback backends remain eligible for extensions not listed explicitly.
    /// </summary>
    public bool SupportsExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return (Capabilities & ModelImportBackendCapabilities.GeneralPurposeFallback) != 0;

        string normalizedExtension = NormalizeExtension(extension);
        return _supportedExtensions.Contains(normalizedExtension, StringComparer.Ordinal)
            || (Capabilities & ModelImportBackendCapabilities.GeneralPurposeFallback) != 0;
    }

    public override string ToString() => $"{StableId}@{ImplementationVersion}";

    internal static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        string normalized = extension.Trim();
        if (normalized[0] != '.')
            normalized = $".{normalized}";
        if (normalized.Length == 1)
            throw new ArgumentException("A file extension must contain at least one character after the period.", nameof(extension));

        return normalized.ToLowerInvariant();
    }

    private static void ValidateStableId(string stableId)
    {
        for (int i = 0; i < stableId.Length; i++)
        {
            char value = stableId[i];
            bool valid = value is >= 'a' and <= 'z'
                || value is >= '0' and <= '9'
                || value is '.' or '-' or '_';
            if (!valid)
                throw new ArgumentException(
                    "Backend stable IDs may contain only lowercase ASCII letters, digits, periods, hyphens, and underscores.",
                    nameof(stableId));
        }
    }
}
