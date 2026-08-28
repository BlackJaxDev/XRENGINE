using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using XREngine.Core.Files;

namespace XREngine.Data;

/// <summary>
/// Lease-based registry that maps third-party file extensions to the feature
/// assembly responsible for importing the corresponding asset type.
/// </summary>
public static class ThirdPartyAssetTypeRegistry
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, List<Registration>> Registrations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers every extension declared by <see cref="XR3rdPartyExtensionsAttribute"/>
    /// on <paramref name="assetType"/>. Duplicate format claims fail explicitly.
    /// </summary>
    public static IDisposable Install(string ownerName, Type assetType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        ArgumentNullException.ThrowIfNull(assetType);

        if (!typeof(XRAsset).IsAssignableFrom(assetType) || assetType.IsAbstract || assetType.IsInterface)
            throw new ArgumentException($"'{assetType.FullName}' is not a concrete {nameof(XRAsset)} type.", nameof(assetType));

        XR3rdPartyExtensionsAttribute? attribute = assetType.GetCustomAttribute<XR3rdPartyExtensionsAttribute>();
        if (attribute is null)
            return EmptyLease.Instance;

        return Install(
            ownerName,
            assetType,
            attribute.ImportOptionsType,
            attribute.Extensions.Select(static entry => entry.ext));
    }

    /// <summary>
    /// Registers an explicit feature-owned third-party descriptor. This overload lets a
    /// runtime-neutral asset type remain free of importer package and options-type references.
    /// </summary>
    public static IDisposable Install(
        string ownerName,
        Type assetType,
        Type? importOptionsType,
        IEnumerable<string> extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        ArgumentNullException.ThrowIfNull(assetType);
        ArgumentNullException.ThrowIfNull(extensions);

        if (!typeof(XRAsset).IsAssignableFrom(assetType) || assetType.IsAbstract || assetType.IsInterface)
            throw new ArgumentException($"'{assetType.FullName}' is not a concrete {nameof(XRAsset)} type.", nameof(assetType));

        Registration registration = new(ownerName, assetType, importOptionsType);
        List<string> normalizedExtensions = extensions
            .Select(Normalize)
            .Where(static extension => extension is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (Sync)
        {
            foreach (string normalized in normalizedExtensions)
            {
                if (Registrations.TryGetValue(normalized, out List<Registration>? existing)
                    && existing.Count > 0)
                {
                    Registration owner = existing[^1];
                    throw new InvalidOperationException(
                        $"Third-party extension '.{normalized}' is already owned by '{owner.OwnerName}' " +
                        $"for asset type '{owner.AssetType.FullName}'. '{ownerName}' cannot also claim it.");
                }
            }

            foreach (string normalized in normalizedExtensions)
            {
                Registrations.Add(normalized, [registration]);
            }
        }

        return normalizedExtensions.Count == 0
            ? EmptyLease.Instance
            : new RegistrationLease(registration, normalizedExtensions);
    }

    /// <summary>Resolves the active asset type and its owning feature for an extension.</summary>
    public static bool TryResolve(
        string extension,
        [NotNullWhen(true)] out Type? assetType,
        [NotNullWhen(true)] out string? ownerName)
    {
        string? normalized = Normalize(extension);
        if (normalized is not null)
        {
            lock (Sync)
            {
                if (Registrations.TryGetValue(normalized, out List<Registration>? claims) && claims.Count > 0)
                {
                    Registration registration = claims[^1];
                    assetType = registration.AssetType;
                    ownerName = registration.OwnerName;
                    return true;
                }
            }
        }

        assetType = null;
        ownerName = null;
        return false;
    }

    /// <summary>Resolves the complete active descriptor for an extension.</summary>
    public static bool TryResolve(
        string extension,
        [NotNullWhen(true)] out ThirdPartyAssetTypeDescriptor? descriptor)
    {
        string? normalized = Normalize(extension);
        if (normalized is not null)
        {
            lock (Sync)
            {
                if (Registrations.TryGetValue(normalized, out List<Registration>? claims) && claims.Count > 0)
                {
                    Registration registration = claims[^1];
                    descriptor = new ThirdPartyAssetTypeDescriptor(
                        registration.OwnerName,
                        registration.AssetType,
                        registration.ImportOptionsType,
                        normalized);
                    return true;
                }
            }
        }

        descriptor = null;
        return false;
    }

    private static string? Normalize(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        ReadOnlySpan<char> normalized = extension.AsSpan().Trim();
        while (!normalized.IsEmpty && normalized[0] == '.')
            normalized = normalized[1..];

        return normalized.IsEmpty ? null : normalized.ToString();
    }

    private sealed record Registration(string OwnerName, Type AssetType, Type? ImportOptionsType);

    private sealed class RegistrationLease(Registration registration, List<string> extensions) : IDisposable
    {
        private Registration? _registration = registration;

        public void Dispose()
        {
            Registration? installed = Interlocked.Exchange(ref _registration, null);
            if (installed is null)
                return;

            lock (Sync)
            {
                foreach (string extension in extensions)
                {
                    if (!Registrations.TryGetValue(extension, out List<Registration>? claims))
                        continue;

                    claims.Remove(installed);
                    if (claims.Count == 0)
                        Registrations.Remove(extension);
                }
            }
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>Active feature claim for one third-party extension.</summary>
public sealed record ThirdPartyAssetTypeDescriptor(
    string OwnerName,
    Type AssetType,
    Type? ImportOptionsType,
    string Extension);
