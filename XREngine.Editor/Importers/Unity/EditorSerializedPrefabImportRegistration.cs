using XREngine.Data;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;
using XREngine.Scene;

namespace XREngine.Editor.Importers.SerializedAssets;

/// <summary>Installs Editor-owned Unity prefab extension and loading behavior.</summary>
public static class EditorSerializedPrefabImportRegistration
{
    /// <summary>
    /// Registers the explicit <c>.prefab</c> descriptor and wraps the currently
    /// installed third-party loader. Disposing the lease restores both surfaces.
    /// </summary>
    public static IDisposable Install()
    {
        IRuntimeThirdPartyAssetLoadingServices fallback = RuntimeThirdPartyAssetLoadingServices.Current;
        return RegistrationLeaseGroup.Create(leases =>
        {
            leases.Add(ThirdPartyAssetTypeRegistry.Install(
                "XREngine.Editor.Unity",
                typeof(XRPrefabSource),
                typeof(ModelImportOptions),
                ["prefab"]));
            leases.Add(ThirdPartyAssetTypeRegistry.Install(
                "XREngine.Editor.Unity",
                typeof(XRScene),
                typeof(XRDefault3rdPartyImportOptions),
                ["unity"]));
            leases.Add(RuntimeThirdPartyAssetLoadingServices.Install(
                new EditorSerializedPrefabAssetLoadingServices(fallback)));
            leases.Add(ModelImportBackendRegistry.Default.Install(
                SerializedModelImportProducerAdapter.Descriptor));
        });
    }
}
