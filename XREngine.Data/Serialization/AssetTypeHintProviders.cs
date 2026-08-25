using System.Diagnostics.CodeAnalysis;

namespace XREngine.Serialization;

/// <summary>Resolves legacy serializer type hints without making Data or Runtime.Core reference feature types.</summary>
public interface IAssetTypeHintProvider
{
    bool TryResolveLegacyRootKey(
        string rootKey,
        Type expectedType,
        [NotNullWhen(true)] out Type? assetType);
}

/// <summary>Lease-based registry for feature-owned legacy asset type hints.</summary>
public static class AssetTypeHintProviders
{
    private static readonly object Sync = new();
    private static IAssetTypeHintProvider[] _providers = [];

    public static IDisposable Install(IAssetTypeHintProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Sync)
        {
            if (Array.Exists(_providers, candidate => ReferenceEquals(candidate, provider)))
                throw new InvalidOperationException("The asset type hint provider instance is already installed.");
            Volatile.Write(ref _providers, [.. _providers, provider]);
        }

        return new ProviderLease(provider);
    }

    public static bool TryResolveLegacyRootKey(
        string? rootKey,
        Type expectedType,
        [NotNullWhen(true)] out Type? assetType)
    {
        assetType = null;
        if (string.IsNullOrWhiteSpace(rootKey))
            return false;

        foreach (IAssetTypeHintProvider provider in Volatile.Read(ref _providers))
        {
            if (provider.TryResolveLegacyRootKey(rootKey, expectedType, out assetType))
                return true;
        }

        return false;
    }

    private sealed class ProviderLease(IAssetTypeHintProvider provider) : IDisposable
    {
        private IAssetTypeHintProvider? _provider = provider;

        public void Dispose()
        {
            IAssetTypeHintProvider? current = Interlocked.Exchange(ref _provider, null);
            if (current is null)
                return;

            lock (Sync)
            {
                int index = Array.FindIndex(_providers, candidate => ReferenceEquals(candidate, current));
                if (index < 0)
                    return;

                IAssetTypeHintProvider[] updated = new IAssetTypeHintProvider[_providers.Length - 1];
                if (index > 0)
                    Array.Copy(_providers, 0, updated, 0, index);
                if (index < _providers.Length - 1)
                    Array.Copy(_providers, index + 1, updated, index, _providers.Length - index - 1);
                Volatile.Write(ref _providers, updated);
            }
        }
    }
}
