using System.Diagnostics.CodeAnalysis;
using XREngine.Data;
using XREngine.Scene.Prefabs;
using XREngine.Serialization;

namespace XREngine;

/// <summary>
/// Installs compatibility registrations for settings and prefab types that remain facade-owned
/// until their dedicated Phase 6 slices.
/// </summary>
public static class FacadeAssetSerializationRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(FacadePublishedCookedAssetRegistration.Install());
            leases.Add(AssetTypeHintProviders.Install(new FacadeAssetTypeHintProvider()));
            leases.Add(YamlEnumAliasRegistry.Install(
                "XRENGINE facade settings",
                "JobSystem",
                EDebugShapePopulationMode.Tasks));
        });

    private sealed class FacadeAssetTypeHintProvider : IAssetTypeHintProvider
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
