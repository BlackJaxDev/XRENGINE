using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine;

/// <summary>Installs Bootstrap-owned cooked serializers for application settings.</summary>
public static class BootstrapPublishedCookedAssetRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(RegisterMemoryPackAsset<GameStartupSettings>());
            leases.Add(RegisterMemoryPackAsset<EditorPreferences>());
        });

    private static IDisposable RegisterMemoryPackAsset<T>() where T : XRAsset
        => PublishedCookedAssetRegistry.Register(
            typeof(T),
            static asset => MemoryPackSerializer.Serialize((T)asset),
            static (payload, _) => MemoryPackSerializer.Deserialize<T>(payload),
            "XREngine.Runtime.Bootstrap");
}
