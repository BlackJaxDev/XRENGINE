using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>Lease-based installation point for runtime asset serialization policy.</summary>
public static class AssetSerializationServices
{
    private static readonly IAssetSerializationServices Default = new MissingServices();
    private static IAssetSerializationServices _current = Default;

    public static IAssetSerializationServices Current => Volatile.Read(ref _current);

    public static IDisposable Install(IAssetSerializationServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IAssetSerializationServices previous = Interlocked.Exchange(ref _current, services);
        return new InstallationLease(services, previous);
    }

    private sealed class InstallationLease(
        IAssetSerializationServices installed,
        IAssetSerializationServices previous) : IDisposable
    {
        private IAssetSerializationServices? _installed = installed;

        public void Dispose()
        {
            IAssetSerializationServices? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }

    private sealed class MissingServices : IAssetSerializationServices
    {
        public string AssetExtension => "asset";
        public string? GameAssetsPath => null;
        public string? EngineAssetsPath => null;
        public string? CurrentDeserializationPath => null;
        public IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => [];

        public void EnsureYamlAssetRuntimeSupported(string? path = null)
        {
        }

        public bool TryGetAssetById(Guid assetId, [NotNullWhen(true)] out XRAsset? asset)
        {
            asset = null;
            return false;
        }

        public bool TryResolveAssetPathById(
            Guid assetId,
            string? referenceAssetPath,
            [NotNullWhen(true)] out string? assetPath)
        {
            assetPath = null;
            return false;
        }

        public bool TryCreatePortableAssetReference(
            string assetPath,
            [NotNullWhen(true)] out string? reference)
        {
            reference = null;
            return false;
        }

        public XRAsset? LoadImmediate(string assetPath, Type assetType)
            => throw MissingRegistration(assetPath);

        public bool TryDeferAssetLoad(string assetPath, Type assetType, out XRAsset? asset)
        {
            asset = null;
            return false;
        }

        public bool TryHandleScalarAsset(
            IParser reader,
            Type expectedType,
            Scalar scalar,
            out object? value)
        {
            value = null;
            return false;
        }

        private static InvalidOperationException MissingRegistration(string? assetPath)
            => new(
                $"Asset serialization runtime owner '{nameof(IAssetSerializationServices)}' is not installed" +
                (string.IsNullOrWhiteSpace(assetPath) ? "." : $" for asset '{assetPath}'."));
    }
}
