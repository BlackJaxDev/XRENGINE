using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine;

/// <summary>Installs cooked serializers for settings types that remain facade-owned until P6.4.</summary>
public static class FacadePublishedCookedAssetRegistration
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
            "XRENGINE facade settings");

}
