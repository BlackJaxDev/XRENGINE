using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Serialization;

namespace XREngine.Serialization;

/// <summary>
/// Installs Runtime.Core-owned asset services, scene import identity, and transform YAML support.
/// </summary>
public static class RuntimeCoreAssetSerializationRegistration
{
    public static IDisposable Install(IAssetSerializationServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return RegistrationLeaseGroup.Create(leases =>
        {
            leases.Add(AssetSerializationServices.Install(new RuntimeAssetSerializationServices(services)));
            leases.Add(CookedBinaryObjectLifecycleServices.Install(RuntimeCookedBinaryObjectLifecycleServices.Instance));
            leases.Add(YamlSerializationContributions.Install(new RuntimeCoreYamlContribution()));
            leases.Add(AssetTypeHintProviders.Install(new RuntimeCoreAssetTypeHintProvider()));
        });
    }

    private sealed class RuntimeCoreYamlContribution : IYamlSerializationContribution
    {
        public string OwnerName => "XREngine.Runtime.Core";

        public IEnumerable<IYamlTypeConverter> CreateTypeConverters()
            => [new TransformBaseYamlTypeConverter()];
    }

    private sealed class RuntimeCoreAssetTypeHintProvider : IAssetTypeHintProvider
    {
        public bool TryResolveLegacyRootKey(
            string rootKey,
            Type expectedType,
            [NotNullWhen(true)] out Type? assetType)
        {
            Type? candidate = string.Equals(rootKey, nameof(XRPrefabSource.RootNode), StringComparison.Ordinal)
                ? typeof(XRPrefabSource)
                : null;
            assetType = candidate;
            return candidate is not null && expectedType.IsAssignableFrom(candidate);
        }
    }

}
