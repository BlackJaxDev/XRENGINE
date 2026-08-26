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
    /// on <paramref name="assetType"/>. Later registrations take precedence until
    /// their lease is disposed.
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

        Registration registration = new(ownerName, assetType);
        List<string> extensions = new(attribute.Extensions.Length);

        lock (Sync)
        {
            foreach ((string extension, _) in attribute.Extensions)
            {
                string? normalized = Normalize(extension);
                if (normalized is null || extensions.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (!Registrations.TryGetValue(normalized, out List<Registration>? claims))
                {
                    claims = [];
                    Registrations.Add(normalized, claims);
                }

                claims.Add(registration);
                extensions.Add(normalized);
            }
        }

        return extensions.Count == 0
            ? EmptyLease.Instance
            : new RegistrationLease(registration, extensions);
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

    private static string? Normalize(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        ReadOnlySpan<char> normalized = extension.AsSpan().Trim();
        while (!normalized.IsEmpty && normalized[0] == '.')
            normalized = normalized[1..];

        return normalized.IsEmpty ? null : normalized.ToString();
    }

    private sealed record Registration(string OwnerName, Type AssetType);

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
